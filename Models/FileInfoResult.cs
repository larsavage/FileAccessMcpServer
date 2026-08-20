namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат получения информации о файле.
    /// </summary>
    internal class FileInfoResult : OperationResult
    {
        /// <summary>
        /// Имя файла.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Расширение файла (например, ".txt", ".json").
        /// </summary>
        public string? Extension { get; set; }

        /// <summary>
        /// MIME-тип файла.
        /// </summary>
        public string? FileType { get; set; }

        /// <summary>
        /// Размер файла в байтах.
        /// </summary>
        public long? Length { get; set; }

        /// <summary>
        /// Дата создания файла.
        /// </summary>
        public DateTime? FileCreate { get; set; }

        /// <summary>
        /// Дата последнего изменения файла.
        /// </summary>
        public DateTime? FileLastUpdate { get; set; }

        /// <summary>
        /// Дата последнего доступа к файлу.
        /// </summary>
        public DateTime? FileLastAccessTime { get; set; }

        /// <summary>
        /// Флаг "только для чтения".
        /// </summary>
        public bool? IsReadOnly { get; set; }

        /// <summary>
        /// Флаг, указывающий, является ли файл символьной ссылкой.
        /// </summary>
        public bool? IsLink { get; set; }

        /// <summary>
        /// Цель символьной ссылки (если IsLink == true).
        /// </summary>
        public string? LinkTarget { get; set; }
    }
}
