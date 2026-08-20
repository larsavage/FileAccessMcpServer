namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Результат операции чтения нескольких файлов одновременно.
    /// </summary>
    internal class MultiFileReadResult : OperationResult
    {
        /// <summary>
        /// Список результатов чтения для каждого запрошенного файла.
        /// </summary>
        public List<FileReadResult>? Files { get; set; }
    }
}
