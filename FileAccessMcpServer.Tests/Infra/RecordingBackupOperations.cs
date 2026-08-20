using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

namespace FileAccessMcpServer.Tests.Infra
{
    /// <summary>
    /// Обёртка над реальным IBackupOperations, записывающая факты вызовов
    /// (для проверки флоу отката: когда вызваны Restore/Delete).
    /// </summary>
    internal sealed class RecordingBackupOperations : IBackupOperations
    {
        private readonly IBackupOperations _inner;

        public RecordingBackupOperations(IBackupOperations inner) => _inner = inner;

        /// <summary>Реальный сервис (доступен для прямых проверок happy-path).</summary>
        public IBackupOperations Inner => _inner;

        public int CreateCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public Task<BackupFileResult> CreateBackupFileAsync(string originalFilePath)
        {
            CreateCalls++;
            return _inner.CreateBackupFileAsync(originalFilePath);
        }

        public Task<OperationResult> RestoreBackupFileAsync(string backupFilePath, string originalFilePath)
        {
            RestoreCalls++;
            return _inner.RestoreBackupFileAsync(backupFilePath, originalFilePath);
        }

        public Task<OperationResult> DeleteBackupFileAsync(string backupFilePath)
        {
            DeleteCalls++;
            return _inner.DeleteBackupFileAsync(backupFilePath);
        }
    }
}