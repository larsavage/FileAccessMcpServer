namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат операций с директориями, содержащий список записей FSEntrie.
    /// </summary>
    internal class FSEntriesResult : OperationResult
    {
        /// <summary>
        /// Путь к директории, для которой выполнялась операция.
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// Список записей о файлах и поддиректориях.
        /// </summary>
        public List<FSEntrie>? Result { get; set; }
    }
}
