using System.Text;
using System.Text.RegularExpressions;

using FileAccessMcpServer.Core;
using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Вспомогательный сервис для операций с директориями и построения дерева каталогов.
/// </summary>
namespace FileAccessMcpServer.Services
{
    /// <summary>
    /// Реализует интерфейс IDirectoryOperations и предоставляет методы для работы с директориями.
    /// </summary>
    internal class DirService : IDirectoryOperations
    {
        private readonly ILogger<DirService> _logger;
        private readonly IOptionsMonitor<AppOptions> _fileOptions;

        /// <summary>
        /// Инициализирует экземпляр класса DirService.
        /// </summary>
        /// <param name="logger">Логгер для записи событий.</param>
        /// <param name="fileOptions">Монитор конфигурационных опций (лимиты количества записей, поддержка hot reload).</param>
        public DirService(ILogger<DirService> logger, IOptionsMonitor<AppOptions> fileOptions)
        {
            _logger = logger;
            _fileOptions = fileOptions;
        }

        /// <summary>
        /// Заменяет обратные слеши на прямые в пути для единообразного отображения.
        /// </summary>
        /// <param name="path">Исходный путь.</param>
        /// <returns>Путь с прямыми слешами.</returns>
        private string ReplaceSeparatorInPath(string path)
        {
            return path.Replace("\\", "/");
        }

        /// <summary>
        /// Рекурсивно получает список директорий и файлов в указанной директории.
        /// </summary>
        /// <param name="basePath">Путь к корневой директории.</param>
        /// <param name="nesting">Максимальный уровень вложенности.</param>
        /// <param name="currentNesting">Текущий уровень вложенности.</param>
        /// <param name="withFiles"><c>true</c>, чтобы включать файлы; <c>false</c>, только директории.</param>
        private async Task<List<FSEntrie>> GetDirectoriesWithFilesAsync(string basePath, int nesting = 5, int currentNesting = 0, bool withFiles = true)
        {
            List<FSEntrie> _directories = new List<FSEntrie>();

            if (currentNesting > nesting)
            {
                _logger.LogDebug("Reached nesting limit. CurrentNesting={CurrentNesting}, MaxNesting={Nesting}, Path='{Path}'", currentNesting, nesting, basePath);
                return _directories;
            }

            if (currentNesting == 0)
                _directories.Add(new FSEntrie(ReplaceSeparatorInPath(basePath), "RootDirectory"));

            if (withFiles)
                _directories.AddRange(await GetFilesInDirectoryAsync(basePath));

            if (IsTreeLimitReached(_directories.Count))
                return _directories;

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(basePath);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("No permission to enumerate directories. Path='{Path}'", basePath);
                return _directories;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Input/output error while enumerating directories. Path='{Path}'", basePath);
                return _directories;
            }

            foreach (var dir in dirs)
            {
                _directories.Add(new FSEntrie(ReplaceSeparatorInPath(dir)));

                if (IsTreeLimitReached(_directories.Count))
                    return _directories;

                // Символьные ссылки (symlink/junction) отображаются в дереве, но не обходятся:
                // это исключает бесконечную рекурсию при циклических ссылках внутри песочницы.
                if (currentNesting < nesting && !IsReparsePoint(dir))
                {
                    _directories.AddRange(await GetDirectoriesWithFilesAsync(dir, nesting, currentNesting + 1, withFiles));

                    if (IsTreeLimitReached(_directories.Count))
                        return _directories;
                }
            }

            _logger.LogDebug("Built directory tree. Path='{Path}', Nesting={Nesting}, Entries={Entries}", basePath, currentNesting, _directories.Count);
            return _directories;
        }

        /// <summary>
        /// Определяет, является ли путь символьной ссылкой (reparse point).
        /// При ошибке проверки трактуется как ссылка, чтобы не углубляться в обход.
        /// </summary>
        private static bool IsReparsePoint(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// Проверяет, достигнут ли лимит количества записей дерева.
        /// </summary>
        private bool IsTreeLimitReached(int entriesCount)
        {
            if (_fileOptions.CurrentValue.MaxTreeEntries <= 0)
                return false;

            if (entriesCount >= _fileOptions.CurrentValue.MaxTreeEntries)
            {
                _logger.LogWarning("Directory tree entry limit reached ({Limit}). Result truncated.", _fileOptions.CurrentValue.MaxTreeEntries);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Получает список файлов в указанной директории.
        /// </summary>
        /// <param name="path">Путь к директории.</param>
        /// <returns>Список файлов с указанием размера каждого файла.</returns>
        public async Task<List<FSEntrie>> GetFilesInDirectoryAsync(string path)
        {
            _logger.LogDebug("Getting files in directory. Path='{Path}'", path);

            List<FSEntrie> _files = new List<FSEntrie>();

            try
            {
                var files = Directory.GetFiles(path);
                _files.AddRange(files.Select(f => new FSEntrie(ReplaceSeparatorInPath(f), "File")));

                if (_fileOptions.CurrentValue.MaxTreeEntries > 0 && _files.Count > _fileOptions.CurrentValue.MaxTreeEntries)
                {
                    _logger.LogWarning("File list truncated. Path='{Path}', Files={Count}, Limit={Limit}", path, _files.Count, _fileOptions.CurrentValue.MaxTreeEntries);
                    _files = _files.Take(_fileOptions.CurrentValue.MaxTreeEntries).ToList();
                }

                foreach (FSEntrie file in _files)
                {
                    try
                    {
                        var fi = new FileInfo(file.Name);
                        file.Size = fi.Length;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to get file size. Path='{Path}'", file.Name);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("No permission to list files. Path='{Path}'", path);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Input/output error while listing files. Path='{Path}'", path);
            }

            _logger.LogInformation("Retrieved {Count} files from directory '{Path}'", _files.Count, path);
            return _files;
        }

        /// <summary>
        /// Получает дерево каталогов с файлами.
        /// </summary>
        /// <param name="path">Путь к корневой директории.</param>
        /// <param name="nesting">Уровень вложенности (по умолчанию 5).</param>
        /// <returns>Список всех записей с файлами и поддиректориями.</returns>
        public async Task<List<FSEntrie>> GetDirectoryTreeWithFilesAsync(string path, int nesting)
        {
            _logger.LogDebug("Getting directory tree with files. Path='{Path}', Nesting={Nesting}", path, nesting);
            var result = await GetDirectoriesWithFilesAsync(path, nesting, 0, true);
            _logger.LogInformation("Built directory tree with files. Path='{Path}', Nesting={Nesting}, Entries={Entries}", path, nesting, result.Count);
            return result;
        }

        /// <summary>
        /// Получает дерево каталогов.
        /// </summary>
        /// <param name="path">Путь к корневой директории.</param>
        /// <param name="nesting">Уровень вложенности (по умолчанию 5).</param>
        /// <returns>Список всех записей с директориями и файлами.</returns>
        public async Task<List<FSEntrie>> GetDirectoryTreeAsync(string path, int nesting)
        {
            _logger.LogDebug("Getting directory tree. Path='{Path}', Nesting={Nesting}", path, nesting);
            var result = await GetDirectoriesWithFilesAsync(path, nesting, 0, false);
            _logger.LogInformation("Built directory tree. Path='{Path}', Nesting={Nesting}, Entries={Entries}", path, nesting, result.Count);
            return result;
        }

        /// <summary>
        /// Ищет файлы по маске имени (рекурсивно).
        /// Маска применяется к имени файла; поддерживаются символы '*' и '?'.
        /// Символьные ссылки (symlink/junction) не обходятся — это исключает циклы.
        /// Количество результатов ограничено опцией MaxSearchResults; при превышении
        /// результат усекается и флаг LimitReached устанавливается в true.
        /// </summary>
        /// <param name="path">Путь к корневой директории поиска.</param>
        /// <param name="mask">Маска имени файла (например, "*.cs" или "*report*").</param>
        /// <returns>FileSearchResult со списком найденных файлов.</returns>
        public Task<FileSearchResult> SearchFilesAsync(string path, string mask)
        {
            _logger.LogDebug("Searching files. Path='{Path}', Mask='{Mask}'", path, mask);

            var result = new FileSearchResult()
            {
                IsSuccess = true,
                BasePath = path,
                Mask = mask,
                TotalFound = 0,
                Result = new List<FSEntrie>()
            };

            int maxResults = _fileOptions.CurrentValue.MaxSearchResults > 0
                ? _fileOptions.CurrentValue.MaxSearchResults
                : Consts.DefaultMaxSearchResults;

            var maskRegex = BuildMaskRegex(mask);

            SearchFilesRecursive(path, maskRegex, maxResults, result.Result, result);

            result.TotalFound = result.Result.Count;

            _logger.LogInformation("Search completed. Path='{Path}', Mask='{Mask}', Found={Found}, LimitReached={LimitReached}",
                path, mask, result.TotalFound, result.LimitReached);

            return Task.FromResult(result);
        }

        /// <summary>
        /// Строит регулярное выражение для маски с поддержкой символов '*' и '?'.
        /// Регистр сравнения зависит от файловой системы ОС (Windows/macOS — без учёта, Linux — с учётом).
        /// </summary>
        private static Regex BuildMaskRegex(string mask)
        {
            string pattern = "^" + Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            var options = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? RegexOptions.IgnoreCase
                : RegexOptions.None;

            return new Regex(pattern, options | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Рекурсивно обходит директории, добавляя в результат файлы, имена которых
        /// соответствуют маске. Останавливается при достижении лимита результатов.
        /// </summary>
        private void SearchFilesRecursive(string directory, Regex maskRegex, int maxResults, List<FSEntrie> results, FileSearchResult state)
        {
            if (state.LimitReached == true)
                return;

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("No permission to enumerate files while searching. Path='{Path}'", directory);
                return;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Input/output error while enumerating files for search. Path='{Path}'", directory);
                return;
            }

            foreach (var file in files)
            {
                if (state.LimitReached == true)
                    return;

                if (!maskRegex.IsMatch(Path.GetFileName(file)))
                    continue;

                var entry = new FSEntrie(ReplaceSeparatorInPath(file), "File");
                try
                {
                    entry.Size = new FileInfo(file).Length;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get file size while searching. Path='{Path}'", file);
                }

                results.Add(entry);

                if (results.Count >= maxResults)
                {
                    state.LimitReached = true;
                    _logger.LogWarning("Search result limit reached ({Limit}). Result truncated.", maxResults);
                    return;
                }
            }

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("No permission to enumerate directories while searching. Path='{Path}'", directory);
                return;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Input/output error while enumerating directories for search. Path='{Path}'", directory);
                return;
            }

            foreach (var dir in dirs)
            {
                if (state.LimitReached == true)
                    return;

                // Символьные ссылки не обходятся: исключает циклы и выход за пределы песочницы.
                if (IsReparsePoint(dir))
                    continue;

                SearchFilesRecursive(dir, maskRegex, maxResults, results, state);
            }
        }

        /// <summary>
        /// Ищет файлы, содержимое которых содержит заданную подстроку (рекурсивно).
        /// Опционально фильтрует файлы по маске имени ('*' и '?').
        /// Файлы размером больше MaxOneFileSizeInBytes пропускаются; symlink-директории не обходятся.
        /// Количество результатов ограничено опцией MaxSearchResults.
        /// </summary>
        /// <param name="path">Путь к корневой директории поиска.</param>
        /// <param name="searchText">Подстрока для поиска в содержимом файлов (с учётом регистра).</param>
        /// <param name="mask">Опциональная маска имени файла; null или пустая строка — без фильтра.</param>
        /// <param name="encoding">Кодировка текста файлов (по умолчанию "utf-8").</param>
        /// <returns>FileSearchResult со списком найденных файлов.</returns>
        public async Task<FileSearchResult> SearchFilesByContentAsync(string path, string searchText, string? mask = null, string encoding = "utf-8")
        {
            _logger.LogDebug("Searching files by content. Path='{Path}', SearchText='{SearchText}', Mask='{Mask}', Encoding='{Encoding}'", path, searchText, mask, encoding);

            var result = new FileSearchResult()
            {
                IsSuccess = true,
                BasePath = path,
                SearchText = searchText,
                Mask = mask,
                TotalFound = 0,
                Result = new List<FSEntrie>()
            };

            int maxResults = _fileOptions.CurrentValue.MaxSearchResults > 0
                ? _fileOptions.CurrentValue.MaxSearchResults
                : Consts.DefaultMaxSearchResults;

            Regex? maskRegex = string.IsNullOrWhiteSpace(mask) ? null : BuildMaskRegex(mask.Trim());

            Encoding? textEncoding = Helpers.TryGetEncoding(encoding);
            if (textEncoding == null)
            {
                _logger.LogError("Failed to get encoding: {Encoding}", encoding);
                textEncoding = Encoding.GetEncoding("utf-8");
            }

            await SearchFilesByContentRecursive(path, searchText, maskRegex, textEncoding, maxResults, result.Result, result);

            result.TotalFound = result.Result.Count;

            _logger.LogInformation("Search by content completed. Path='{Path}', SearchText='{SearchText}', Found={Found}, LimitReached={LimitReached}",
                path, searchText, result.TotalFound, result.LimitReached);

            return result;
        }

        /// <summary>
        /// Рекурсивно обходит директории, проверяя содержимое файлов на наличие подстроки.
        /// Останавливается при достижении лимита результатов.
        /// </summary>
        private async Task SearchFilesByContentRecursive(string directory, string searchText, Regex? maskRegex, Encoding textEncoding, int maxResults, List<FSEntrie> results, FileSearchResult state)
        {
            if (state.LimitReached == true)
                return;

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("No permission to enumerate files while searching by content. Path='{Path}'", directory);
                return;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Input/output error while enumerating files for content search. Path='{Path}'", directory);
                return;
            }

            foreach (var file in files)
            {
                if (state.LimitReached == true)
                    return;

                if (maskRegex != null && !maskRegex.IsMatch(Path.GetFileName(file)))
                    continue;

                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.Length > _fileOptions.CurrentValue.MaxOneFileSizeInBytes)
                    {
                        _logger.LogDebug("File skipped while searching by content: too large. Path='{Path}', Size={Size} bytes", file, fileInfo.Length);
                        continue;
                    }

                    bool contains;
                    using (var reader = new StreamReader(file, textEncoding, detectEncodingFromByteOrderMarks: true))
                    {
                        string content = await reader.ReadToEndAsync();
                        contains = content.Contains(searchText, StringComparison.Ordinal);
                    }

                    if (!contains)
                        continue;

                    var entry = new FSEntrie(ReplaceSeparatorInPath(file), "File");
                    entry.Size = fileInfo.Length;
                    results.Add(entry);

                    if (results.Count >= maxResults)
                    {
                        state.LimitReached = true;
                        _logger.LogWarning("Search result limit reached ({Limit}). Result truncated.", maxResults);
                        return;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogDebug("No permission to read file while searching by content. Path='{Path}'", file);
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Input/output error while reading file for content search. Path='{Path}'", file);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read file while searching by content. Path='{Path}'", file);
                }
            }

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("No permission to enumerate directories while searching by content. Path='{Path}'", directory);
                return;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Input/output error while enumerating directories for content search. Path='{Path}'", directory);
                return;
            }

            foreach (var dir in dirs)
            {
                if (state.LimitReached == true)
                    return;

                // Символьные ссылки не обходятся: исключает циклы и выход за пределы песочницы.
                if (IsReparsePoint(dir))
                    continue;

                await SearchFilesByContentRecursive(dir, searchText, maskRegex, textEncoding, maxResults, results, state);
            }
        }

        /// <summary>
        /// Создает новую директорию.
        /// </summary>
        /// <param name="path">Путь к создаваемой директории.</param>
        /// <returns>JSON-объект с результатом операции (успех или ошибка).</returns>
        public async Task<OperationResult> CreateDirectoryAsync(string path)
        {
            _logger.LogDebug("Creating directory. Path='{Path}'", path);
            try
            {
                if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    _logger.LogInformation("Directory created. Path='{Path}'", path);
                }
                else
                {
                    _logger.LogDebug("Directory already exists or path is empty. Path='{Path}'", path);
                }

                return new OperationResult()
                {
                    IsSuccess = true,
                    Message = "Directory created successfully",
                    Code = 200
                };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Security error: no permission to create directory. Path='{Path}'", path);
                return OperationResult.Fail($"Security error: You do not have permission to create directory {path}.");
            }
            catch (IOException ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Input/output error while creating directory. Path='{Path}', CorrelationId='{CorrelationId}'", path, correlationId);
                return OperationResult.Fail($"An input/output error occurred. Correlation ID: {correlationId}");
            }
            catch (Exception ex)
            {
                string correlationId = Helpers.GetCorrelationId();
                _logger.LogError(ex, "Internal error while creating directory. Path='{Path}', CorrelationId='{CorrelationId}'", path, correlationId);
                return OperationResult.Fail($"An internal error occurred. Correlation ID: {correlationId}");
            }
        }
    }
}
