using FileAccessMcpServer.Models;

namespace FileAccessMcpServer.Core
{
    /// <summary>
    /// Проверяет корректность конфигурационных опций (<see cref="AppOptions"/>) при старте
    /// и при перезагрузке конфигурации. При невалидных параметрах приложение завершает работу.
    /// </summary>
    internal static class ConfigValidator
    {
        private static readonly string[] AllowedHttpSessionModes =
        {
            "stateful",
            "stateless",
            "statefulForInitializeClients"
        };

        /// <summary>
        /// Выполняет валидацию конфигурации и возвращает список ошибок.
        /// Пустой список означает, что конфигурация корректна.
        /// </summary>
        /// <param name="options">Проверяемые конфигурационные опции.</param>
        /// <returns>Список описаний ошибок (пустой — если ошибок нет).</returns>
        public static List<string> Validate(AppOptions options)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(options.SandboxDir))
            {
                errors.Add($"'{nameof(AppOptions.SandboxDir)}' is required and cannot be empty.");
            }
            else
            {
                if (!Path.IsPathRooted(options.SandboxDir))
                    errors.Add($"'{nameof(AppOptions.SandboxDir)}' must be an absolute path. Current value: '{options.SandboxDir}'.");
                else if (File.Exists(options.SandboxDir))
                    errors.Add($"'{nameof(AppOptions.SandboxDir)}' points to an existing file, expected a directory. Current value: '{options.SandboxDir}'.");
            }

            if (!string.IsNullOrWhiteSpace(options.BackupDir))
            {
                if (!Path.IsPathRooted(options.BackupDir))
                    errors.Add($"'{nameof(AppOptions.BackupDir)}' must be an absolute path. Current value: '{options.BackupDir}'.");
                else if (File.Exists(options.BackupDir))
                    errors.Add($"'{nameof(AppOptions.BackupDir)}' points to an existing file, expected a directory. Current value: '{options.BackupDir}'.");
            }

            if (options.MaxOneFileSizeInBytes <= 0)
                errors.Add($"'{nameof(AppOptions.MaxOneFileSizeInBytes)}' must be a positive integer. Current value: {options.MaxOneFileSizeInBytes}.");

            if (options.MaxQuantityFiles <= 0)
                errors.Add($"'{nameof(AppOptions.MaxQuantityFiles)}' must be a positive integer. Current value: {options.MaxQuantityFiles}.");

            if (options.MaxTotalSizeInBytes <= 0)
                errors.Add($"'{nameof(AppOptions.MaxTotalSizeInBytes)}' must be a positive integer. Current value: {options.MaxTotalSizeInBytes}.");

            if (options.MaxTreeEntries <= 0)
                errors.Add($"'{nameof(AppOptions.MaxTreeEntries)}' must be a positive integer. Current value: {options.MaxTreeEntries}.");

            if (options.MaxSearchResults <= 0)
                errors.Add($"'{nameof(AppOptions.MaxSearchResults)}' must be a positive integer. Current value: {options.MaxSearchResults}.");

            if (options.MaxTotalSizeInBytes < options.MaxOneFileSizeInBytes)
                errors.Add($"'{nameof(AppOptions.MaxTotalSizeInBytes)}' ({options.MaxTotalSizeInBytes}) cannot be less than '{nameof(AppOptions.MaxOneFileSizeInBytes)}' ({options.MaxOneFileSizeInBytes}).");

            if (options.RateLimit is not null && options.RateLimit.Enabled)
            {
                if (options.RateLimit.WindowInSeconds <= 0)
                    errors.Add($"'{nameof(RateLimitOptions)}.{nameof(RateLimitOptions.WindowInSeconds)}' must be a positive integer. Current value: {options.RateLimit.WindowInSeconds}.");

                if (options.RateLimit.PermitLimit <= 0)
                    errors.Add($"'{nameof(RateLimitOptions)}.{nameof(RateLimitOptions.PermitLimit)}' must be a positive integer. Current value: {options.RateLimit.PermitLimit}.");

                if (options.RateLimit.QueueLimit < 0)
                    errors.Add($"'{nameof(RateLimitOptions)}.{nameof(RateLimitOptions.QueueLimit)}' cannot be negative. Current value: {options.RateLimit.QueueLimit}.");
            }

            if (!string.IsNullOrWhiteSpace(options.HttpSessionMode) &&
                !AllowedHttpSessionModes.Contains(options.HttpSessionMode.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"'{nameof(AppOptions.HttpSessionMode)}' must be one of: {string.Join(", ", AllowedHttpSessionModes)}. Current value: '{options.HttpSessionMode}'.");
            }

            return errors;
        }
    }
}
