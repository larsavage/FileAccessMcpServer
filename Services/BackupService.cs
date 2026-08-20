using FileAccessMcpServer.Core;
using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

using Microsoft.Extensions.Logging;

namespace FileAccessMcpServer.Services
{
    /// <summary>
    /// Реализует резервное копирование файлов перед их перезаписью/удалением,
    /// а также восстановление из бэкапа и его удаление (откат при прерванной операции).
    /// </summary>
    internal class BackupService : IBackupOperations
    {
        private readonly ISandboxContext _sandboxContext;
        private readonly ILogger<BackupService> _logger;

        /// <summary>
        /// Инициализирует экземпляр класса BackupService.
        /// </summary>
        /// <param name="sandboxContext">Контекст песочницы (каталоги SandboxDir, BackupDir из конфигурации).</param>
        /// <param name="logger">Логгер для записи событий.</param>
        public BackupService(ISandboxContext sandboxContext, ILogger<BackupService> logger)
        {
            _sandboxContext = sandboxContext;
            _logger = logger;
        }

        /// <summary>
        /// Создает резервную копию указанного файла.
        /// Если файл не существует, возвращает успех без создания копии (<see cref="BackupFileResult.BackupPath"/> = <c>null</c>).
        /// Имя копии — <c>{имя}.{yyyyMMdd_HHmmss}.{Guid}.bak</c>.
        /// </summary>
        /// <param name="originalFilePath">Полный путь к исходному файлу.</param>
        /// <returns>BackupFileResult с путём к созданной копии.</returns>
        public async Task<BackupFileResult> CreateBackupFileAsync(string originalFilePath)
        {
            if (!File.Exists(originalFilePath))
            {
                _logger.LogDebug("No backup needed: source file does not exist. FilePath='{FilePath}'", originalFilePath);
                return new BackupFileResult() { IsSuccess = true, Code = 200 };
            }

            _logger.LogDebug("Creating backup. Source='{Source}', BackupDir='{BackupDir}'", originalFilePath, _sandboxContext.BackupDir);

            string backupFilePath;

            if (string.IsNullOrEmpty(_sandboxContext.BackupDir))
                backupFilePath = $"{originalFilePath}.{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")}.{Guid.NewGuid().ToString()}.bak";
            else
            {
                if (!Path.Exists(_sandboxContext.BackupDir))
                {
                    Directory.CreateDirectory(_sandboxContext.BackupDir);
                    _logger.LogDebug("Created backup directory. Path='{BackupDir}'", _sandboxContext.BackupDir);
                }

                var fileName = $"{Path.GetFileName(originalFilePath)}.{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")}.{Guid.NewGuid().ToString()}.bak";
                backupFilePath = Path.Join(_sandboxContext.BackupDir, fileName);
            }

            try
            {
                await using var sourceStream = new FileStream(originalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                await using var destinationStream = new FileStream(backupFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await sourceStream.CopyToAsync(destinationStream);

                _logger.LogInformation("Backup created. Source='{Source}', Backup='{Backup}'", originalFilePath, backupFilePath);
                return new BackupFileResult() { IsSuccess = true, BackupPath = backupFilePath, Code = 200 };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Failed to create backup. Source='{Source}', Backup='{Backup}', CorrelationId='{CorrelationId}'", originalFilePath, backupFilePath, correlationId);
                return new BackupFileResult() { IsSuccess = false, Message = $"Failed to create backup. Correlation ID: {correlationId}", Code = 400 };
            }
        }

        /// <summary>
        /// Восстанавливает файл из резервной копии (перезаписывает целевой файл содержимым бэкапа).
        /// Если бэкап не существует — считается успехом (восстанавливать нечего).
        /// </summary>
        /// <param name="backupFilePath">Полный путь к резервной копии.</param>
        /// <param name="originalFilePath">Полный путь к файлу, который требуется восстановить.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        public async Task<OperationResult> RestoreBackupFileAsync(string backupFilePath, string originalFilePath)
        {
            try
            {
                if (!File.Exists(backupFilePath))
                {
                    _logger.LogDebug("No backup to restore: backup file does not exist. Backup='{Backup}'", backupFilePath);
                    return OperationResult.Success();
                }

                string? parentDirectory = Path.GetDirectoryName(originalFilePath);
                if (!string.IsNullOrEmpty(parentDirectory) && !Directory.Exists(parentDirectory))
                    Directory.CreateDirectory(parentDirectory);

                await using var sourceStream = new FileStream(backupFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                await using var destinationStream = new FileStream(originalFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await sourceStream.CopyToAsync(destinationStream);

                _logger.LogInformation("Backup restored. Backup='{Backup}', Original='{Original}'", backupFilePath, originalFilePath);
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Failed to restore backup. Backup='{Backup}', Original='{Original}', CorrelationId='{CorrelationId}'", backupFilePath, originalFilePath, correlationId);
                return OperationResult.Fail($"Failed to restore backup. Correlation ID: {correlationId}");
            }
        }

        /// <summary>
        /// Удаляет резервную копию файла (если она существует).
        /// </summary>
        /// <param name="backupFilePath">Полный путь к удаляемой резервной копии.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        public async Task<OperationResult> DeleteBackupFileAsync(string backupFilePath)
        {
            try
            {
                if (!File.Exists(backupFilePath))
                {
                    _logger.LogDebug("No backup to delete: file does not exist. Backup='{Backup}'", backupFilePath);
                    return OperationResult.Success();
                }

                File.Delete(backupFilePath);
                _logger.LogInformation("Backup deleted. Backup='{Backup}'", backupFilePath);
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Failed to delete backup. Backup='{Backup}', CorrelationId='{CorrelationId}'", backupFilePath, correlationId);
                return OperationResult.Fail($"Failed to delete backup. Correlation ID: {correlationId}");
            }
        }
    }
}