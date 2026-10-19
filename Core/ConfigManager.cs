using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using FileAccessMcpServer.Models;

namespace FileAccessMcpServer.Core
{
    /// <summary>
    /// Отвечает за расположение файла конфигурации и генерацию конфига по умолчанию.
    /// </summary>
    internal static class ConfigManager
    {
        /// <summary>
        /// Имя файла конфигурации по умолчанию.
        /// </summary>
        public const string DefaultConfigFileName = "config.json";

        /// <summary>
        /// Имя аргумента командной строки для указания пути к файлу конфигурации.
        /// </summary>
        public const string ConfigArgName = "--config";

        /// <summary>
        /// Имя аргумента командной строки для генерации конфига по умолчанию.
        /// </summary>
        public const string GenerateConfigArgName = "--generate-config";

        /// <summary>
        /// Сокращённый алиас аргумента <see cref="GenerateConfigArgName"/>.
        /// </summary>
        public const string InitArgName = "--init";

        /// <summary>
        /// Имя аргумента командной строки для вывода справки.
        /// </summary>
        public const string HelpArgName = "--help";

        /// <summary>
        /// Сокращённый алиас аргумента <see cref="HelpArgName"/>.
        /// </summary>
        public const string HelpArgShortName = "-h";

        /// <summary>
        /// Извлекает путь к файлу конфигурации из аргументов командной строки.
        /// Поддерживаются форматы "--config path" и "--config=path".
        /// </summary>
        /// <param name="args">Аргументы командной строки.</param>
        /// <returns>Путь к файлу конфигурации или <c>null</c>, если аргумент не задан.</returns>
        public static string? TryGetConfigPathFromArgs(string[] args)
        {
            if (args == null)
                return null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.StartsWith(ConfigArgName + "=", StringComparison.OrdinalIgnoreCase))
                    return arg[(ConfigArgName.Length + 1)..].Trim();

                if (string.Equals(arg, ConfigArgName, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        return args[i + 1].Trim();
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Проверяет, запрошена ли генерация конфига по умолчанию.
        /// </summary>
        /// <param name="args">Аргументы командной строки.</param>
        /// <returns><c>true</c>, если задан <c>--generate-config</c> или <c>--init</c>.</returns>
        public static bool IsGenerateConfigRequested(string[] args)
        {
            return ContainsArg(args, GenerateConfigArgName) || ContainsArg(args, InitArgName);
        }

        /// <summary>
        /// Проверяет, запрошена ли справка.
        /// </summary>
        /// <param name="args">Аргументы командной строки.</param>
        /// <returns><c>true</c>, если задан <c>--help</c> или <c>-h</c>.</returns>
        public static bool IsHelpRequested(string[] args)
        {
            return ContainsArg(args, HelpArgName) || ContainsArg(args, HelpArgShortName);
        }

        /// <summary>
        /// Разрешает полный путь к файлу конфигурации.
        /// Приоритет: явный аргумент --config, затем config.json в текущем каталоге,
        /// затем config.json в каталоге приложения.
        /// </summary>
        /// <param name="args">Аргументы командной строки.</param>
        /// <returns>Полный путь к файлу конфигурации.</returns>
        public static string ResolveConfigPath(string[] args)
        {
            string? explicitPath = TryGetConfigPathFromArgs(args);
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return Path.GetFullPath(explicitPath);

            string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), DefaultConfigFileName);
            if (File.Exists(cwdPath))
                return cwdPath;

            string baseDirPath = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
            if (File.Exists(baseDirPath))
                return baseDirPath;

            return cwdPath;
        }

        /// <summary>
        /// Генерирует файл конфигурации по умолчанию, если его нет.
        /// Существующий файл не перезаписывается.
        /// </summary>
        /// <param name="configPath">Полный путь к файлу конфигурации.</param>
        /// <param name="error">Сообщение об ошибке (null при успехе).</param>
        /// <returns><c>true</c>, если конфиг создан; <c>false</c>, если файл уже существует или произошла ошибка.</returns>
        public static bool TryGenerateDefaultConfig(string configPath, out string? error)
        {
            if (File.Exists(configPath))
            {
                error = $"Config file already exists: '{configPath}'. Refusing to overwrite. Delete it first to regenerate.";
                return false;
            }

            try
            {
                string? configDir = Path.GetDirectoryName(configPath);
                if (string.IsNullOrEmpty(configDir))
                    configDir = Directory.GetCurrentDirectory();

                Directory.CreateDirectory(configDir);

                var defaults = new Dictionary<string, object?>
                {
                    [AppOptions.SectionName] = new Dictionary<string, object?>
                    {
                        ["SandboxDir"] = Path.Combine(configDir, "sandbox"),
                        ["BackupDir"] = Path.Combine(configDir, "backup"),
                        ["MaxOneFileSizeInBytes"] = 4L * Consts.Megabyte,
                        ["MaxQuantityFiles"] = 5,
                        ["MaxTotalSizeInBytes"] = 10L * Consts.Megabyte,
                        ["MaxTreeEntries"] = 2000,
                        ["MaxSearchResults"] = Consts.DefaultMaxSearchResults,
                        ["ApiKey"] = string.Empty,
                        ["HttpSessionMode"] = "statefulForInitializeClients",
                        ["RateLimit"] = new Dictionary<string, object?>
                        {
                            ["Enabled"] = true,
                            ["WindowInSeconds"] = Consts.DefaultRateLimitWindowInSeconds,
                            ["PermitLimit"] = Consts.DefaultRateLimitPermitLimit,
                            ["QueueLimit"] = 0
                        }
                    }
                };

                string json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                // BOM (UTF-8 with signature) — для корректного отображения в Windows-редакторах.
                File.WriteAllText(configPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to write config file '{configPath}': {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Выводит справку по аргументам командной строки.
        /// </summary>
        public static void PrintUsage()
        {
            Console.WriteLine("FileAccessMcpServer — MCP-сервер безопасной работы с файлами в песочнице");
            Console.WriteLine();
            Console.WriteLine("Использование:");
            Console.WriteLine($"  {Path.GetFileName(Environment.ProcessPath)} [--stdio|--http] [--config <path>] [--generate-config|--init] [--help|-h]");
            Console.WriteLine();
            Console.WriteLine("Аргументы:");
            Console.WriteLine("  --stdio             Работать через stdio (клиент запускает процесс).");
            Console.WriteLine("  --http              Работать через Streamable HTTP (по умолчанию).");
            Console.WriteLine("                      Также поддерживается переменная окружения MCP_TRANSPORT=stdio.");
            Console.WriteLine($"  {ConfigArgName} <path>      Путь к файлу конфигурации (по умолчанию config.json в рабочем каталоге).");
            Console.WriteLine($"  {GenerateConfigArgName}    Сгенерировать конфиг по умолчанию, если файла нет, и выйти.");
            Console.WriteLine($"  {InitArgName}              Алиас для --generate-config.");
            Console.WriteLine($"  {HelpArgName}, -h           Показать эту справку и выйти.");
            Console.WriteLine();
            Console.WriteLine("Конфигурация читается из секции \"FileAccess\". Изменения файла применяются без перезапуска.");
            Console.WriteLine("При некорректных параметрах конфигурации приложение завершает работу с ненулевым кодом.");
        }

        private static bool ContainsArg(string[] args, string name)
        {
            if (args == null)
                return false;

            return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
