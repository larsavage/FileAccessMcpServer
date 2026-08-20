namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат чтения бинарного файла в формате Base64.
    /// </summary>
    internal class FileBinaryReadResult : OperationResult
    {
        /// <summary>
        /// Полный путь к прочитанному файлу.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// MIME-тип файла (например, "application/pdf", "application/octet-stream").
        /// </summary>
        public string? MimeType { get; set; }

        /// <summary>
        /// Размер файла в байтах.
        /// </summary>
        public long? SizeInBytes { get; set; }

        /// <summary>
        /// Base64-представление содержимого файла.
        /// </summary>
        public string? Base64Data { get; set; }
    }
}
