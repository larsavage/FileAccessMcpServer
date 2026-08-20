using FileAccessMcpServer.Models;

namespace FileAccessMcpServer.Interfaces
{
    /// <summary>
    /// Интерфейс для операций с директориями
    /// </summary>
    internal interface IDirectoryOperations
    {
        /// <summary>
        /// Получить список файлов в указанной директории
        /// </summary>
        Task<List<FSEntrie>> GetFilesInDirectoryAsync(string path);
        
        /// <summary>
        /// Получить дерево каталога
        /// </summary>
        Task<List<FSEntrie>> GetDirectoryTreeAsync(string path, int nesting);

        /// <summary>
        /// Получить дерево каталога с файлами и их метаданными.
        /// </summary>
        /// <param name="path">Путь к корневой директории.</param>
        /// <param name="nesting">Максимальный уровень вложенности.</param>
        /// <returns>Список записей FSEntrie с файлами и поддиректориями.</returns>
        Task<List<FSEntrie>> GetDirectoryTreeWithFilesAsync(string path, int nesting);

        /// <summary>
        /// Создать каталог
        /// </summary>
        Task<OperationResult> CreateDirectoryAsync(string path);

        /// <summary>
        /// Ищет файлы по маске имени (рекурсивно, с ограничением количества результатов).
        /// Маска применяется к имени файла; поддерживаются символы '*' и '?'.
        /// </summary>
        /// <param name="path">Путь к корневой директории поиска.</param>
        /// <param name="mask">Маска имени файла (например, "*.cs" или "*report*").</param>
        /// <returns>FileSearchResult со списком найденных файлов.</returns>
        Task<FileSearchResult> SearchFilesAsync(string path, string mask);

        /// <summary>
        /// Ищет файлы, содержимое которых содержит заданную подстроку (рекурсивно, с ограничением
        /// количества результатов). Опционально фильтрует файлы по маске имени.
        /// Файлы размером больше MaxOneFileSizeInBytes пропускаются.
        /// </summary>
        /// <param name="path">Путь к корневой директории поиска.</param>
        /// <param name="searchText">Подстрока для поиска в содержимом файлов (с учётом регистра).</param>
        /// <param name="mask">Опциональная маска имени файла; null или пустая строка — без фильтра.</param>
        /// <param name="encoding">Кодировка текста файлов (по умолчанию "utf-8").</param>
        /// <returns>FileSearchResult со списком найденных файлов.</returns>
        Task<FileSearchResult> SearchFilesByContentAsync(string path, string searchText, string? mask = null, string encoding = "utf-8");
    }
}