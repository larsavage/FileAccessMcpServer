namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат чтения файла по частям (paginated reading).
    /// </summary>
    internal class FileReadChunkResult : OperationResult
    {
        /// <summary>
        /// Полный путь к файлу.
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// Символ, с которого начато чтение (индексация с 0).
        /// </summary>
        public long? StartChar { get; set; }

        /// <summary>
        /// Количество символов, возвращённых в текущем блоке.
        /// </summary>
        public int? CharsReturned { get; set; }

        /// <summary>
        /// Общее количество символов, прочитанных до текущего момента (включая предыдущие блоки).
        /// </summary>
        public long? TotalCharsReadUpTo { get; set; }

        /// <summary>
        /// Флаг, указывающий, есть ли ещё символы для чтения.
        /// </summary>
        public bool? HasMore { get; set; }

        /// <summary>
        /// Символ, с которого нужно продолжить чтение (если HasMore == true).
        /// </summary>
        public long? NextStartChar { get; set; }

        /// <summary>
        /// Текстовое содержимое текущего чанка.
        /// </summary>
        public string? Content { get; set; }
    }
}
