using System.Security.Cryptography;
using System.Text;

using FileAccessMcpServer.Core;
using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Вспомогательный сервис для операций с файлами и чтения/записи содержимого.
/// </summary>
namespace FileAccessMcpServer.Services
{
    /// <summary>
    /// Реализует интерфейс IFileOperations и предоставляет методы для работы с файлами.
    /// </summary>
    internal class FileService : IFileOperations
    {
        private readonly ILogger<FileService> _logger;
        private readonly IOptionsMonitor<AppOptions> _fileOptions;
        private readonly IBackupOperations _backupOperations;

        /// <summary>
        /// Инициализирует экземпляр класса FileService.
        /// </summary>
        /// <param name="logger">Логгер для записи событий.</param>
        /// <param name="fileOptions">Монитор конфигурационных опций (максимальные размеры файлов, поддержка hot reload).</param>
        /// <param name="backupOperations">Сервис резервного копирования.</param>
        public FileService(ILogger<FileService> logger, IOptionsMonitor<AppOptions> fileOptions, IBackupOperations backupOperations)
        {
            _logger = logger;
            _fileOptions = fileOptions;
            _backupOperations = backupOperations;
        }

        /// <summary>
        /// Возвращает кодировку по имени с резервным значением по умолчанию.
        /// </summary>
        /// <param name="encoding">Имя кодировки (например, "utf-8", "windows-1251").</param>
        /// <param name="fallback">Резервная кодировка, если указанная не найдена.</param>
        /// <returns>Объект Encoding.</returns>
        private Encoding GetEncoding(string encoding, string fallback = "utf-8")
        {
            var resolved = Helpers.TryGetEncoding(encoding);
            if (resolved != null)
                return resolved;

            _logger.LogError("Failed to get encoding: {Encoding}", encoding);
            return Encoding.GetEncoding(fallback);
        }

        /// <summary>
        /// Откатывает критическую операцию после прерывания/отмены: при необходимости восстанавливает
        /// целевой файл из бэкапа (на случай частичной записи) и удаляет бэкап, чтобы не оставлять
        /// «сиротские» .bak-файлы. Ошибки восстановления/удаления только логируются (best-effort).
        /// </summary>
        /// <param name="backupPath">Путь к созданному бэкапу (может быть <c>null</c> — копии нет).</param>
        /// <param name="targetPath">Путь к файлу, который нужно восстановить (если <paramref name="restore"/> = true).</param>
        /// <param name="restore">Восстанавливать ли файл из бэкапа (для write/append/replace — да; для delete, где оригинал не затронут, — нет).</param>
        private async Task RollbackAsync(string? backupPath, string? targetPath, bool restore)
        {
            if (string.IsNullOrEmpty(backupPath))
                return;

            if (restore && !string.IsNullOrEmpty(targetPath))
            {
                var restoreResult = await _backupOperations.RestoreBackupFileAsync(backupPath, targetPath);
                if (!restoreResult.IsSuccess)
                    _logger.LogWarning("Backup restore failed during rollback. Backup='{Backup}', Target='{Target}', Reason='{Reason}'", backupPath, targetPath, restoreResult.Message);
            }

            var deleteResult = await _backupOperations.DeleteBackupFileAsync(backupPath);
            if (!deleteResult.IsSuccess)
                _logger.LogWarning("Backup delete failed during rollback. Backup='{Backup}', Reason='{Reason}'", backupPath, deleteResult.Message);
        }

        /// <summary>
        /// Получает информацию о файле.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <returns>JSON-объект с информацией (имя, расширение, тип, размер, даты создания/изменения и т.д.).</returns>
        /// <exception cref="FileNotFoundException">Если файл не существует.</exception>
        /// <exception cref="UnauthorizedAccessException">Если нет прав доступа к файлу.</exception>
        public async Task<FileInfoResult> FileGetInfoAsync(string filePath)
        {
            _logger.LogDebug("Getting file info. FilePath='{FilePath}'", filePath);

            try
            {
                var fileInfo = new FileInfo(filePath);
                var result = new FileInfoResult()
                {
                    IsSuccess = true,
                    Name = fileInfo.Name,
                    Extension = fileInfo.Extension,
                    FileType = Helpers.GetContentTypeFromExtension(fileInfo.Extension),
                    Length = fileInfo.Length,
                    FileCreate = fileInfo.CreationTime,
                    FileLastUpdate = fileInfo.LastWriteTime,
                    FileLastAccessTime = fileInfo.LastAccessTime,
                    IsReadOnly = fileInfo.IsReadOnly,
                    IsLink = fileInfo.LinkTarget != null ? true : false,
                    LinkTarget = fileInfo.LinkTarget,
                    Code = 200
                };

                _logger.LogInformation("File info retrieved. FilePath='{FilePath}', Size={Size} bytes, Type='{FileType}', IsLink={IsLink}", filePath, fileInfo.Length, result.FileType, result.IsLink);
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to read file info. FilePath='{FilePath}'", filePath);
                return new FileInfoResult() { IsSuccess = false, Message = $"Security error: You do not have permission to read the file {filePath}." };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while reading file info. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileInfoResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while reading file info. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileInfoResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

        /// <summary>
        /// Считывает содержимое файла.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>JSON-объект с содержимым файла и информацией о размере.</returns>
        /// <exception cref="FileNotFoundException">Если файл не существует или слишком большой.</exception>
        /// <exception cref="UnauthorizedAccessException">Если нет прав доступа к файлу.</exception>
        public async Task<FileReadResult> FileReadAsync(string filePath, string encoding = "utf-8")
        {
            _logger.LogDebug("Reading file. FilePath='{FilePath}', Encoding='{Encoding}'", filePath, encoding);

            Encoding textEncoding = GetEncoding(encoding);

            try
            {
                var fileInfo = new FileInfo(filePath);

                if (fileInfo.Length > _fileOptions.CurrentValue.MaxOneFileSizeInBytes)
                {
                    long maxMb = _fileOptions.CurrentValue.MaxOneFileSizeInBytes / Consts.Megabyte;
                    _logger.LogWarning("File is too large to read. FilePath='{FilePath}', Size={Size} bytes, MaxSize={MaxSize} bytes", filePath, fileInfo.Length, _fileOptions.CurrentValue.MaxOneFileSizeInBytes);
                    return new FileReadResult() { IsSuccess = false, Message = $"File too large ({fileInfo.Length / Consts.Megabyte} MiB). The maximum size to read is {maxMb} MiB." };
                }

                string fileContent = await File.ReadAllTextAsync(filePath, textEncoding);

                _logger.LogInformation("File read successfully. FilePath='{FilePath}', Size={Size} bytes, Encoding='{Encoding}'", filePath, fileInfo.Length, encoding);
                return new FileReadResult()
                {
                    IsSuccess = true,
                    Path = filePath,
                    SizeInBytes = fileInfo.Length,
                    Content = fileContent,
                    Code = 200
                };
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("File not found while reading. FilePath='{FilePath}'", filePath);
                return new FileReadResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while reading. FilePath='{FilePath}'", filePath);
                return new FileReadResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to read file. FilePath='{FilePath}'", filePath);
                return new FileReadResult() { IsSuccess = false, Message = $"Security error: You do not have permission to read the file {filePath}." };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while reading file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileReadResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while reading file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileReadResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

        /// <summary>
        /// Считывает изображение из файла и возвращает его в формате Base64 data URL.
        /// Проверяет размер файла на соответствие максимальному лимиту.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу изображения.</param>
        /// <returns>FileImageResult с Base64 data URL и MIME-типом.</returns>
        public async Task<FileImageResult> FileImageReadAsync(string filePath)
        {
            _logger.LogDebug("Reading image. FilePath='{FilePath}'", filePath);

            var fileInfo = new FileInfo(filePath);

            string ext = Path.GetExtension(filePath).ToLower();
            string mimeType = Helpers.GetContentTypeFromExtension(ext);

            try
            {
                if (fileInfo.Length > _fileOptions.CurrentValue.MaxOneFileSizeInBytes)
                {
                    long maxMb = _fileOptions.CurrentValue.MaxOneFileSizeInBytes / Consts.Megabyte;
                    _logger.LogWarning("Image is too large to read. FilePath='{FilePath}', Size={Size} bytes, MaxSize={MaxSize} bytes", filePath, fileInfo.Length, _fileOptions.CurrentValue.MaxOneFileSizeInBytes);
                    return new FileImageResult()
                    {
                        IsSuccess = false,
                        Message = $"File too large ({fileInfo.Length / Consts.Megabyte} MiB). The maximum size to read is {maxMb} MiB."
                    };
                }

                byte[] imageBytes = await File.ReadAllBytesAsync(filePath);
                string base64Data = Convert.ToBase64String(imageBytes);

                _logger.LogInformation("Image read successfully. FilePath='{FilePath}', Size={Size} bytes, MimeType='{MimeType}'", filePath, fileInfo.Length, mimeType);
                return new FileImageResult()
                {
                    IsSuccess = true,
                    FilePath = filePath,
                    MimeType = mimeType,
                    SizeInBytes = fileInfo.Length,
                    DataUrl = $"data:{mimeType};base64,{base64Data}",
                    Code = 200
                };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to read image. FilePath='{FilePath}'", filePath);
                return new FileImageResult() { IsSuccess = false, Message = $"Security error: You do not have permission to read the file {filePath}." };
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("Image not found. FilePath='{FilePath}'", filePath);
                return new FileImageResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while reading image. FilePath='{FilePath}'", filePath);
                return new FileImageResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while reading image. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileImageResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while reading image. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileImageResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

        /// <summary>
        /// Считывает содержимое бинарного файла и возвращает его в формате Base64.
        /// Проверяет размер файла на соответствие максимальному лимиту.
        /// </summary>
        /// <param name="filePath">Полный путь к бинарному файлу.</param>
        /// <returns>FileBinaryReadResult с Base64-представлением содержимого и MIME-типом.</returns>
        public async Task<FileBinaryReadResult> FileBinaryReadAsync(string filePath)
        {
            _logger.LogDebug("Reading binary file. FilePath='{FilePath}'", filePath);

            try
            {
                var fileInfo = new FileInfo(filePath);

                if (fileInfo.Length > _fileOptions.CurrentValue.MaxOneFileSizeInBytes)
                {
                    long maxMb = _fileOptions.CurrentValue.MaxOneFileSizeInBytes / Consts.Megabyte;
                    _logger.LogWarning("Binary file is too large to read. FilePath='{FilePath}', Size={Size} bytes, MaxSize={MaxSize} bytes", filePath, fileInfo.Length, _fileOptions.CurrentValue.MaxOneFileSizeInBytes);
                    return new FileBinaryReadResult()
                    {
                        IsSuccess = false,
                        Message = $"File too large ({fileInfo.Length / Consts.Megabyte} MiB). The maximum size to read is {maxMb} MiB."
                    };
                }

                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
                string base64Data = Convert.ToBase64String(fileBytes);
                string mimeType = Helpers.GetContentTypeFromExtension(Path.GetExtension(filePath));

                _logger.LogInformation("Binary file read successfully. FilePath='{FilePath}', Size={Size} bytes, MimeType='{MimeType}'", filePath, fileInfo.Length, mimeType);
                return new FileBinaryReadResult()
                {
                    IsSuccess = true,
                    FilePath = filePath,
                    MimeType = mimeType,
                    SizeInBytes = fileInfo.Length,
                    Base64Data = base64Data,
                    Code = 200
                };
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("Binary file not found. FilePath='{FilePath}'", filePath);
                return new FileBinaryReadResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while reading binary file. FilePath='{FilePath}'", filePath);
                return new FileBinaryReadResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to read binary file. FilePath='{FilePath}'", filePath);
                return new FileBinaryReadResult() { IsSuccess = false, Message = $"Security error: You do not have permission to read the file {filePath}." };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while reading binary file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileBinaryReadResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while reading binary file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileBinaryReadResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

        /// <summary>
        /// Записывает содержимое файла.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="content">Содержимое, которое нужно записать в файл.</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>JSON-объект с подтверждением успешной записи.</returns>
        /// <exception cref="DirectoryNotFoundException">Если директория не существует.</exception>
        /// <exception cref="UnauthorizedAccessException">Если нет прав доступа к записи файла.</exception>
        public async Task<FileWriteResult> FileWriteAsync(string filePath, string content, string encoding = "utf-8")
        {
            if (content == null) content = string.Empty;
            _logger.LogDebug("Writing file. FilePath='{FilePath}', Encoding='{Encoding}', ContentLength={ContentLength} chars", filePath, encoding, content.Length);

            try
            {
                Encoding textEncoding = GetEncoding(encoding);
                long contentSizeBytes = textEncoding.GetByteCount(content);
                if (contentSizeBytes > _fileOptions.CurrentValue.MaxOneFileSizeInBytes)
                {
                    long maxMb = _fileOptions.CurrentValue.MaxOneFileSizeInBytes / Consts.Megabyte;
                    _logger.LogWarning("Content is too large to write. FilePath='{FilePath}', ContentSize={Size} bytes, MaxSize={MaxSize} bytes", filePath, contentSizeBytes, _fileOptions.CurrentValue.MaxOneFileSizeInBytes);
                    return new FileWriteResult() { IsSuccess = false, Message = $"Content size too large ({contentSizeBytes / Consts.Megabyte} MiB). Maximum write limit is {maxMb} MiB" };
                }

                string? parentDirectory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(parentDirectory) && !Directory.Exists(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                    _logger.LogDebug("Created parent directory for write. Path='{ParentDirectory}'", parentDirectory);
                }

                var backupResult = await _backupOperations.CreateBackupFileAsync(filePath);
                if (!backupResult.IsSuccess)
                    return new FileWriteResult() { IsSuccess = false, Message = $"Backup operation for {filePath} failed. Error message {backupResult.Message}" };

                try
                {
                    await File.WriteAllTextAsync(filePath, content, textEncoding);
                }
                catch
                {
                    await RollbackAsync(backupResult.BackupPath, filePath, restore: true);
                    throw;
                }

                _logger.LogInformation("File written successfully. FilePath='{FilePath}', BytesWritten={BytesWritten} bytes", filePath, contentSizeBytes);
                return new FileWriteResult()
                {
                    IsSuccess = true,
                    Message = $"File saved successfully: '{filePath}'",
                    Path = filePath,
                    BytesWritten = contentSizeBytes,
                    Code = 200
                };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to write file. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"Security error: You do not have permission to write the file {filePath}" };
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("File not found while writing. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while writing. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while writing file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileWriteResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while writing file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileWriteResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

        /// <summary>
        /// Добавляет содержимое в конец файла. Если файл не существует — создаёт его.
        /// Перед изменением существующего файла создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="content">Содержимое, которое нужно добавить в конец файла.</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>FileWriteResult с количеством добавленных байт.</returns>
        public async Task<FileWriteResult> FileAppendAsync(string filePath, string content, string encoding = "utf-8")
        {
            if (content == null) content = string.Empty;
            _logger.LogDebug("Appending to file. FilePath='{FilePath}', Encoding='{Encoding}', ContentLength={ContentLength} chars", filePath, encoding, content.Length);

            try
            {
                Encoding textEncoding = GetEncoding(encoding);
                long contentSizeBytes = textEncoding.GetByteCount(content);

                bool fileExists = File.Exists(filePath);
                long existingSize = fileExists ? new FileInfo(filePath).Length : 0;
                if (existingSize + contentSizeBytes > _fileOptions.CurrentValue.MaxOneFileSizeInBytes)
                {
                    long maxMb = _fileOptions.CurrentValue.MaxOneFileSizeInBytes / Consts.Megabyte;
                    _logger.LogWarning("Content is too large to append. FilePath='{FilePath}', ExistingSize={ExistingSize} bytes, ContentSize={ContentSize} bytes, MaxSize={MaxSize} bytes", filePath, existingSize, contentSizeBytes, _fileOptions.CurrentValue.MaxOneFileSizeInBytes);
                    return new FileWriteResult() { IsSuccess = false, Message = $"Content size too large ({contentSizeBytes / Consts.Megabyte} MiB). Maximum total file size is {maxMb} MiB." };
                }

                string? parentDirectory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(parentDirectory) && !Directory.Exists(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                    _logger.LogDebug("Created parent directory for append. Path='{ParentDirectory}'", parentDirectory);
                }

                string? backupPath = null;
                if (fileExists)
                {
                    var backupResult = await _backupOperations.CreateBackupFileAsync(filePath);
                    if (!backupResult.IsSuccess)
                        return new FileWriteResult() { IsSuccess = false, Message = $"Backup operation for {filePath} failed. Error message {backupResult.Message}" };
                    backupPath = backupResult.BackupPath;
                }

                try
                {
                    await File.AppendAllTextAsync(filePath, content, textEncoding);
                }
                catch
                {
                    await RollbackAsync(backupPath, filePath, restore: true);
                    throw;
                }

                _logger.LogInformation("Content appended successfully. FilePath='{FilePath}', BytesWritten={BytesWritten} bytes", filePath, contentSizeBytes);
                return new FileWriteResult()
                {
                    IsSuccess = true,
                    Message = $"Content appended to file successfully: '{filePath}'",
                    Path = filePath,
                    BytesWritten = contentSizeBytes,
                    Code = 200
                };
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("File not found while appending. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while appending. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to append to file. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"Security error: You do not have permission to write the file {filePath}" };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while appending to file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileWriteResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while appending to file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileWriteResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

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
        public async Task<FileWriteResult> FileReplaceLinesAsync(string filePath, long startLine, long lineCount, string newContent, string encoding = "utf-8")
        {
            if (newContent == null) newContent = string.Empty;
            _logger.LogDebug("Replacing lines in file. FilePath='{FilePath}', StartLine={StartLine}, LineCount={LineCount}, NewContentLength={NewContentLength} chars, Encoding='{Encoding}'", filePath, startLine, lineCount, newContent.Length, encoding);

            try
            {
                if (startLine < 1)
                {
                    _logger.LogWarning("Invalid startLine value. FilePath='{FilePath}', StartLine={StartLine}", filePath, startLine);
                    return new FileWriteResult() { IsSuccess = false, Message = $"Invalid startLine value '{startLine}'. startLine must be >= 1." };
                }

                if (lineCount < 1)
                {
                    _logger.LogWarning("Invalid lineCount value. FilePath='{FilePath}', LineCount={LineCount}", filePath, lineCount);
                    return new FileWriteResult() { IsSuccess = false, Message = $"Invalid lineCount value '{lineCount}'. lineCount must be >= 1." };
                }

                Encoding textEncoding = GetEncoding(encoding);
                string originalText = await File.ReadAllTextAsync(filePath, textEncoding);

                bool hasTrailingNewline = originalText.EndsWith("\n") || originalText.EndsWith("\r");
                var parts = originalText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).ToList();
                if (hasTrailingNewline && parts.Count > 0 && parts[^1].Length == 0)
                    parts.RemoveAt(parts.Count - 1);

                int totalLines = parts.Count;
                int startIndex = (int)(startLine - 1);

                if (startLine > totalLines)
                {
                    _logger.LogWarning("Line range out of bounds. FilePath='{FilePath}', StartLine={StartLine}, TotalLines={TotalLines}", filePath, startLine, totalLines);
                    return new FileWriteResult() { IsSuccess = false, Message = $"Line range is out of bounds: file '{filePath}' has {totalLines} lines, but startLine is {startLine}." };
                }

                if (lineCount > totalLines - startIndex)
                {
                    _logger.LogWarning("Line range out of bounds. FilePath='{FilePath}', StartLine={StartLine}, LineCount={LineCount}, TotalLines={TotalLines}", filePath, startLine, lineCount, totalLines);
                    return new FileWriteResult() { IsSuccess = false, Message = $"Line range is out of bounds: file '{filePath}' has {totalLines} lines, but requested to replace lines {startLine}..{startLine + lineCount - 1}." };
                }

                int removeCount = (int)lineCount;
                var replacementLines = new List<string>();
                if (!string.IsNullOrEmpty(newContent))
                {
                    replacementLines = newContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).ToList();
                    if (replacementLines.Count > 0 && replacementLines[^1].Length == 0 && (newContent.EndsWith("\n") || newContent.EndsWith("\r")))
                        replacementLines.RemoveAt(replacementLines.Count - 1);
                }

                string newline = originalText.Contains("\r\n") ? "\r\n" : "\n";

                var result = new List<string>(totalLines - removeCount + replacementLines.Count);
                result.AddRange(parts.Take(startIndex));
                result.AddRange(replacementLines);
                result.AddRange(parts.Skip(startIndex + removeCount));

                string resultText = string.Join(newline, result);
                if (hasTrailingNewline) resultText += newline;

                long resultSizeBytes = textEncoding.GetByteCount(resultText);
                if (resultSizeBytes > _fileOptions.CurrentValue.MaxOneFileSizeInBytes)
                {
                    long maxMb = _fileOptions.CurrentValue.MaxOneFileSizeInBytes / Consts.Megabyte;
                    _logger.LogWarning("Resulting file is too large. FilePath='{FilePath}', ResultSize={ResultSize} bytes, MaxSize={MaxSize} bytes", filePath, resultSizeBytes, _fileOptions.CurrentValue.MaxOneFileSizeInBytes);
                    return new FileWriteResult() { IsSuccess = false, Message = $"Resulting file size too large ({resultSizeBytes / Consts.Megabyte} MiB). Maximum file size is {maxMb} MiB." };
                }

                var backupResult = await _backupOperations.CreateBackupFileAsync(filePath);
                if (!backupResult.IsSuccess)
                    return new FileWriteResult() { IsSuccess = false, Message = $"Backup operation for {filePath} failed. Error message {backupResult.Message}" };

                try
                {
                    await File.WriteAllTextAsync(filePath, resultText, textEncoding);
                }
                catch
                {
                    await RollbackAsync(backupResult.BackupPath, filePath, restore: true);
                    throw;
                }

                _logger.LogInformation("Lines replaced successfully. FilePath='{FilePath}', StartLine={StartLine}, LineCount={LineCount}, BytesWritten={BytesWritten} bytes", filePath, startLine, lineCount, resultSizeBytes);
                return new FileWriteResult()
                {
                    IsSuccess = true,
                    Message = $"Lines replaced successfully: '{filePath}'",
                    Path = filePath,
                    BytesWritten = resultSizeBytes,
                    Code = 200
                };
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("File not found while replacing lines. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while replacing lines. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to replace lines in file. FilePath='{FilePath}'", filePath);
                return new FileWriteResult() { IsSuccess = false, Message = $"Security error: You do not have permission to write the file {filePath}" };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while replacing lines in file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileWriteResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while replacing lines in file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileWriteResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

        /// <summary>
        /// Считывает содержимое файла частями (блоками символов).
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="startChar">Символ, с которого начать чтение (индексация с 0, по умолчанию 0).</param>
        /// <param name="charsCount">Количество символов для чтения в одном блоке (по умолчанию 1000000).</param>
        /// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
        /// <returns>JSON-объект с содержимым файла по частям.</returns>
        public async Task<FileReadChunkResult> FileReadChunksAsync(string filePath, long startChar = 0, int charsCount = 1000000, string encoding = "utf-8")
        {
            _logger.LogDebug("Reading file chunks. FilePath='{FilePath}', StartChar={StartChar}, CharsCount={CharsCount}, Encoding='{Encoding}'", filePath, startChar, charsCount, encoding);

            if (startChar < 0)
            {
                _logger.LogWarning("Invalid startChar value. FilePath='{FilePath}', StartChar={StartChar}", filePath, startChar);
                return new FileReadChunkResult() { IsSuccess = false, Message = $"Invalid startChar value '{startChar}'. startChar must be >= 0." };
            }

            if (charsCount < 1 || charsCount > Consts.MaxChunkChars)
            {
                _logger.LogWarning("Invalid charsCount value. FilePath='{FilePath}', CharsCount={CharsCount}, MaxChars={MaxChars}", filePath, charsCount, Consts.MaxChunkChars);
                return new FileReadChunkResult() { IsSuccess = false, Message = $"Invalid charsCount value '{charsCount}'. charsCount must be in range [1..{Consts.MaxChunkChars}]." };
            }

            Encoding textEncoding = GetEncoding(encoding);

            try
            {
                // +4 символа: буфер с запасом на случай переноса surrogate-пары между блоками.
                var buffer = new char[charsCount + 4];
                int totalRead = 0;
                bool hasMore = false;

                using (var reader = new StreamReader(filePath, textEncoding, detectEncodingFromByteOrderMarks: true))
                {
                    // StreamReader не умеет Seek по символам — пропускаем startChar символов повторным чтением.
                    if (startChar > 0)
                    {
                        var skipBuffer = new char[8192];
                        long skipped = 0;
                        while (skipped < startChar)
                        {
                            int read = await reader.ReadBlockAsync(skipBuffer, 0, (int)Math.Min(startChar - skipped, skipBuffer.Length));
                            if (read == 0) break;
                            skipped += read;
                        }
                    }

                    while (totalRead < charsCount)
                    {
                        int read = await reader.ReadBlockAsync(buffer, totalRead, charsCount - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    // Блок не должен заканчиваться на high surrogate (первая половина пары):
                    // дочитываем несколько символов, чтобы пара попала в текущий блок целиком.
                    int extraChars = 0;
                    if (totalRead == charsCount && totalRead > 0 && char.IsHighSurrogate(buffer[totalRead - 1]))
                    {
                        var extra = new char[4];
                        int extraRead = await reader.ReadBlockAsync(extra, 0, extra.Length);
                        for (int i = 0; i < extraRead && extraChars < 4; i++)
                        {
                            buffer[totalRead + i] = extra[i];
                            extraChars++;
                            if (char.IsLowSurrogate(extra[i])) break;
                        }
                    }

                    totalRead += extraChars;

                    if (totalRead >= charsCount)
                    {
                        var peek = new char[1];
                        hasMore = await reader.ReadBlockAsync(peek, 0, 1) > 0;
                    }
                }

                _logger.LogInformation("File chunks read. FilePath='{FilePath}', StartChar={StartChar}, CharsReturned={CharsReturned}, HasMore={HasMore}", filePath, startChar, totalRead, hasMore);
                return new FileReadChunkResult()
                {
                    IsSuccess = true,
                    Code = 200,
                    Path = filePath,
                    StartChar = startChar,
                    CharsReturned = totalRead,
                    TotalCharsReadUpTo = startChar + totalRead,
                    HasMore = hasMore,
                    NextStartChar = hasMore ? startChar + totalRead : (long?)null,
                    Content = new string(buffer, 0, totalRead)
                };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to read file chunks. FilePath='{FilePath}'", filePath);
                return new FileReadChunkResult() { IsSuccess = false, Message = $"Security error: You do not have permission to read the file {filePath}." };
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("File not found while reading chunks. FilePath='{FilePath}'", filePath);
                return new FileReadChunkResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while reading chunks. FilePath='{FilePath}'", filePath);
                return new FileReadChunkResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while reading file chunks. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileReadChunkResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while reading file chunks. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileReadChunkResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

        /// <summary>
        /// Перемещает (переименовывает) файл внутри песочницы.
        /// Автоматически создает недостающие директории в пути назначения.
        /// Перед перезаписью существующего целевого файла создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к исходному файлу.</param>
        /// <param name="newFilePath">Полный путь к новому расположению файла.</param>
        /// <param name="isRewrite"><c>true</c>, чтобы перезаписывать существующий целевой файл.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        public async Task<OperationResult> FileMoveAsync(string filePath, string newFilePath, bool isRewrite = false)
        {
            _logger.LogDebug("Move file. Source='{FilePath}', Destination='{NewFilePath}', IsRewrite={IsRewrite}", filePath, newFilePath, isRewrite);

            try
            {
                string? destinationDirectory = Path.GetDirectoryName(newFilePath);
                if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                    _logger.LogDebug("Created destination directory for move. Path='{DestinationDirectory}'", destinationDirectory);
                }

                string? backupPath = null;
                if (Path.Exists(newFilePath))
                {
                    if (!isRewrite)
                        return OperationResult.Fail($"Destination file already exists: '{newFilePath}'. Use isRewrite=true to overwrite.");

                    var result = await _backupOperations.CreateBackupFileAsync(newFilePath);
                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("Backup failed before move. Destination='{NewFilePath}', Reason='{Reason}'", newFilePath, result.Message);
                        return result;
                    }
                    backupPath = result.BackupPath;
                }

                try
                {
                    File.Move(filePath, newFilePath, isRewrite);
                }
                catch
                {
                    await RollbackAsync(backupPath, newFilePath, restore: true);
                    throw;
                }

                _logger.LogInformation("File moved. Source='{FilePath}', Destination='{NewFilePath}', IsRewrite={IsRewrite}", filePath, newFilePath, isRewrite);
                return OperationResult.Success();
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to move file. Source='{FilePath}', Destination='{NewFilePath}'", filePath, newFilePath);
                return OperationResult.Fail($"Security error: You do not have permission to move the file {filePath}.");
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("File not found while moving. Source='{FilePath}', Destination='{NewFilePath}'", filePath, newFilePath);
                return new OperationResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while moving. Source='{FilePath}', Destination='{NewFilePath}'", filePath, newFilePath);
                return new OperationResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while moving file. Source='{FilePath}', Destination='{NewFilePath}', CorrelationId='{CorrelationId}'", filePath, newFilePath, correlationId);
                return OperationResult.Fail($"An input/output error occurred. Correlation ID: {correlationId}");
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while moving file. Source='{FilePath}', Destination='{NewFilePath}', CorrelationId='{CorrelationId}'", filePath, newFilePath, correlationId);
                return OperationResult.Fail($"An internal error occurred. Correlation ID: {correlationId}");
            }
        }

        /// <summary>
        /// Копирует файл внутри песочницы.
        /// Автоматически создает недостающие директории в пути назначения.
        /// Перед перезаписью существующего целевого файла создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к исходному файлу.</param>
        /// <param name="newFilePath">Полный путь к копии файла.</param>
        /// <param name="isRewrite"><c>true</c>, чтобы перезаписывать существующий целевой файл.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        public async Task<OperationResult> FileCopyAsync(string filePath, string newFilePath, bool isRewrite = false)
        {
            _logger.LogDebug("Copy file. Source='{FilePath}', Destination='{NewFilePath}', IsRewrite={IsRewrite}", filePath, newFilePath, isRewrite);

            try
            {
                string? destinationDirectory = Path.GetDirectoryName(newFilePath);
                if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                    _logger.LogDebug("Created destination directory for copy. Path='{DestinationDirectory}'", destinationDirectory);
                }

                string? backupPath = null;
                if (Path.Exists(newFilePath))
                {
                    if (!isRewrite)
                        return OperationResult.Fail($"Destination file already exists: '{newFilePath}'. Use isRewrite=true to overwrite.");

                    var result = await _backupOperations.CreateBackupFileAsync(newFilePath);
                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("Backup failed before copy. Destination='{NewFilePath}', Reason='{Reason}'", newFilePath, result.Message);
                        return result;
                    }
                    backupPath = result.BackupPath;
                }

                try
                {
                    using var originFile = new FileStream(filePath, FileMode.Open);
                    using var copyFile = new FileStream(newFilePath, isRewrite ? FileMode.Create : FileMode.CreateNew);
                    await originFile.CopyToAsync(copyFile);
                }
                catch
                {
                    await RollbackAsync(backupPath, newFilePath, restore: true);
                    throw;
                }

                _logger.LogInformation("File copied. Source='{FilePath}', Destination='{NewFilePath}', IsRewrite={IsRewrite}", filePath, newFilePath, isRewrite);
                return OperationResult.Success();
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to copy file. Source='{FilePath}', Destination='{NewFilePath}'", filePath, newFilePath);
                return OperationResult.Fail($"Security error: You do not have permission to copy the file {filePath}.");
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("File not found while copying. Source='{FilePath}', Destination='{NewFilePath}'", filePath, newFilePath);
                return new OperationResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while copying. Source='{FilePath}', Destination='{NewFilePath}'", filePath, newFilePath);
                return new OperationResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while copying file. Source='{FilePath}', Destination='{NewFilePath}', CorrelationId='{CorrelationId}'", filePath, newFilePath, correlationId);
                return OperationResult.Fail($"An input/output error occurred. Correlation ID: {correlationId}");
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while copying file. Source='{FilePath}', Destination='{NewFilePath}', CorrelationId='{CorrelationId}'", filePath, newFilePath, correlationId);
                return OperationResult.Fail($"An internal error occurred. Correlation ID: {correlationId}");
            }
        }

        /// <summary>
        /// Удаляет файл внутри песочницы. Перед удалением создаётся резервная копия.
        /// </summary>
        /// <param name="filePath">Полный путь к удаляемому файлу.</param>
        /// <returns>OperationResult с результатом операции.</returns>
        public async Task<OperationResult> FileDeleteAsync(string filePath)
        {
            _logger.LogDebug("Delete file. FilePath='{FilePath}'", filePath);

            if (!Path.Exists(filePath))
            {
                _logger.LogWarning("File not found for delete. FilePath='{FilePath}'", filePath);
                return new OperationResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }

            try
            {
                string? backupPath = null;
                if (Path.Exists(filePath))
                {
                    var result = await _backupOperations.CreateBackupFileAsync(filePath);
                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("Backup failed before delete. FilePath='{FilePath}', Reason='{Reason}'", filePath, result.Message);
                        return result;
                    }
                    backupPath = result.BackupPath;
                }

                try
                {
                    File.Delete(filePath);
                }
                catch
                {
                    await RollbackAsync(backupPath, filePath, restore: false);
                    throw;
                }

                _logger.LogInformation("File deleted. FilePath='{FilePath}'", filePath);
                return OperationResult.Success();
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to delete file. FilePath='{FilePath}'", filePath);
                return OperationResult.Fail($"Security error: You do not have permission to delete the file {filePath}.");
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while deleting file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return OperationResult.Fail($"An input/output error occurred. Correlation ID: {correlationId}");
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while deleting file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return OperationResult.Fail($"An internal error occurred. Correlation ID: {correlationId}");
            }
        }

        /// <summary>
        /// Вычисляет хеш (контрольную сумму) файла указанным алгоритмом.
        /// </summary>
        /// <param name="filePath">Полный путь к файлу.</param>
        /// <param name="algorithm">Название алгоритма: "md5", "crc32", "sha256" или "sha512".</param>
        /// <returns>FileHashResult с хешем в hex-виде (нижний регистр) и размером файла.</returns>
        public async Task<FileHashResult> FileHashAsync(string filePath, string algorithm)
        {
            _logger.LogDebug("Hashing file. FilePath='{FilePath}', Algorithm='{Algorithm}'", filePath, algorithm);

            string normalized = (algorithm ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized is not ("md5" or "crc32" or "sha256" or "sha512"))
            {
                _logger.LogWarning("Unsupported hash algorithm. Algorithm='{Algorithm}'", algorithm);
                return new FileHashResult() { IsSuccess = false, Message = $"Unsupported hash algorithm '{algorithm}'. Supported algorithms: md5, crc32, sha256, sha512." };
            }

            try
            {
                var fileInfo = new FileInfo(filePath);
                string hashHex;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    switch (normalized)
                    {
                        case "md5":
                            using (var md5 = MD5.Create())
                                hashHex = await ComputeHashHexAsync(md5, stream);
                            break;
                        case "sha256":
                            using (var sha256 = SHA256.Create())
                                hashHex = await ComputeHashHexAsync(sha256, stream);
                            break;
                        case "sha512":
                            using (var sha512 = SHA512.Create())
                                hashHex = await ComputeHashHexAsync(sha512, stream);
                            break;
                        case "crc32":
                            hashHex = await ComputeCrc32HexAsync(stream);
                            break;
                        default:
                            return new FileHashResult() { IsSuccess = false, Message = $"Unsupported hash algorithm '{algorithm}'. Supported algorithms: md5, crc32, sha256, sha512." };
                    }
                }

                _logger.LogInformation("File hashed. FilePath='{FilePath}', Algorithm='{Algorithm}', Size={Size} bytes, Hash='{Hash}'", filePath, normalized, fileInfo.Length, hashHex);
                return new FileHashResult()
                {
                    IsSuccess = true,
                    Path = filePath,
                    Algorithm = normalized,
                    Hash = hashHex,
                    SizeInBytes = fileInfo.Length,
                    Code = 200
                };
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("File not found while hashing. FilePath='{FilePath}'", filePath);
                return new FileHashResult() { IsSuccess = false, Message = $"File not found: {filePath}", Code = 404 };
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while hashing. FilePath='{FilePath}'", filePath);
                return new FileHashResult() { IsSuccess = false, Message = $"Directory not found: {filePath}", Code = 404 };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to hash file. FilePath='{FilePath}'", filePath);
                return new FileHashResult() { IsSuccess = false, Message = $"Security error: You do not have permission to read the file {filePath}." };
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while hashing file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileHashResult() { IsSuccess = false, Message = $"An input/output error occurred. Correlation ID: {correlationId}" };
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while hashing file. FilePath='{FilePath}', CorrelationId='{CorrelationId}'", filePath, correlationId);
                return new FileHashResult() { IsSuccess = false, Message = $"An internal error occurred. Correlation ID: {correlationId}" };
            }
        }

        /// <summary>
        /// Вычисляет хеш потока с помощью заданного алгоритма и возвращает hex-строку (нижний регистр).
        /// </summary>
        private static async Task<string> ComputeHashHexAsync(HashAlgorithm hashAlgorithm, Stream stream)
        {
            byte[] hash = await hashAlgorithm.ComputeHashAsync(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Вычисляет CRC-32 (IEEE 802.3, полином 0xEDB88320) потока и возвращает hex-строку (нижний регистр).
        /// </summary>
        private static async Task<string> ComputeCrc32HexAsync(Stream stream)
        {
            var crc = new Crc32();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
                crc.Update(buffer, 0, read);
            return crc.Value.ToString("x8");
        }

        /// <summary>
        /// Реализация контрольной суммы CRC-32 (IEEE 802.3).
        /// </summary>
        private sealed class Crc32
        {
            private static readonly uint[] Table = BuildTable();

            private uint _value = 0xFFFFFFFFu;

            private static uint[] BuildTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint crc = i;
                    for (int j = 0; j < 8; j++)
                        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                    table[i] = crc;
                }
                return table;
            }

            public void Update(byte[] buffer, int offset, int count)
            {
                for (int i = offset; i < offset + count; i++)
                    _value = (_value >> 8) ^ Table[(_value ^ buffer[i]) & 0xFF];
            }

            public uint Value => _value ^ 0xFFFFFFFFu;
        }
    }
}
