namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат чтения изображения, содержащий Base64 data URL.
    /// </summary>
    internal class FileImageResult : OperationResult
    {
        /// <summary>
        /// Полный путь к файлу изображения.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// MIME-тип изображения (например, "image/png").
        /// </summary>
        public string? MimeType { get; set; }

        /// <summary>
        /// Размер файла в байтах.
        /// </summary>
        public long? SizeInBytes { get; set; }

        /// <summary>
        /// Base64 data URL изображения (формат: "data:{mimeType};base64,{data}").
        /// </summary>
        public string? DataUrl { get; set; }
    }
}
