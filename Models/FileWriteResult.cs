namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат операции записи в файл.
    /// </summary>
    internal class FileWriteResult : OperationResult
    {
        /// <summary>
        /// Полный путь к записанному файлу.
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// Количество записанных байт.
        /// </summary>
        public long? BytesWritten { get; set; }
    }
}
