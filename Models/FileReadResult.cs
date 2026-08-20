namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат чтения содержимого файла.
    /// </summary>
    internal class FileReadResult : OperationResult
    {
        /// <summary>
        /// Полный путь к прочитанному файлу.
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// Размер файла в байтах.
        /// </summary>
        public long? SizeInBytes { get; set; }

        /// <summary>
        /// Текстовое содержимое файла.
        /// </summary>
        public string? Content { get; set; }
    }
}
