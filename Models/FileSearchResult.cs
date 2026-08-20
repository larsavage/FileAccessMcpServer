namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат поиска файлов по маске.
    /// </summary>
    internal class FileSearchResult : OperationResult
    {
        /// <summary>
        /// Каталог, с которого выполнялся поиск.
        /// </summary>
        public string? BasePath { get; set; }

        /// <summary>
        /// Маска имени файла, по которой выполнялся поиск (null — без фильтра по имени).
        /// </summary>
        public string? Mask { get; set; }

        /// <summary>
        /// Подстрока, по которой выполнялся поиск в содержимом файлов (для контентного поиска).
        /// </summary>
        public string? SearchText { get; set; }

        /// <summary>
        /// Количество файлов, включённых в результат.
        /// </summary>
        public int? TotalFound { get; set; }

        /// <summary>
        /// Флаг, указывающий, что найдено больше файлов, чем позволяет лимит, и результат усечён.
        /// </summary>
        public bool? LimitReached { get; set; }

        /// <summary>
        /// Список найденных файлов.
        /// </summary>
        public List<FSEntrie>? Result { get; set; }
    }
}
