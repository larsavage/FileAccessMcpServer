using System.ComponentModel;
using System.Security.Cryptography;

using FileAccessMcpServer.Core;
using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

/// <summary>
/// Инструменты для операций с файлами в MCP сервере.
/// </summary>
namespace FileAccessMcpServer.Tools
{
    /// <summary>
    /// Предоставляет инструменты для работы с файлами: чтение, запись, получение информации о файлах, чтение изображений, копирование, перемещение и удаление.
    /// </summary>
    [McpServerToolType]
    internal class FileTools
    {
        private readonly ILogger<FileTools> _logger;
        private readonly IFileOperations _fileService;
        private readonly IOptionsMonitor<AppOptions> _fileOptions;
        private readonly PathValidator _pathValidator;

        // Статический кэш: токены подтверждения должны переживать отдельные MCP-запросы.
        // Записи автоматически истекают через TokenLifetime, а общий размер кэша ограничен
        // (SizeLimit), чтобы защитить память от переполнения через file_delete.
        private static readonly MemoryCache _pendingConfirmations = new(new MemoryCacheOptions
        {
            SizeLimit = Consts.MaxPendingDeleteConfirmations
        });

        private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

        // Сериализует операцию "проверить токен → потратить → удалить запись", чтобы
        // параллельный запрос с тем же токеном никогда не увидел уже израсходованную запись.
        private static readonly object _pendingConfirmationsLock = new();

        /// <summary>
        /// Запись о подтверждении удаления. Позволяет атомарно «потратить» токен,
        /// чтобы параллельные запросы не выполнили удаление дважды.
        /// </summary>
        private sealed class ConfirmationRecord
        {
            public required string Token { get; init; }

            private int _consumed;

            /// <summary>
            /// Атомарно помечает токен использованным. Возвращает <c>true</c>, если вызов первый.
            /// </summary>
            public bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
        }

        /// <summary>
        /// Нормализует ключ кэша: полный путь без завершающего разделителя. На Windows путь
        /// приводится к верхнему регистру, так как файловая система не чувствительна к регистру.
        /// </summary>
        private static string NormalizeCacheKey(string absoluteFilePath)
        {
            string key = Path.GetFullPath(absoluteFilePath);
            key = Path.TrimEndingDirectorySeparator(key);
            return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
        }

        /// <summary>
        /// Инициализирует экземпляр класса FileTools.
        /// </summary>
        /// <param name="logger">Логгер для записи событий.</param>
        /// <param name="fileService">Сервис операций с файлами.</param>
        /// <param name="fileOptions">Монитор конфигурационных опций (лимиты batch-операций, поддержка hot reload).</param>
        /// <param name="pathValidator">Валидатор путей для проверки доступа к песочнице.</param>
        public FileTools(ILogger<FileTools> logger, IFileOperations fileService, IOptionsMonitor<AppOptions> fileOptions, PathValidator pathValidator)
        {
            _logger = logger;
            _fileService = fileService;
            _fileOptions = fileOptions;
            _pathValidator = pathValidator;
        }

        /// <summary>
        /// Чтение файла частями (блоками символов), чтобы не перегружать контекст модели.
        /// </summary>
        [McpServerTool(Name = "file_read_chunks")]
        [Description("Позволяет читать большие текстовые файлы по частям (блоками символов), чтобы не перегружать контекст модели. Работает с файлами любой длины строки.")]
        public async Task<FileReadChunkResult> ReadFileChunksAsync(
            [Description("Абсолютный путь к файлу")] string filePath,
            [Description("Символ, с которого НАЧАТЬ чтение (индексация с 0)")] long startChar = 0,
            [Description("Количество символов для чтения в одном блоке. По умолчанию 500000. Максимум 1000000.")] int charsCount = 500_000,
            [Description("Кодировка текста. По умолчанию utf-8.")] string encoding = "utf-8")
        {
            if (startChar < 0) startChar = 0;
            if (charsCount <= 0) charsCount = 500_000; // default value
            if (charsCount > Consts.MaxChunkChars) charsCount = Consts.MaxChunkChars;

            _logger.LogDebug("MCP tool 'file_read_chunks' invoked. FilePath='{FilePath}', StartChar={StartChar}, CharsCount={CharsCount}, Encoding='{Encoding}'", filePath, startChar, charsCount, encoding);

            string absolutePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absolutePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_read_chunks' rejected. Reason='{Reason}'", result.Message);
                return new FileReadChunkResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var chunkResult = await _fileService.FileReadChunksAsync(absolutePath, startChar, charsCount, encoding);
            _logger.LogInformation("MCP tool 'file_read_chunks' completed. FilePath='{FilePath}', IsSuccess={IsSuccess}", absolutePath, chunkResult.IsSuccess);
            return chunkResult;
        }

        /// <summary>
        /// Считывает содержимое нескольких файлов из песочницы одновременно.
        /// </summary>
        [McpServerTool(Name = "files_read_multiple")]
        [Description("Считывает содержимое нескольких файлов из песочницы одновременно.")]
        public async Task<MultiFileReadResult> ReadMultipleFilesAsync(
            [Description("Массив путей к файлам для чтения (максимум 5 файлов). Пример: ['d:\\sandbox\\file1.txt', '/sandbox/project/file2.json']")] string[] filesPath,
            [Description("Кодировка текста. По умолчанию utf-8.")] string encoding = "utf-8")
        {
            _logger.LogDebug("MCP tool 'files_read_multiple' invoked. FileCount={FileCount}, Encoding='{Encoding}'", filesPath?.Length ?? 0, encoding);

            if (filesPath == null || filesPath.Length == 0)
            {
                _logger.LogWarning("MCP tool 'files_read_multiple' rejected: path list is empty.");
                return new MultiFileReadResult() { IsSuccess = false, Message = "Path list cannot be empty" };
            }

            if (filesPath.Length > _fileOptions.CurrentValue.MaxQuantityFiles)
            {
                _logger.LogWarning("MCP tool 'files_read_multiple' rejected: too many files. Requested={Requested}, Max={Max}", filesPath.Length, _fileOptions.CurrentValue.MaxQuantityFiles);
                return new MultiFileReadResult() { IsSuccess = false, Message = $"The maximum number of files to read is {_fileOptions.CurrentValue.MaxQuantityFiles}. You specified {filesPath.Length} files" };
            }

            List<FileReadResult> fResult = new List<FileReadResult>();
            long fullSize = 0;
            foreach (var filePath in filesPath)
            {
                string absoluteFilePath;
                var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath);

                if (result != null)
                {
                    _logger.LogWarning("MCP tool 'files_read_multiple': file rejected. FilePath='{FilePath}', Reason='{Reason}'", filePath, result.Message);
                    fResult.Add(new FileReadResult() { IsSuccess = result.IsSuccess, Message = result.Message });
                    continue;
                }

                long fileLength;
                try
                {
                    fileLength = new FileInfo(absoluteFilePath).Length;
                }
                catch (FileNotFoundException)
                {
                    fResult.Add(new FileReadResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 });
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    fResult.Add(new FileReadResult() { IsSuccess = false, Message = $"Security error: You do not have permission to read the file {filePath}." });
                    continue;
                }
                catch (IOException ex)
                {
                    string correlationId = Helpers.GetCorrelationId();
                    _logger.LogError(ex, "Input/output error while getting file length for batch read. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", absoluteFilePath, correlationId);
                    fResult.Add(new FileReadResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" });
                    continue;
                }

                if (_fileOptions.CurrentValue.MaxTotalSizeInBytes < fullSize + fileLength)
                {
                    fResult.Add(new FileReadResult() { IsSuccess = false, Message = $"[Skipped] File too large for batch processing: {filePath}. {(fileLength > _fileOptions.CurrentValue.MaxOneFileSizeInBytes ? "Requires isolated reading." : "Postponed to the next batch.")}" });
                    _logger.LogWarning("MCP tool 'files_read_multiple': total size limit reached. TotalSize={TotalSize} bytes, MaxTotalSize={MaxTotalSize} bytes", fullSize, _fileOptions.CurrentValue.MaxTotalSizeInBytes);
                    continue;
                }

                FileReadResult _result = await _fileService.FileReadAsync(absoluteFilePath, encoding);
                if (_result.SizeInBytes != null) fullSize += _result.SizeInBytes.Value;
                fResult.Add(_result);
            }

            _logger.LogInformation("MCP tool 'files_read_multiple' completed. FilesRead={FilesRead}, TotalSize={TotalSize} bytes", fResult.Count, fullSize);
            return new MultiFileReadResult()
            {
                IsSuccess = true,
                Files = fResult
            };
        }

        /// <summary>
        /// Получение информации о файле: размер, дата создания/изменения, тип файла.
        /// </summary>
        [McpServerTool(Name = "file_get_info")]
        [Description("Получение информации о файле: размер, дата создания/изменения, тип файла.")]
        public async Task<FileInfoResult> GetFileInfoAsync([Description("Абсолютный путь к файлу (например, 'd:\\sandbox\\app\\logs\\app.log' или '/sandbox/app/logs/app.log')")] string filePath)
        {
            _logger.LogDebug("MCP tool 'file_get_info' invoked. FilePath='{FilePath}'", filePath);

            string absoluteFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_get_info' rejected. Reason='{Reason}'", result.Message);
                return new FileInfoResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var infoResult = await _fileService.FileGetInfoAsync(absoluteFilePath);
            _logger.LogInformation("MCP tool 'file_get_info' completed. FilePath='{FilePath}', IsSuccess={IsSuccess}", absoluteFilePath, infoResult.IsSuccess);
            return infoResult;
        }

        /// <summary>
        /// Вычисляет хеш (контрольную сумму) файла внутри песочницы.
        /// </summary>
        [McpServerTool(Name = "file_hash")]
        [Description("Вычисляет хеш (контрольную сумму) файла. Поддерживаемые алгоритмы: md5, crc32, sha256, sha512.")]
        public async Task<FileHashResult> FileHashAsync(
            [Description("Абсолютный путь к файлу внутри песочницы (например, 'd:\\sandbox\\archive.zip' или '/sandbox/archive.zip')")] string filePath,
            [Description("Название алгоритма хеширования: 'md5', 'crc32', 'sha256' или 'sha512'. По умолчанию sha256.")] string algorithm = "sha256")
        {
            _logger.LogDebug("MCP tool 'file_hash' invoked. FilePath='{FilePath}', Algorithm='{Algorithm}'", filePath, algorithm);

            string absoluteFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_hash' rejected. Reason='{Reason}'", result.Message);
                return new FileHashResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var hashResult = await _fileService.FileHashAsync(absoluteFilePath, algorithm);
            _logger.LogInformation("MCP tool 'file_hash' completed. FilePath='{FilePath}', Algorithm='{Algorithm}', IsSuccess={IsSuccess}", absoluteFilePath, algorithm, hashResult.IsSuccess);
            return hashResult;
        }

        /// <summary>
        /// Создает новый файл или полностью перезаписывает существующий внутри песочницы. Автоматически создает недостающие директории.
        /// </summary>
        [McpServerTool(Name = "file_write")]
        [Description("Создает новый файл или полностью перезаписывает существующий внутри песочницы. Автоматически создает недостающие директории.")]
        public async Task<FileWriteResult> WriteFileAsync(
            [Description("Абсолютный путь к файлу внутри песочницы (например, 'd:\\sandbox\\config.json' или '/sandbox/subfolder/script.py')")] string filePath,
            [Description("Полное текстовое содержимое, которое нужно записать в файл")] string content,
            [Description("Encoding текста. По умолчанию utf-8.")] string encoding = "utf-8")
        {
            if (content == null) content = string.Empty;

            _logger.LogDebug("MCP tool 'file_write' invoked. FilePath='{FilePath}', ContentLength={ContentLength} chars, Encoding='{Encoding}'", filePath, content.Length, encoding);

            string absoluteFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath, false);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_write' rejected. Reason='{Reason}'", result.Message);
                return new FileWriteResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var writeResult = await _fileService.FileWriteAsync(absoluteFilePath, content, encoding);
            _logger.LogInformation("MCP tool 'file_write' completed. FilePath='{FilePath}', IsSuccess={IsSuccess}", absoluteFilePath, writeResult.IsSuccess);
            return writeResult;
        }

        /// <summary>
        /// Добавляет текст в конец файла. Если файл не существует — создаёт его.
        /// </summary>
        [McpServerTool(Name = "file_append")]
        [Description("Добавляет текст в конец файла. Если файл не существует — создаёт его. Перед изменением существующего файла автоматически создаётся резервная копия.")]
        public async Task<FileWriteResult> AppendToFileAsync(
            [Description("Абсолютный путь к файлу внутри песочницы (например, 'd:\\sandbox\\logs\\app.log' или '/sandbox/logs/app.log')")] string filePath,
            [Description("Текст, который нужно добавить в конец файла")] string content,
            [Description("Кодировка текста. По умолчанию utf-8.")] string encoding = "utf-8")
        {
            if (content == null) content = string.Empty;

            _logger.LogDebug("MCP tool 'file_append' invoked. FilePath='{FilePath}', ContentLength={ContentLength} chars, Encoding='{Encoding}'", filePath, content.Length, encoding);

            string absoluteFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath, false);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_append' rejected. Reason='{Reason}'", result.Message);
                return new FileWriteResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var appendResult = await _fileService.FileAppendAsync(absoluteFilePath, content, encoding);
            _logger.LogInformation("MCP tool 'file_append' completed. FilePath='{FilePath}', IsSuccess={IsSuccess}", absoluteFilePath, appendResult.IsSuccess);
            return appendResult;
        }

        ///// <summary>
        ///// Заменяет указанный диапазон строк файла новыми строками (нумерация строк с 1).
        ///// </summary>
        //[McpServerTool(Name = "file_replace_lines")]
        //[Description("Заменяет указанный диапазон строк файла новыми строками. Нумерация строк начинается с 1. Перед изменением автоматически создаётся резервная копия.")]
        //public async Task<FileWriteResult> ReplaceLinesAsync(
        //    [Description("Абсолютный путь к файлу внутри песочницы (например, 'd:\\sandbox\\config.json')")] string filePath,
        //    [Description("Номер первой строки для замены (нумерация с 1)")] long startLine,
        //    [Description("Количество строк, которые нужно заменить")] long lineCount,
        //    [Description("Новые строки, которые будут вставлены вместо заменяемого диапазона")] string newContent,
        //    [Description("Кодировка текста. По умолчанию utf-8.")] string encoding = "utf-8")
        //{
        //    if (newContent == null) newContent = string.Empty;

        //    _logger.LogDebug("MCP tool 'file_replace_lines' invoked. FilePath='{FilePath}', StartLine={StartLine}, LineCount={LineCount}, NewContentLength={NewContentLength} chars, Encoding='{Encoding}'", filePath, startLine, lineCount, newContent.Length, encoding);

        //    string absoluteFilePath;
        //    var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath);
        //    if (result != null)
        //    {
        //        _logger.LogWarning("MCP tool 'file_replace_lines' rejected. Reason='{Reason}'", result.Message);
        //        return new FileWriteResult() { IsSuccess = result.IsSuccess, Message = result.Message };
        //    }

        //    var replaceResult = await _fileService.FileReplaceLinesAsync(absoluteFilePath, startLine, lineCount, newContent, encoding);
        //    _logger.LogInformation("MCP tool 'file_replace_lines' completed. FilePath='{FilePath}', IsSuccess={IsSuccess}", absoluteFilePath, replaceResult.IsSuccess);
        //    return replaceResult;
        //}

        /// <summary>
        /// Считывает текстовое содержимое указанного файла из песочницы. Максимальный размер файла для чтения 4 Мб.
        /// </summary>
        [McpServerTool(Name = "file_read")]
        [Description("Считывает текстовое содержимое указанного файла из песочницы. Максимальный размер файла для чтения 4 Мб.")]
        public async Task<FileReadResult> ReadFileAsync(
            [Description("Абсолютный путь к файлу внутри песочницы (например, 'd:\\sandbox\\config.json' или '/sandbox/subfolder/script.py')")] string filePath,
            [Description("Кодировка текста. По умолчанию utf-8.")] string encoding = "utf-8")
        {
            _logger.LogDebug("MCP tool 'file_read' invoked. FilePath='{FilePath}', Encoding='{Encoding}'", filePath, encoding);

            string absoluteFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_read' rejected. Reason='{Reason}'", result.Message);
                return new FileReadResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var readResult = await _fileService.FileReadAsync(absoluteFilePath, encoding);
            _logger.LogInformation("MCP tool 'file_read' completed. FilePath='{FilePath}', IsSuccess={IsSuccess}", absoluteFilePath, readResult.IsSuccess);
            return readResult;
        }

        /// <summary>
        /// Считывает изображение (PNG, BMP, JPG) из песочницы и возвращает его в формате Base64, понятном для мультимодальных ИИ.
        /// </summary>
        [McpServerTool(Name = "file_image_read")]
        [Description("Считывает изображение (PNG, BMP, JPG) из песочницы и возвращает его в формате Base64, понятном для мультимодальных ИИ.")]
        public async Task<FileImageResult> ReadImageAsync(
            [Description("Абсолютный путь к файлу изображения внутри песочницы (например, 'd:\\sandbox\\photos\\diagram.png' или '/sandbox/photos/diagram.png')")] string fileImagePath)
        {
            _logger.LogDebug("MCP tool 'file_image_read' invoked. FilePath='{FilePath}'", fileImagePath);

            string absoluteFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(fileImagePath, out absoluteFilePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_image_read' rejected. Reason='{Reason}'", result.Message);
                return new FileImageResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var imageResult = await _fileService.FileImageReadAsync(absoluteFilePath);
            _logger.LogInformation("MCP tool 'file_image_read' completed. FilePath='{FilePath}', IsSuccess={IsSuccess}", absoluteFilePath, imageResult.IsSuccess);
            return imageResult;
        }

        /// <summary>
        /// Считывает бинарный файл (PDF, ZIP, DLL и т.д.) из песочницы и возвращает его содержимое в формате Base64.
        /// </summary>
        [McpServerTool(Name = "file_binary_read")]
        [Description("Считывает бинарный файл из песочницы и возвращает его содержимое в формате Base64 вместе с MIME-типом. Подходит для файлов, не являющихся изображениями (PDF, ZIP, DLL и т.д.).")]
        public async Task<FileBinaryReadResult> ReadBinaryAsync(
            [Description("Абсолютный путь к бинарному файлу внутри песочницы (например, 'd:\\sandbox\\data\\archive.zip')")] string filePath)
        {
            _logger.LogDebug("MCP tool 'file_binary_read' invoked. FilePath='{FilePath}'", filePath);

            string absoluteFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_binary_read' rejected. Reason='{Reason}'", result.Message);
                return new FileBinaryReadResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var binaryResult = await _fileService.FileBinaryReadAsync(absoluteFilePath);
            _logger.LogInformation("MCP tool 'file_binary_read' completed. FilePath='{FilePath}', IsSuccess={IsSuccess}", absoluteFilePath, binaryResult.IsSuccess);
            return binaryResult;
        }

        /// <summary>
        /// Копирует файл внутри песочницы. Оба пути проверяются на нахождение в песочнице.
        /// </summary>
        [McpServerTool(Name = "file_copy")]
        [Description("Копирует файл. Оба пути проверяются на расположении в песочнице")]
        public async Task<OperationResult> FileCopyAsync(
            [Description("Путь к оригинальному файлу внутри песочницы")] string originFilePath,
            [Description("Путь к скопированному файлу внутри песочницы")] string newFilePath,
            [Description("Перезаписать в случае, если такой файл существует. По умолчанию нет (false)")] bool isRewrite = false)
        {
            _logger.LogDebug("MCP tool 'file_copy' invoked. OriginFilePath='{OriginFilePath}', NewFilePath='{NewFilePath}', IsRewrite={IsRewrite}", originFilePath, newFilePath, isRewrite);

            if (originFilePath == newFilePath)
                return OperationResult.Fail("The source and destination files are the same");

            string absoluteOriginFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(originFilePath, out absoluteOriginFilePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_copy' rejected for source file. Reason='{Reason}'", result.Message);
                return result;
            }

            string absoluteNewFilePath;
            result = _pathValidator.ValidateAndGetAbsolutePath(newFilePath, out absoluteNewFilePath, false);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_copy' rejected for destination file. Reason='{Reason}'", result.Message);
                return result;
            }

            var copyResult = await _fileService.FileCopyAsync(absoluteOriginFilePath, absoluteNewFilePath, isRewrite);
            _logger.LogInformation("MCP tool 'file_copy' completed. Source='{OriginFilePath}', Destination='{NewFilePath}', IsSuccess={IsSuccess}", absoluteOriginFilePath, absoluteNewFilePath, copyResult.IsSuccess);
            return copyResult;
        }

        /// <summary>
        /// Перемещает (переименовывает) файл внутри песочницы. Оба пути проверяются на нахождение в песочнице.
        /// </summary>
        [McpServerTool(Name = "file_move")]
        [Description("Перемещает файл. Оба пути проверяются на расположении в песочнице")]
        public Task<OperationResult> FileMoveAsync(
            [Description("Путь к оригинальному файлу внутри песочницы")] string originFilePath,
            [Description("Новый путь к файлу внутри песочницы")] string newFilePath,
            [Description("Перезаписать в случае, если такой файл существует. По умолчанию нет (false)")] bool isRewrite = false)
        {
            return MoveFileCoreAsync("file_move", originFilePath, newFilePath, isRewrite);
        }

        /// <summary>
        /// Переименовывает файл внутри песочницы. Алиас инструмента file_move (указывается текущий путь и новый путь/имя файла).
        /// </summary>
        [McpServerTool(Name = "file_rename")]
        [Description("Переименовывает файл внутри песочницы (алиас file_move). Указывается текущий путь к файлу и новый путь или новое имя файла.")]
        public Task<OperationResult> FileRenameAsync(
            [Description("Текущий путь к файлу внутри песочницы")] string originFilePath,
            [Description("Новый путь к файлу или новое имя файла внутри песочницы")] string newFilePath,
            [Description("Перезаписать в случае, если такой файл существует. По умолчанию нет (false)")] bool isRewrite = false)
        {
            return MoveFileCoreAsync("file_rename", originFilePath, newFilePath, isRewrite);
        }

        /// <summary>
        /// Общая реализация перемещения/переименования файла для инструментов file_move и file_rename.
        /// </summary>
        private async Task<OperationResult> MoveFileCoreAsync(string toolName, string originFilePath, string newFilePath, bool isRewrite)
        {
            _logger.LogDebug("MCP tool '{ToolName}' invoked. OriginFilePath='{OriginFilePath}', NewFilePath='{NewFilePath}', IsRewrite={IsRewrite}", toolName, originFilePath, newFilePath, isRewrite);

            if (originFilePath == newFilePath)
                return OperationResult.Fail("The source and destination files are the same");

            string absoluteOriginFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(originFilePath, out absoluteOriginFilePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool '{ToolName}' rejected for source file. Reason='{Reason}'", toolName, result.Message);
                return result;
            }

            string absoluteNewFilePath;
            result = _pathValidator.ValidateAndGetAbsolutePath(newFilePath, out absoluteNewFilePath, false);
            if (result != null)
            {
                _logger.LogWarning("MCP tool '{ToolName}' rejected for destination file. Reason='{Reason}'", toolName, result.Message);
                return result;
            }

            var moveResult = await _fileService.FileMoveAsync(absoluteOriginFilePath, absoluteNewFilePath, isRewrite);
            _logger.LogInformation("MCP tool '{ToolName}' completed. Source='{OriginFilePath}', Destination='{NewFilePath}', IsSuccess={IsSuccess}", toolName, absoluteOriginFilePath, absoluteNewFilePath, moveResult.IsSuccess);
            return moveResult;
        }

        /// <summary>
        /// Удаляет файл внутри песочницы. Требует обязательного подтверждения: через elicitation
        /// (если клиент его поддерживает) либо через одноразовый токен confirmToken.
        /// </summary>
        [McpServerTool(Name = "file_delete")]
        [Description("Удаляет указанный файл с диска внутри песочницы. ВНИМАНИЕ: Этот инструмент является деструктивным и Обязательно требует явного подтверждения (confirmation) от пользователя перед каждым выполнением! Не вызывайте скрытно.")]
        public Task<OperationResult> FileDeleteToolAsync(
            McpServer server,
            [Description("Путь к оригинальному файлу внутри песочницы")] string filePath,
            [Description("Токен подтверждения (оставьте пустым при первом вызове)")] string confirmToken = "")
        {
            return FileDeleteCoreAsync(server, filePath, confirmToken);
        }

        /// <summary>
        /// Удаляет файл без доступа к <see cref="McpServer"/> (используется тестами и внутренними вызовами).
        /// Поведение совпадает с инструментом, но elicitation недоступен — применяется токен-флоу.
        /// </summary>
        public Task<OperationResult> FileDeleteAsync(string filePath, string confirmToken = "")
        {
            return FileDeleteCoreAsync(null, filePath, confirmToken);
        }

        /// <summary>
        /// Общая реализация удаления: elicitation с резервным токен-подтверждением.
        /// </summary>
        private async Task<OperationResult> FileDeleteCoreAsync(McpServer? server, string filePath, string confirmToken)
        {
            _logger.LogDebug("MCP tool 'file_delete' invoked. FilePath='{FilePath}'", filePath);

            string absoluteFilePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absoluteFilePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'file_delete' rejected. Reason='{Reason}'", result.Message);
                return result;
            }

            if (string.IsNullOrWhiteSpace(confirmToken))
            {
                // Приоритет — elicitation: пользователь подтверждает операцию в диалоге клиента.
                var outcome = await ConfirmationService.RequestConfirmationAsync(
                    server,
                    _logger,
                    "Удаление файла",
                    $"Удалить файл '{absoluteFilePath}'? Действие необратимо.",
                    CancellationToken.None);

                if (outcome == ConfirmationService.ConfirmationOutcome.Confirmed)
                {
                    _logger.LogInformation("Deletion confirmed via elicitation. FilePath='{FilePath}'", absoluteFilePath);
                    return await _fileService.FileDeleteAsync(absoluteFilePath);
                }

                if (outcome == ConfirmationService.ConfirmationOutcome.Declined)
                {
                    return new OperationResult() { IsSuccess = false, Message = $"[ОТКЛОНЕНО]: Пользователь отклонил удаление файла '{filePath}'." };
                }

                // Клиент не поддерживает elicitation — переходим на одноразовый токен.
            }

            if (string.IsNullOrWhiteSpace(confirmToken))
            {
                string generatedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(TokenLifetime)
                        .SetSize(1);

                _pendingConfirmations.Set(
                    NormalizeCacheKey(absoluteFilePath),
                    new ConfirmationRecord { Token = generatedToken },
                    cacheEntryOptions);

                _logger.LogInformation("Deletion confirmation requested. FilePath='{FilePath}', TokenLifetime={TokenLifetime} minutes", absoluteFilePath, TokenLifetime.TotalMinutes);
                return new OperationResult()
                {
                    IsSuccess = true,
                    Code = 202,
                    Message = $"ЗАПРОС ПОДТВЕРЖДЕНИЯ! Для удаления файла '{filePath}' ИИ должен спросить у пользователя " +
                        $"разрешение. Если пользователь согласен, вызовите этот инструмент повторно, " +
                        $"передав в параметр confirmToken значение: {generatedToken}"
                };
            }

            string cacheKey = NormalizeCacheKey(absoluteFilePath);

            bool confirmed = false;
            lock (_pendingConfirmationsLock)
            {
                if (_pendingConfirmations.TryGetValue(cacheKey, out ConfirmationRecord? record))
                {
                    if (string.Equals(record!.Token, confirmToken?.Trim(), StringComparison.OrdinalIgnoreCase) && record.TryConsume())
                    {
                        _pendingConfirmations.Remove(cacheKey);
                        confirmed = true;
                    }
                    else
                    {
                        _logger.LogWarning("Deletion confirmation rejected: invalid or already used token. FilePath='{FilePath}'", absoluteFilePath);
                        return new OperationResult() { IsSuccess = false, Message = $"[ОТКЛОНЕНО]: Неверный или уже использованный код подтверждения '{confirmToken}' для удаления файла '{filePath}'. Доступ заблокирован." };
                    }
                }
            }

            if (confirmed)
            {
                _logger.LogInformation("Deletion confirmed. FilePath='{FilePath}'", absoluteFilePath);
                return await _fileService.FileDeleteAsync(absoluteFilePath);
            }

            _logger.LogWarning("Deletion confirmation expired or not initialized. FilePath='{FilePath}'", absoluteFilePath);
            return new OperationResult()
            {
                IsSuccess = false,
                Message = $"[ОШИБКА]: Время ожидания подтверждения для файла '{filePath}' истекло (5 минут) либо запрос не был инициализирован. Запустите процедуру удаления заново без токена."
            };
        }

    }
}
