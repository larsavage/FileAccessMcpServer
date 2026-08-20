namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Представляет запись о директории или файле для построения дерева.
    /// </summary>
    /// <example>
    /// Создание записи о директории:
    /// var dir = new FSEntrie("/path/to/dir");
    /// 
    /// Создание записи о файле:
    /// var file = new FSEntrie("/path/to/file.txt", "File");
    /// </example>
    internal class FSEntrie
    {
        /// <summary>
        /// Тип записи: "Dir" (директория) или "File" (файл).
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Имя или путь к директории/файлу.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Размер файла в байтах (для файлов, для директорий - null).
        /// </summary>
        public long? Size { get; set; } = null;

        /// <summary>
        /// Инициализирует запись о директории.
        /// </summary>
        /// <param name="name">Путь к директории.</param>
        public FSEntrie(string name)
        {
            Type = "Dir";
            Name = name;
        }

        /// <summary>
        /// Инициализирует запись о директории или файле.
        /// </summary>
        /// <param name="name">Путь к директории или файлу.</param>
        /// <param name="type">Тип записи: "Dir" или "File".</param>
        public FSEntrie(string name, string type)
        {
            Type = type;
            Name = name;
        }
    }
}
