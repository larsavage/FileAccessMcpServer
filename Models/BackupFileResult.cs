namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат создания резервной копии файла.
    /// Помимо базовых полей OperationResult содержит путь к созданному бэкапу
    /// (<see cref="BackupPath"/> — <c>null</c>, если копия не требовалась).
    /// </summary>
    internal class BackupFileResult : OperationResult
    {
        /// <summary>
        /// Полный путь к созданной резервной копии, либо <c>null</c>,
        /// если копия не была создана (например, исходный файл отсутствует).
        /// </summary>
        public string? BackupPath { get; set; }
    }
}