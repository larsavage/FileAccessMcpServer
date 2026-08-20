namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат вычисления хеша (контрольной суммы) файла.
    /// </summary>
    internal class FileHashResult : OperationResult
    {
        /// <summary>
        /// Полный путь к файлу.
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// Имя использованного алгоритма (md5, crc32, sha256, sha512).
        /// </summary>
        public string? Algorithm { get; set; }

        /// <summary>
        /// Хеш файла в виде шестнадцатеричной строки (нижний регистр).
        /// </summary>
        public string? Hash { get; set; }

        /// <summary>
        /// Размер файла в байтах.
        /// </summary>
        public long? SizeInBytes { get; set; }
    }
}
