using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

using Microsoft.Extensions.Logging;

namespace FileAccessMcpServer.Core
{
    /// <summary>
    /// Выполняет валидацию путей в пределах песочницы.
    /// Содержит методы chroot-проверки и обнаружения symlink-атак.
    /// </summary>
    internal class PathValidator
    {
        private readonly ILogger<PathValidator> _logger;
        private readonly ISandboxContext _sandboxContext;

        /// <summary>
        /// Инициализирует экземпляр класса PathValidator.
        /// </summary>
        /// <param name="logger">Логгер для записи событий.</param>
        /// <param name="sandboxContext">Контекст песочницы (корень из конфигурации).</param>
        public PathValidator(ILogger<PathValidator> logger, ISandboxContext sandboxContext)
        {
            _logger = logger;
            _sandboxContext = sandboxContext;
        }

        /// <summary>
        /// Проверяет, что целевой путь находится внутри корня песочницы.
        /// Предотвращает доступ за пределы разрешенного каталога (chroot).
        /// </summary>
        /// <param name="sandboxRoot">Корневой путь песочницы.</param>
        /// <param name="targetFullPath">Полный целевой путь для проверки.</param>
        /// <returns><c>true</c>, если путь находится внутри песочницы; <c>false</c> иначе.</returns>
        public bool ChrootValidation(string sandboxRoot, string targetFullPath)
        {
            if (string.IsNullOrWhiteSpace(sandboxRoot) || string.IsNullOrWhiteSpace(targetFullPath))
            {
                _logger.LogWarning("Chroot validation failed: sandbox root or target path is empty. SandboxRoot='{SandboxRoot}', Target='{Target}'", sandboxRoot, targetFullPath);
                return false;
            }

            // 1. Приводим оба пути к абсолютному и каноничному виду (разрешаем '..' и '.')
            string fullSandbox = Path.GetFullPath(sandboxRoot);
            string fullTarget = Path.GetFullPath(targetFullPath);

            // 2. Гарантируем наличие разделителя в конце пути песочницы
            // Это исключает уязвимость, когда папка "C:\sandbox-secret" ошибочно проходит валидацию для "C:\sandbox"
            if (!fullSandbox.EndsWith(Path.DirectorySeparatorChar))
            {
                fullSandbox += Path.DirectorySeparatorChar;
            }

            // 3. Для полной безопасности также добавляем разделитель к целевому пути, если это директория,
            // либо проверяем строгое совпадение строк (на случай если target — это сама папка песочницы)
            // Сравнение выполняется с учётом чувствительности файловой системы к регистру
            if (fullTarget.Equals(Path.GetFullPath(sandboxRoot), GetPathComparison()))
            {
                return true;
            }

            // 4. Проверяем, начинается ли целевой путь с пути песочницы
            if (!fullTarget.StartsWith(fullSandbox, GetPathComparison()))
            {
                _logger.LogWarning("Chroot validation failed: target path is outside the sandbox. Sandbox='{Sandbox}', Target='{Target}'", fullSandbox, fullTarget);
                return false;
            }

            // 5. Проверка на символьные ссылки (symlinks) в каждом компоненте пути
            // Path.GetFullPath() НЕ разрешает symlink, поэтому строковое сравнение может быть обманчивым.
            // Если внутри sandbox есть symlink, ведущий наружу — это должно блокироваться.
            if (!CheckForSymlinkInPath(fullSandbox, fullTarget))
            {
                _logger.LogWarning("Chroot validation failed: symlink detected leading outside the sandbox. Target='{Target}'", fullTarget);
                return false;
            }

            _logger.LogDebug("Chroot validation passed. Sandbox='{Sandbox}', Target='{Target}'", fullSandbox, fullTarget);
            return true;
        }

        /// <summary>
        /// Проверяет каждый компонент пути на наличие символьных ссылок (reparse point).
        /// Каждая ссылка разрешается в фактическое местоположение (ResolveLinkTarget), а её
        /// цель обязана находиться внутри корня песочницы. Проверяются все существующие
        /// компоненты пути, включая конечный файл.
        /// </summary>
        private bool CheckForSymlinkInPath(string sandboxRoot, string targetPath)
        {
            string trimmedRoot = sandboxRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string resolvedRoot = ResolveFinalPath(trimmedRoot);
            StringComparison comparison = GetPathComparison();

            var relativePath = targetPath.Substring(sandboxRoot.Length);
            var components = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();

            string currentPath = resolvedRoot;

            foreach (var component in components)
            {
                var nextPath = Path.Combine(currentPath, component);

                if (!Path.Exists(nextPath))
                {
                    currentPath = nextPath;
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(nextPath);
                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        _logger.LogDebug("Reparse point detected in path. Path='{Path}'", nextPath);

                        FileSystemInfo? resolvedTarget = Directory.Exists(nextPath)
                            ? Directory.ResolveLinkTarget(nextPath, true)
                            : File.ResolveLinkTarget(nextPath, true);

                        if (resolvedTarget is null)
                        {
                            _logger.LogWarning("Reparse point has no resolvable target. Path='{Path}'", nextPath);
                            return false;
                        }

                        string resolvedFullPath = Path.GetFullPath(resolvedTarget.FullName);
                        if (!IsPathWithinRoot(resolvedRoot, resolvedFullPath, comparison))
                        {
                            _logger.LogWarning("Symlink resolves outside the sandbox. Path='{Path}', ResolvedTarget='{ResolvedTarget}'", nextPath, resolvedFullPath);
                            return false;
                        }

                        _logger.LogDebug("Reparse point resolves inside the sandbox. Path='{Path}', ResolvedTarget='{ResolvedTarget}'", nextPath, resolvedFullPath);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogWarning("No permission to inspect path component. Path='{Path}'", nextPath);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to resolve reparse point. Path='{Path}'", nextPath);
                    return false;
                }

                currentPath = nextPath;
            }

            return true;
        }

        /// <summary>
        /// Возвращает фактический путь к объекту файловой системы, разрешая цепочки символьных ссылок.
        /// Если путь не существует или не является ссылкой, возвращается исходный путь.
        /// </summary>
        private string ResolveFinalPath(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var resolved = Directory.ResolveLinkTarget(path, true);
                    if (resolved is not null)
                        return Path.GetFullPath(resolved.FullName);
                }
                else if (File.Exists(path))
                {
                    var resolved = File.ResolveLinkTarget(path, true);
                    if (resolved is not null)
                        return Path.GetFullPath(resolved.FullName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve final path. Path='{Path}'", path);
            }

            return path;
        }

        /// <summary>
        /// Проверяет, что путь находится внутри корневого каталога (или совпадает с ним).
        /// </summary>
        private static bool IsPathWithinRoot(string root, string candidate, StringComparison comparison)
        {
            string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.Equals(root, comparison) || candidate.StartsWith(rootWithSeparator, comparison);
        }

        /// <summary>
        /// Возвращает стратегию сравнения путей в зависимости от чувствительности файловой
        /// системы к регистру: Windows и macOS (по умолчанию) — регистронезависимо, Linux — регистрозависимо.
        /// </summary>
        private static StringComparison GetPathComparison()
        {
            return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }

        /// <summary>
        /// Проверяет, что корень песочницы задан в конфигурации (SandboxDir).
        /// </summary>
        /// <returns><c>null</c>, если корень задан; OperationResult с ошибкой иначе.</returns>
        private OperationResult? ValidateSandboxConfigured()
        {
            if (string.IsNullOrEmpty(_sandboxContext.SandboxDir))
            {
                _logger.LogWarning("Security error: sandbox directory is not configured. Set 'SandboxDir' in config.json.");
                return OperationResult.Fail("Security Error: Sandbox directory is not configured. Set 'SandboxDir' in config.json.");
            }

            _logger.LogDebug("Sandbox directory from configuration: '{SandboxDir}'", _sandboxContext.SandboxDir);
            return null;
        }

        /// <summary>
        /// Вычисляет абсолютный путь, комбинируя basePath с sandbox при необходимости.
        /// </summary>
        /// <param name="path">Относительный или абсолютный путь.</param>
        /// <param name="sandbox">Корневой каталог песочницы.</param>
        /// <returns>Абсолютный нормализованный путь.</returns>
        private string ComputeAbsolutePath(string path, string sandbox)
        {
            string absolutePath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(sandbox, path));

            _logger.LogDebug("Computed absolute path '{AbsolutePath}' from '{Path}'", absolutePath, path);
            return absolutePath;
        }

        /// <summary>
        /// Валидирует путь и возвращает абсолютный путь к файлу/директории.
        /// </summary>
        /// <param name="path">Путь к файлу или директории. Пустая строка или пробелы трактуются как корень песочницы.</param>
        /// <param name="absolutePath">Выходная переменная для абсолютного пути (по умолчанию null).</param>
        /// <param name="withCheckExist"><c>true</c>, чтобы проверять существование; <c>false</c>, чтобы не проверять.</param>
        /// <returns><c>null</c>, если валидация успешна; OperationResult с ошибкой иначе.</returns>
        public OperationResult? ValidateAndGetAbsolutePath(string path, out string absolutePath, bool withCheckExist = true)
        {
            absolutePath = string.Empty;

            if (ValidateSandboxConfigured() is { } errorSandbox) return errorSandbox;

            string sd = _sandboxContext.SandboxDir!;

            // Пустой/отсутствующий путь трактуется как корень песочницы: это соответствует
            // значениям по умолчанию basePath/path ("") в MCP-инструментах каталогов и
            // позволяет оперировать всей песочницей целиком.
            absolutePath = string.IsNullOrWhiteSpace(path)
                ? Path.GetFullPath(sd)
                : ComputeAbsolutePath(path, sd);

            if (!ChrootValidation(sd, absolutePath))
                return OperationResult.Fail("Security Error: Attempted access outside the sandbox.");

            if (withCheckExist && !Path.Exists(absolutePath))
            {
                _logger.LogWarning("Path does not exist. Path='{Path}'", absolutePath);
                return new OperationResult() { IsSuccess = false, Message = $"File or directory '{path}' not found.", Code = 404 };
                
            }

            _logger.LogDebug("Path validation passed. AbsolutePath='{AbsolutePath}', WithCheckExist={WithCheckExist}", absolutePath, withCheckExist);
            return null;
        }
    }
}
