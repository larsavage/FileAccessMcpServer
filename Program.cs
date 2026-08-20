using System.Diagnostics;
using System.Text;
using System.Threading.RateLimiting;

using FileAccessMcpServer.Core;
using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;
using FileAccessMcpServer.Services;
using FileAccessMcpServer.Tools;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        // Регистрация кодовых страниц (windows-1251, cp866 и др.):
        // без этого Encoding.GetEncoding("windows-1251") не находит кодировку и код молча падает на UTF-8.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Корректный вывод русского текста в консоль и в перенаправленные потоки (stderr/stdout)
        // независимо от системной кодовой страницы.
        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8NoBom) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8NoBom) { AutoFlush = true });
        }
        catch (IOException) { /* консоль недоступна — вывод не критичен */ }
        catch (UnauthorizedAccessException) { /* нет прав на установку кодировки — продолжаем */ }

        if (ConfigManager.IsHelpRequested(args))
        {
            ConfigManager.PrintUsage();
            return 0;
        }

        string configPath = ConfigManager.ResolveConfigPath(args);

        // Генерация конфига по умолчанию по явному запросу, если файла нет.
        if (ConfigManager.IsGenerateConfigRequested(args))
        {
            if (ConfigManager.TryGenerateDefaultConfig(configPath, out string? generateError))
            {
                Console.WriteLine($"Default config generated: '{configPath}'");
                Console.WriteLine("Edit it (SandboxDir/BackupDir) and run the server again.");
                return 0;
            }

            Console.Error.WriteLine($"Failed to generate config: {generateError}");
            Environment.Exit(1);
        }

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Config file not found: '{configPath}'.");
            Console.Error.WriteLine($"Create it manually or run with '--generate-config' to create a default config.");
            Environment.Exit(1);
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        // JSON-логирование (одна JSON-запись на строку) — корректно обрабатывается
        // контейнерными рантаймами и системами агрегации логов (Loki/ELK и т.п.).
        builder.Logging.AddJsonConsole(o => o.UseUtcTimestamp = true);

        // Debug и выше — в stderr (остальное в stdout); временные метки — UTC в формате ISO 8601.
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Debug);
        builder.Services.Configure<JsonConsoleFormatterOptions>(o =>
        {
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });

        // reloadOnChange: true — изменения config.json применяются без перезапуска сервиса.
        builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);

        // Параметры конфигурации можно переопределять переменными окружения:
        // FILEACCESS__APIKEY, FILEACCESS__SANDBOXDIR, FILEACCESS__RATELIMIT__ENABLED и т.п.
        // («__» соответствует разделителю «:» в конфигурации, регистр не важен).
        // Провайдер добавляется ПОСЛЕ config.json, поэтому ENV имеет приоритет —
        // это удобно для k8s (ConfigMap/Secret) и Docker. Переменные читаются при старте (без hot reload).
        builder.Configuration.AddEnvironmentVariables();
        builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));

        // Валидация конфигурации при старте: при некорректных параметрах завершаем работу приложения.
        var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
        var validationErrors = ConfigValidator.Validate(appOptions);
        if (validationErrors.Count > 0)
        {
            Console.Error.WriteLine("Invalid configuration. Application will be terminated.");
            foreach (var error in validationErrors)
                Console.Error.WriteLine($"  - {error}");
            Console.Error.WriteLine($"Fix '{configPath}' or regenerate it with '--generate-config'.");
            Environment.Exit(1);
        }

        builder.Services.AddScoped<ISandboxContext, SandboxContext>();
        builder.Services.AddScoped<IBackupOperations, BackupService>();
        builder.Services.AddScoped<PathValidator, PathValidator>();

        builder.Services.AddTransient<IFileOperations, FileService>();
        builder.Services.AddTransient<IDirectoryOperations, DirService>();
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<FileTools>()
            .WithTools<DirTools>()
            .WithTools<HealthTools>();

        // Таймаут graceful shutdown: сколько хост ждёт завершения in-flight запросов
        // после сигнала остановки, прежде чем принудительно завершиться.
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(Consts.DefaultShutdownTimeoutSeconds));

        // Эндпоинты /healthz (/readyz) для k8s liveness/readiness и обратных прокси.
        builder.Services.AddHealthChecks();

        // Rate limiting (защита от floods/DDoS на уровне приложения).
        // Фиксированное окно на клиентский IP; значения читаются из IOptionsMonitor,
        // поэтому изменения конфигурации применяются без перезапуска сервиса.
        builder.Services.AddRateLimiter(limiterOptions =>
        {
            limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiterOptions.OnRejected = (context, _) =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RateLimiter");

                logger.LogWarning(
                    "Request rejected by rate limit. RemoteIp='{RemoteIp}' Path='{Path}' Endpoint='{Endpoint}'",
                    context.HttpContext.Connection.RemoteIpAddress,
                    context.HttpContext.Request.Path,
                    context.HttpContext.GetEndpoint()?.DisplayName ?? "(none)");

                return ValueTask.CompletedTask;
            };

            limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var appOptions = httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<AppOptions>>()
                    .CurrentValue;

                RateLimitOptions? rateLimit = appOptions.RateLimit;
                if (rateLimit is null || !rateLimit.Enabled)
                    return RateLimitPartition.GetNoLimiter<string>(string.Empty);

                // Ключ включает не только IP, но и параметры лимита: при их изменении
                // создаётся новый рейт-лимитер — hot reload работает без перезапуска.
                string clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                string partitionKey = $"{clientIp}|{rateLimit.PermitLimit}|{rateLimit.WindowInSeconds}|{rateLimit.QueueLimit}";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimit.PermitLimit,
                    Window = TimeSpan.FromSeconds(rateLimit.WindowInSeconds),
                    QueueLimit = rateLimit.QueueLimit,
                    AutoReplenishment = true
                });
            });
        });

        var app = builder.Build();

        // Graceful shutdown через IHostApplicationLifetime (app.Lifetime).
        // Хост вызывает ApplicationStopping → завершает in-flight запросы (таймаут
        // HostOptions.ShutdownTimeout) → вызывает ApplicationStopped.
        int exitCode = 0;

        Stopwatch? shutdownStopwatch = null;

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (string.IsNullOrWhiteSpace(appOptions.ApiKey))
                app.Logger.LogWarning("ApiKey is empty — running without authentication");
            app.Logger.LogInformation("Application started. SandboxDir='{SandboxDir}', BackupDir='{BackupDir}'", appOptions.SandboxDir, appOptions.BackupDir);
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            shutdownStopwatch = Stopwatch.StartNew();
            app.Logger.LogInformation("Graceful shutdown requested. Waiting up to {ShutdownTimeout} s for in-flight requests to complete...", Consts.DefaultShutdownTimeoutSeconds);
        });

        app.Lifetime.ApplicationStopped.Register(() =>
            app.Logger.LogInformation("Application stopped. Shutdown took {ElapsedMs} ms.", shutdownStopwatch?.ElapsedMilliseconds ?? 0));

        // Обеспечиваем существование каталогов песочницы и резервных копий.
        EnsureDirectories(appOptions, app.Logger);

        // Валидация при перезагрузке конфигурации: при некорректных параметрах выполняется
        // graceful shutdown через StopApplication(): in-flight запросы успевают завершиться,
        // код возврата процесса — 1.
        var optionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<AppOptions>>();
        optionsMonitor.OnChange(newOptions =>
        {
            var errors = ConfigValidator.Validate(newOptions);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    app.Logger.LogError("Configuration reload rejected: {Error}", error);
                app.Logger.LogCritical("Invalid configuration after reload. Performing graceful shutdown.");
                Interlocked.Exchange(ref exitCode, 1);
                app.Lifetime.StopApplication();
                return;
            }

            EnsureDirectories(newOptions, app.Logger);
            app.Logger.LogInformation("Configuration reloaded successfully.");
        });

        // API-ключ: если задан FileAccess:ApiKey, каждый запрос обязан содержать корректный X-Api-Key.
        // Эндпоинты /healthz и /readyz из проверки исключены (см. ApiKeyMiddleware).
        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseRateLimiter();
        app.MapHealthChecks("/healthz");
        app.MapHealthChecks("/readyz");
        app.MapMcp();
        await app.RunAsync();
        return Volatile.Read(ref exitCode);
    }

    /// <summary>
    /// Создаёт каталоги песочницы и резервных копий, если они отсутствуют.
    /// </summary>
    /// <param name="options">Конфигурационные опции (SandboxDir, BackupDir).</param>
    /// <param name="logger">Логгер для записи событий.</param>
    private static void EnsureDirectories(AppOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.SandboxDir) && !Directory.Exists(options.SandboxDir))
        {
            Directory.CreateDirectory(options.SandboxDir);
            logger.LogInformation("Sandbox directory created. Path='{SandboxDir}'", options.SandboxDir);
        }

        if (!string.IsNullOrWhiteSpace(options.BackupDir) && !Directory.Exists(options.BackupDir))
        {
            Directory.CreateDirectory(options.BackupDir);
            logger.LogInformation("Backup directory created. Path='{BackupDir}'", options.BackupDir);
        }
    }
}
