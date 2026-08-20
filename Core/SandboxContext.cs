using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

using Microsoft.Extensions.Options;

namespace FileAccessMcpServer.Core
{
    /// <summary>
    /// Определяет корень песочницы и каталог резервных копий из серверной конфигурации
    /// (config.json, секция FileAccess: SandboxDir, BackupDir).
    /// Использует <see cref="IOptionsMonitor{T}"/>, поэтому изменения конфигурации
    /// применяются без перезапуска сервиса.
    /// </summary>
    internal class SandboxContext : ISandboxContext
    {
        private readonly IOptionsMonitor<AppOptions> _optionsMonitor;

        /// <summary>
        /// Инициализирует экземпляр класса SandboxContext.
        /// </summary>
        /// <param name="optionsMonitor">Монитор конфигурационных опций песочницы (SandboxDir, BackupDir).</param>
        public SandboxContext(IOptionsMonitor<AppOptions> optionsMonitor)
        {
            _optionsMonitor = optionsMonitor;
        }

        /// <summary>
        /// Возвращает корневой каталог песочницы из конфигурации (SandboxDir).
        /// </summary>
        public string? SandboxDir
        {
            get
            {
                var value = _optionsMonitor.CurrentValue.SandboxDir;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        /// <summary>
        /// Возвращает каталог для резервных копий из конфигурации (BackupDir).
        /// </summary>
        public string? BackupDir
        {
            get
            {
                var value = _optionsMonitor.CurrentValue.BackupDir;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
    }
}
