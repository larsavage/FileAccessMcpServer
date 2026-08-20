using System.ComponentModel;

using FileAccessMcpServer.Core;
using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

/// <summary>
/// Инструменты для операций с директориями в MCP сервере.
/// </summary>
namespace FileAccessMcpServer.Tools
{
    /// <summary>
    /// Предоставляет инструменты для работы с директориями: построение дерева, получение списка файлов, создание директории.
    /// </summary>
    internal class DirTools
    {
        private readonly ILogger<DirTools> _logger;
        private readonly IDirectoryOperations _dirService;
        private readonly PathValidator _pathValidator;

        /// <summary>
        /// Инициализирует экземпляр класса DirTools.
        /// </summary>
        /// <param name="logger">Логгер для записи событий.</param>
        /// <param name="dirService">Сервис операций с директориями.</param>
        /// <param name="pathValidator">Валидатор путей для проверки доступа к песочнице.</param>
        public DirTools(ILogger<DirTools> logger, IDirectoryOperations dirService, PathValidator pathValidator)
        {
            _logger = logger;
            _dirService = dirService;
            _pathValidator = pathValidator;
        }

        /// <summary>
        /// Возвращает дерево каталогов. Выполняется проверка, что каталог находится в песочнице.
        /// </summary>
        [McpServerTool(Name = "directory_tree")]
        [Description("Метод возвращает дерево каталогов. Выполняется проверка, что каталог находится в песочнице.")]
        public async Task<FSEntriesResult> GetDirectoryAsync(
            [Description("Базовый путь с которого начинается построение дерева (например, 'd:\\sandbox\\projects-xxx' или '/sandbox/project-xxx')")] string basePath,
            [Description("Уровень вложенности. По умолчанию 5. Максимальный уровень вложенности 10")] int nesting = 5)
        {
            _logger.LogDebug("MCP tool 'directory_tree' invoked. BasePath='{BasePath}', Nesting={Nesting}", basePath, nesting);

            string absolutePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(basePath, out absolutePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'directory_tree' rejected. Reason='{Reason}'", result.Message);
                return new FSEntriesResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            if (nesting <= 0 || nesting > 10)
            {
                _logger.LogWarning("Nesting value out of range ({Nesting}). Clamping to 10.", nesting);
                nesting = 10;
            }

            var entries = await _dirService.GetDirectoryTreeAsync(absolutePath, nesting);
            _logger.LogInformation("MCP tool 'directory_tree' completed. BasePath='{BasePath}', Entries={Entries}", basePath, entries.Count);
            return new FSEntriesResult()
            {
                IsSuccess = true,
                Path = basePath,
                Result = entries,
                Code = 200
            };
        }

        /// <summary>
        /// Возвращает дерево каталогов с файлами. Выполняется проверка, что каталог находится в песочнице.
        /// </summary>
        [McpServerTool(Name = "directory_tree_with_files")]
        [Description("Метод возвращает дерево каталогов с файлами. Выполняется проверка, что каталог находится в песочнице.")]
        public async Task<FSEntriesResult> GetDirectoryTreeWithFilesAsync(
            [Description("Базовый путь с которого начинается построение дерева (например, 'd:\\sandbox\\src\\projects' или '/sandbox/project-xxx')")] string basePath,
            [Description("Уровень вложенности. По умолчанию 5. Максимальный уровень вложенности 10")] int nesting = 5)
        {
            _logger.LogDebug("MCP tool 'directory_tree_with_files' invoked. BasePath='{BasePath}', Nesting={Nesting}", basePath, nesting);

            string absolutePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(basePath, out absolutePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'directory_tree_with_files' rejected. Reason='{Reason}'", result.Message);
                return new FSEntriesResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            if (nesting <= 0 || nesting > 10)
            {
                _logger.LogWarning("Nesting value out of range ({Nesting}). Clamping to 10.", nesting);
                nesting = 10;
            }

            var entries = await _dirService.GetDirectoryTreeWithFilesAsync(absolutePath, nesting);
            _logger.LogInformation("MCP tool 'directory_tree_with_files' completed. BasePath='{BasePath}', Entries={Entries}", basePath, entries.Count);
            return new FSEntriesResult()
            {
                IsSuccess = true,
                Path = basePath,
                Result = entries,
                Code = 200
            };
        }

        /// <summary>
        /// Возвращает список файлов в каталоге. Выполняется проверка, что каталог находится в песочнице.
        /// </summary>
        [McpServerTool(Name = "directory_list_files")]
        [Description("Метод возвращает список файлов в каталоге. Выполняется проверка, что каталог находится в песочнице.")]
        public async Task<FSEntriesResult> GetDirectoryListFilesAsync(
            [Description("Путь к каталогу, для которого необходимо вывести список файлов")] string basePath = "")
        {
            _logger.LogDebug("MCP tool 'directory_list_files' invoked. BasePath='{BasePath}'", basePath);

            string absolutePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(basePath, out absolutePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'directory_list_files' rejected. Reason='{Reason}'", result.Message);
                return new FSEntriesResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var entries = await _dirService.GetFilesInDirectoryAsync(absolutePath);
            _logger.LogInformation("MCP tool 'directory_list_files' completed. BasePath='{BasePath}', Files={Files}", basePath, entries.Count);
            return new FSEntriesResult()
            {
                IsSuccess = true,
                Path = basePath,
                Result = entries,
                Code = 200
            };
        }

        /// <summary>
        /// Создает каталог. Выполняется проверка, что каталог находится в песочнице.
        /// </summary>
        [McpServerTool(Name = "create_directory")]
        [Description("создает каталог. Выполняется проверка, что каталог находится в песочнице.")]
        public async Task<OperationResult> CreateDirectoryAsync(
            [Description("Путь к каталогу, который нужно создать. Метод автоматически создаст все отсутствующие папки в цепочке пути.")] string path = "")
        {
            _logger.LogDebug("MCP tool 'create_directory' invoked. Path='{Path}'", path);

            string absolutePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(path, out absolutePath, false);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'create_directory' rejected. Reason='{Reason}'", result.Message);
                return result;
            }

            var operationResult = await _dirService.CreateDirectoryAsync(absolutePath);
            _logger.LogInformation("MCP tool 'create_directory' completed. Path='{Path}', IsSuccess={IsSuccess}", absolutePath, operationResult.IsSuccess);
            return operationResult;
        }

        /// <summary>
        /// Ищет файлы по маске имени внутри песочницы. Поиск выполняется рекурсивно.
        /// </summary>
        [McpServerTool(Name = "search_files")]
        [Description("Ищет файлы по маске имени (например, '*.cs' или '*report*') внутри песочницы. Поддерживаются символы '*' (любое количество символов) и '?' (один символ). Результат ограничен (по умолчанию 50 файлов, настраивается в config.json). Маски, совпадающие со всеми файлами ('*.*', '*', '**'), заблокированы.")]
        public async Task<FileSearchResult> SearchFilesAsync(
            [Description("Путь к каталогу, с которого начинается поиск (например, 'd:\\sandbox\\projects' или '/sandbox/projects')")] string basePath,
            [Description("Маска имени файла. Примеры: '*.cs', '*report*', 'config.?'")] string mask)
        {
            _logger.LogDebug("MCP tool 'search_files' invoked. BasePath='{BasePath}', Mask='{Mask}'", basePath, mask);

            if (string.IsNullOrWhiteSpace(mask))
            {
                _logger.LogWarning("MCP tool 'search_files' rejected: mask is empty.");
                return new FileSearchResult() { IsSuccess = false, Message = "Mask cannot be empty" };
            }

            string trimmedMask = mask.Trim();
            if (IsBlockedMask(trimmedMask))
            {
                _logger.LogWarning("MCP tool 'search_files' rejected: mask '{Mask}' is blocked.", trimmedMask);
                return new FileSearchResult()
                {
                    IsSuccess = false,
                    Message = $"Mask '{trimmedMask}' is blocked: it matches all files and would produce too large output. Specify a more specific mask."
                };
            }

            string absolutePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(basePath, out absolutePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'search_files' rejected. Reason='{Reason}'", result.Message);
                return new FileSearchResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var searchResult = await _dirService.SearchFilesAsync(absolutePath, trimmedMask);
            _logger.LogInformation("MCP tool 'search_files' completed. BasePath='{BasePath}', Mask='{Mask}', Found={Found}, LimitReached={LimitReached}",
                absolutePath, trimmedMask, searchResult.TotalFound ?? 0, searchResult.LimitReached);
            return searchResult;
        }

        /// <summary>
        /// Определяет, заблокирована ли маска как совпадающая со всеми файлами.
        /// </summary>
        private static bool IsBlockedMask(string mask)
        {
            return mask == "*.*" || mask == "*" || mask == "**";
        }

        /// <summary>
        /// Ищет файлы, содержимое которых содержит заданную подстроку, внутри песочницы.
        /// Поиск выполняется рекурсивно; опционально фильтрует файлы по маске имени.
        /// </summary>
        [McpServerTool(Name = "search_files_content")]
        [Description("Ищет текстовые файлы, содержимое которых содержит заданную подстроку (с учётом регистра). Опциональная маска имени файла ('*' — любое количество символов, '?' — один символ). Результат ограничен (по умолчанию 50 файлов, настраивается в config.json). Файлы больше MaxOneFileSizeInBytes пропускаются. Маски, совпадающие со всеми файлами ('*.*', '*', '**'), заблокированы.")]
        public async Task<FileSearchResult> SearchFilesByContentAsync(
            [Description("Путь к каталогу, с которого начинается поиск (например, 'd:\\sandbox\\projects' или '/sandbox/projects')")] string basePath,
            [Description("Подстрока, которую нужно найти в содержимом файлов (например, 'TODO' или 'HttpClient')")] string searchText,
            [Description("Опциональная маска имени файла. Примеры: '*.cs', '*log*'. Пусто — без фильтра по имени")] string mask = "",
            [Description("Кодировка текста. По умолчанию utf-8.")] string encoding = "utf-8")
        {
            _logger.LogDebug("MCP tool 'search_files_content' invoked. BasePath='{BasePath}', SearchText='{SearchText}', Mask='{Mask}', Encoding='{Encoding}'", basePath, searchText, mask, encoding);

            if (string.IsNullOrWhiteSpace(searchText))
            {
                _logger.LogWarning("MCP tool 'search_files_content' rejected: search text is empty.");
                return new FileSearchResult() { IsSuccess = false, Message = "Search text cannot be empty" };
            }

            string? trimmedMask = null;
            if (!string.IsNullOrWhiteSpace(mask))
            {
                trimmedMask = mask.Trim();
                if (IsBlockedMask(trimmedMask))
                {
                    _logger.LogWarning("MCP tool 'search_files_content' rejected: mask '{Mask}' is blocked.", trimmedMask);
                    return new FileSearchResult()
                    {
                        IsSuccess = false,
                        Message = $"Mask '{trimmedMask}' is blocked: it matches all files and would produce too large output. Specify a more specific mask."
                    };
                }
            }

            string absolutePath;
            var result = _pathValidator.ValidateAndGetAbsolutePath(basePath, out absolutePath);
            if (result != null)
            {
                _logger.LogWarning("MCP tool 'search_files_content' rejected. Reason='{Reason}'", result.Message);
                return new FileSearchResult() { IsSuccess = result.IsSuccess, Message = result.Message };
            }

            var searchResult = await _dirService.SearchFilesByContentAsync(absolutePath, searchText, trimmedMask, encoding);
            _logger.LogInformation("MCP tool 'search_files_content' completed. BasePath='{BasePath}', SearchText='{SearchText}', Found={Found}, LimitReached={LimitReached}",
                absolutePath, searchText, searchResult.TotalFound ?? 0, searchResult.LimitReached);
            return searchResult;
        }
    }
}
