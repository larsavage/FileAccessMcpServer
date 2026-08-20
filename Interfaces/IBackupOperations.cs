using FileAccessMcpServer.Models;

namespace FileAccessMcpServer.Interfaces
{
    /// <summary>
    /// Интерфейс для операций резервного копирования файлов.
    /// </summary>
    internal interface IBackupOperations
    {
        /// <summary>
        /// Создает резервную копию указанного файла.
        /// Если файл не существует, операция считается успешной, а <see cref="BackupFileResult.BackupPath"/>
        /// возвращается <c>null</c> (нечего копировать).
        /// </summary>
        /// <param name="originalFilePath">Полный путь к исходному файлу.</param>
        /// <returns>BackupFileResult с путём к созданной копии (если она создана).</returns>
        Task<BackupFileResult> CreateBackupFileAsync(string originalFilePath);

        /// <summary>
        /// Восстанавливает файл из резервной копии (перезаписывает целевой файл содержимым бэкапа).
        /// Используется для отката, если критическая операция была прервана/отменена
        /// (например, при частичной записи файла).
        /// </summary>
        /// <param name="backupFilePath">Полный путь к резервной копии.</param>
        /// <param name="originalFilePath">Полный путь к файлу, который требуется восстановить.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        Task<OperationResult> RestoreBackupFileAsync(string backupFilePath, string originalFilePath);

        /// <summary>
        /// Удаляет резервную копию файла.
        /// Используется для очистки бэкапа после успешного отката или когда оригинал не был затронут
        /// (операция прервана до изменения файла).
        /// </summary>
        /// <param name="backupFilePath">Полный путь к удаляемой резервной копии.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        Task<OperationResult> DeleteBackupFileAsync(string backupFilePath);
    }
}