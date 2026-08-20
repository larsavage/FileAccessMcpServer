using FileAccessMcpServer.Models;

namespace FileAccessMcpServer.Interfaces
{
    /// <summary>
    /// Интерфейс для операций с файлами.
    /// Предоставляет асинхронные методы для чтения, записи и получения информации о файлах.
    /// </summary>
    internal interface IFileOperations
    {
        /// <summary>
        /// Получить информацию о файле.
        /// Возвращает строковое представление информации (размер, дата создания/изменения и т.д.).
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <returns>Строка с информацией о файле.</returns>
        /// <exception cref="FileNotFoundException">Если файл не существует.</exception>
        /// <exception cref="UnauthorizedAccessException">Если нет прав доступа к файлу.</exception>
        Task<FileInfoResult> FileGetInfoAsync(string filePath);

        /// <summary>
        /// Считать содержимое файла.
        /// Возвращает текст файла с указанным кодированием.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>Содержимое файла в виде строки.</returns>
        /// <exception cref="FileNotFoundException">Если файл не существует.</exception>
        /// <exception cref="UnauthorizedAccessException">Если нет прав доступа к файлу.</exception>
        Task<FileReadResult> FileReadAsync(string filePath, string encoding = "utf-8");

        /// <summary>
        /// Записать содержимое файла.
        /// Создаёт или перезаписывает файл указанным текстом с указанной кодировкой.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="body">Содержимое, которое нужно записать в файл.</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>Путь к созданному файлу.</returns>
        /// <exception cref="DirectoryNotFoundException">Если директория не существует.</exception>
        /// <exception cref="UnauthorizedAccessException">Если нет прав доступа к записи файла.</exception>
        Task<FileWriteResult> FileWriteAsync(string filePath, string content, string encoding = "utf-8");

        /// <summary>
        /// Добавляет содержимое в конец файла. Если файл не существует — создаёт его.
        /// Перед изменением существующего файла создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="content">Содержимое, которое нужно добавить в конец файла.</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>FileWriteResult с количеством добавленных байт.</returns>
        Task<FileWriteResult> FileAppendAsync(string filePath, string content, string encoding = "utf-8");

        /// <summary>
        /// Заменяет указанный диапазон строк файла новыми строками (нумерация строк с 1).
        /// Перед изменением создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="startLine">Номер первой строки для замены (с 1).</param>
        /// <param name="lineCount">Количество строк, которые нужно заменить.</param>
        /// <param name="newContent">Новые строки, которые будут вставлены вместо заменяемого диапазона.</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>FileWriteResult с результатом операции.</returns>
        Task<FileWriteResult> FileReplaceLinesAsync(string filePath, long startLine, long lineCount, string newContent, string encoding = "utf-8");

        /// <summary>
        /// Считать содержимое файла частями (блоками символов).
        /// Позволяет читать файл начиная с указанного символа указанным количеством символов.
        /// В отличие от построчного чтения, работает с файлами любой длины строки.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="startChar">Символ, с которого начать чтение (индексация с 0, по умолчанию 0).</param>
        /// <param name="charsCount">Количество символов для чтения в одном блоке (по умолчанию 1000000).</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>Содержимое файла начиная со startChar на charsCount символов.</returns>
        Task<FileReadChunkResult> FileReadChunksAsync(string filePath, long startChar = 0, int charsCount = 1000000, string encoding = "utf-8");

        /// <summary>
        /// Считывает изображение и возвращает его в формате Base64 data URL.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу изображения.</param>
        /// <returns>FileImageResult с Base64 представлением изображения.</returns>
        Task<FileImageResult> FileImageReadAsync(string filePath);

        /// <summary>
        /// Считывает содержимое бинарного файла и возвращает его в формате Base64.
        /// </summary>
        /// <param name="filePath">Полный путь к бинарному файлу.</param>
        /// <returns>FileBinaryReadResult с Base64-представлением содержимого и MIME-типом.</returns>
        Task<FileBinaryReadResult> FileBinaryReadAsync(string filePath);

        /// <summary>
        /// Копирует файл внутри песочницы.
        /// Перед перезаписью существующего целевого файла создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к исходному файлу.</param>
        /// <param name="newFilePath">Полный путь к копии файла.</param>
        /// <param name="isRewrite"><c>true</c>, чтобы перезаписывать существующий целевой файл.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        Task<OperationResult> FileCopyAsync(string filePath, string newFilePath, bool isRewrite = false);

        /// <summary>
        /// Перемещает (переименовывает) файл внутри песочницы.
        /// Перед перезаписью существующего целевого файла создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к исходному файлу.</param>
        /// <param name="newFilePath">Полный путь к новому расположению файла.</param>
        /// <param name="isRewrite"><c>true</c>, чтобы перезаписывать существующий целевой файл.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        Task<OperationResult> FileMoveAsync(string filePath, string newFilePath, bool isRewrite = false);

        /// <summary>
        /// Удаляет файл внутри песочницы. Перед удалением создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к удаляемому файлу.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        Task<OperationResult> FileDeleteAsync(string filePath);

        /// <summary>
        /// Вычисляет хеш (контрольную сумму) файла.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="algorithm">Название алгоритма: "md5", "crc32", "sha256" или "sha512".</param>
        /// <returns>FileHashResult с хешем в hex-виде.</returns>
        Task<FileHashResult> FileHashAsync(string filePath, string algorithm);
    }
}
