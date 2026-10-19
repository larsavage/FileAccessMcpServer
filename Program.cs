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

using ModelContextProtocol.AspNetCore;

internal static class Program
{
    private const string StdioTransportArg = "--stdio";
    private const string HttpTransportArg = "--http";
    private const string TransportEnvVar = "MCP_TRANSPORT";

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

        // Выбор транспорта: --stdio/--http или переменная окружения MCP_TRANSPORT=stdio.
        // По умолчанию — Streamable HTTP.
        return ShouldUseStdioTransport(args)
            ? await RunStdioAsync(configPath)
            : await RunHttpAsync(args, configPath);
    }

    /// <summary>
    /// Определяет, должен ли сервер работать через stdio (иначе — через Streamable HTTP).
    /// Приоритет: аргумент <c>--http</c>, затем <c>--stdio</c>, затем переменная окружения <c>MCP_TRANSPORT</c>.
    /// </summary>
    private static bool ShouldUseStdioTransport(string[] args)
    {
        if (args.Any(a => string.Equals(a, HttpTransportArg, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (args.Any(a => string.Equals(a, StdioTransportArg, StringComparison.OrdinalIgnoreCase)))
            return true;

        string? transport = Environment.GetEnvironmentVariable(TransportEnvVar);
        return string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Общая настройка конфигурации, валидации и сервисов для обоих транспортов.
    /// </summary>
    /// <returns>Проверенные конфигурационные опции.</returns>
    private static AppOptions ConfigureCommonServices(IHostApplicationBuilder builder, string configPath)
    {
        // reloadOnChange: true — изменения config.json применяются без перезапуска сервиса.
        builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);

        // Параметры конфигурации можно переопределять переменными окружения:
        // FILEACCESS__APIKEY, FILEACCESS__SANDBOXDIR, FILEACCESS__RATELIMIT__ENABLED и т.п.
        // Провайдер добавляется ПОСЛЕ config.json, поэтому ENV имеет приоритет.
        builder.Configuration.AddEnvironmentVariables();
        builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));

        var options = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
        var validationErrors = ConfigValidator.Validate(options);
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
        return options;
    }

    /// <summary>
    /// Регистрирует инструменты MCP (файлы, директории, health).
    /// </summary>
    private static void AddMcpTools(IMcpServerBuilder mcpBuilder)
    {
        mcpBuilder
            .WithTools<FileTools>()
            .WithTools<DirTools>()
            .WithTools<HealthTools>();
    }

    /// <summary>
    /// Запускает сервер через stdio-транспорт (для клиентов, запускающих процесс как дочерний: LM Studio, llama.cpp и т.д.).
    /// </summary>
    private static async Task<int> RunStdioAsync(string configPath)
    {
        var builder = Host.CreateApplicationBuilder();

        // ВАЖНО: в stdio-режиме stdout — это канал протокола MCP. Любой лог в stdout сломает протокол,
        // поэтому все логи направляются в stderr через LogToStandardErrorThreshold.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        var appOptions = ConfigureCommonServices(builder, configPath);

        var mcpBuilder = builder.Services.AddMcpServer();
        AddMcpTools(mcpBuilder);
        mcpBuilder.WithStdioServerTransport();

        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(Consts.DefaultShutdownTimeoutSeconds));

        var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        EnsureDirectories(appOptions, logger);
        RegisterReloadValidation(host.Services, logger, onInvalidConfig: null);

        if (string.IsNullOrWhiteSpace(appOptions.ApiKey))
            logger.LogWarning("ApiKey is ignored in stdio mode (stdio has no HTTP authentication).");
        logger.LogInformation("FileAccessMcpServer started in stdio mode. SandboxDir='{SandboxDir}', BackupDir='{BackupDir}'", appOptions.SandboxDir, appOptions.BackupDir);

        await host.RunAsync();
        return 0;
    }

    /// <summary>
    /// Запускает сервер через современный Streamable HTTP (ASP.NET Core).
    /// </summary>
    private static async Task<int> RunHttpAsync(string[] args, string configPath)
    {
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

        var appOptions = ConfigureCommonServices(builder, configPath);

        var mcpBuilder = builder.Services.AddMcpServer();
        AddMcpTools(mcpBuilder);
        mcpBuilder.WithHttpTransport(o =>
        {
            // Stateful (по умолчанию) нужен для server→client запросов (elicitation) по HTTP.
            o.SessionMode = ParseHttpSessionMode(appOptions.HttpSessionMode);
        });

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
                var currentOptions = httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<AppOptions>>()
                    .CurrentValue;

                RateLimitOptions? rateLimit = currentOptions.RateLimit;
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
        int exitCode = 0;
        Stopwatch? shutdownStopwatch = null;

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (string.IsNullOrWhiteSpace(appOptions.ApiKey))
                app.Logger.LogWarning("ApiKey is empty — running without authentication");
            app.Logger.LogInformation("Application started (Streamable HTTP, session mode={SessionMode}). SandboxDir='{SandboxDir}', BackupDir='{BackupDir}'", appOptions.HttpSessionMode, appOptions.SandboxDir, appOptions.BackupDir);
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
        RegisterReloadValidation(app.Services, app.Logger, onInvalidConfig: () =>
        {
            Interlocked.Exchange(ref exitCode, 1);
            app.Lifetime.StopApplication();
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
    /// Преобразует строковое значение конфигурации в <see cref="HttpServerSessionMode"/>.
    /// Неизвестные значения трактуются как <see cref="HttpServerSessionMode.Stateful"/>.
    /// </summary>
    private static HttpServerSessionMode ParseHttpSessionMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "stateless" => HttpServerSessionMode.Stateless,
            "statefulforinitializeclients" => HttpServerSessionMode.StatefulForInitializeClients,
            _ => HttpServerSessionMode.Stateful
        };
    }

    /// <summary>
    /// Создаёт каталоги песочницы и резервных копий, если они отсутствуют.
    /// </summary>
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

    /// <summary>
    /// Подписывается на перезагрузку конфигурации. При некорректных параметрах вызывается
    /// <paramref name="onInvalidConfig"/> (для HTTP — graceful shutdown); для stdio дополнительных
    /// действий не требуется.
    /// </summary>
    private static void RegisterReloadValidation(IServiceProvider services, ILogger logger, Action? onInvalidConfig)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<AppOptions>>();
        optionsMonitor.OnChange(newOptions =>
        {
            var errors = ConfigValidator.Validate(newOptions);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    logger.LogError("Configuration reload rejected: {Error}", error);

                if (onInvalidConfig is not null)
                {
                    logger.LogCritical("Invalid configuration after reload. Performing graceful shutdown.");
                    onInvalidConfig();
                }

                return;
            }

            EnsureDirectories(newOptions, logger);
            logger.LogInformation("Configuration reloaded successfully.");
        });
    }
}
