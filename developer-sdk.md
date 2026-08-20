# FileAccessMcpServer - SDK для разработчиков

## Введение

Данный документ описывает архитектуру проекта **FileAccessMcpServer** и содержит руководство по расширению его функционала. Проект представляет собой сервер MCP (Model Context Protocol) для безопасной работы с файлами в песочнице.

---

## Архитектура проекта

```
D:/AI-Sandbox/FileAccessMcpServer/
├── Core/                  # Базовый слой
│   ├── Consts.cs         # Константы проекта
│   ├── ApiKeyMiddleware.cs# Middleware проверки API-ключа (X-Api-Key)
│   ├── Helpers.cs        # Вспомогательные методы (MIME-типы)
│   ├── PathValidator.cs  # Валидация путей (chroot + symlink защита)
│   ├── SandboxContext.cs # Корень песочницы из конфигурации (SandboxDir/BackupDir)
│   ├── ConfigValidator.cs# Валидация конфигурации при старте и перезагрузке
│   └── ConfigManager.cs  # Расположение конфига и генерация конфига по умолчанию
├── Models/                # Модели данных
│   ├── AppOptions.cs     # Конфигурационные опции
│   ├── OperationResult.cs# Базовый класс результата
│   ├── FSEntrie.cs       # Запись о файле/директории
│   ├── FSEntriesResult.cs
│   ├── FileReadResult.cs
│   ├── FileReadChunkResult.cs
│   ├── FileWriteResult.cs
│   ├── FileInfoResult.cs
│   ├── FileImageResult.cs
│   ├── FileBinaryReadResult.cs
│   ├── FileHashResult.cs # Результат вычисления хеша файла
│   └── MultiFileReadResult.cs
├── Interfaces/            # Контракты
│   ├── ISandboxContext.cs
│   ├── IDirectoryOperations.cs
│   ├── IFileOperations.cs
│   └── IBackupOperations.cs
├── Services/              # Реализация бизнес-логики
│   ├── DirService.cs     # Реализация IDirectoryOperations
│   ├── FileService.cs    # Реализация IFileOperations
│   └── BackupService.cs  # Реализация IBackupOperations
├── Tools/                 # MCP инструменты
│   ├── DirTools.cs       # Инструменты работы с директориями
│   ├── FileTools.cs      # Инструменты работы с файлами
│   └── HealthTools.cs    # Проверка здоровья сервера
├── config.json            # Конфигурация
└── Program.cs             # Точка входа, DI, настройка MCP
```

---

## Паттерны проектирования

### 1. Layered Architecture (Слоистая архитектура)

Проект следует принципу разделения на слои:

- **Core**: Базовый слой с общими функциями, константами
- **Models**: Сущности и данные проекта
- **Interfaces**: Контракты для реализации
- **Services**: Конкретные реализации бизнес-логики
- **Tools**: MCP инструменты для внешнего взаимодействия

### 2. Dependency Injection (Инверсия зависимостей)

Все сервисы используют DI контейнер через конструктор:

```csharp
// Пример конструктора сервиса (IOptionsMonitor — для поддержки hot reload)
public FileService(
    ILogger<FileService> logger,
    IOptionsMonitor<AppOptions> fileOptions,
    IBackupOperations backupOperations)
{
    _logger = logger;
    _fileOptions = fileOptions;
    _backupOperations = backupOperations;
}
```

### 3. Interface Segregation (Разделение интерфейсов)

Каждый интерфейс отвечает за одну область ответственности:

- `ISandboxContext` — получение корня песочницы и каталога бэкапов из конфигурации (SandboxDir/BackupDir)
- `IDirectoryOperations` — операции с директориями
- `IFileOperations` — операции с файлами
- `IBackupOperations` — резервное копирование

---

## API Documentation

### Core Layer

#### Consts.Megabyte

```csharp
/// <summary>
/// Размер мегабайта в байтах (1024 * 1024).
/// Используется при ограничении размера файлов и директорий.
/// </summary>
public const int Megabyte = 1024 * 1024; // = 1048576
```

Прочие константы `Consts`:

| Константа | Значение | Назначение |
|-----------|----------|------------|
| `MaxChunkChars` | `1_000_000` | Макс. размер блока символов для поблочного чтения (`file_read_chunks`). |
| `DefaultMaxSearchResults` | `50` | Лимит результатов поиска по умолчанию. |
| `DefaultRateLimitWindowInSeconds` | `60` | Окно rate limiting по умолчанию (сек). |
| `DefaultRateLimitPermitLimit` | `100` | Лимит запросов на IP за окно по умолчанию. |
| `DefaultRateLimitQueueLimit` | `0` | Очередь rate limiting по умолчанию (выключена). |
| `MaxPendingDeleteConfirmations` | `10_000` | Макс. число ожидающих подтверждений `file_delete` в `MemoryCache`. |
| `DefaultShutdownTimeoutSeconds` | `30` | Таймаут graceful shutdown (`HostOptions.ShutdownTimeout`) — сколько хост ждёт завершения in-flight запросов перед принудительным завершением. |

#### Helpers.GetContentTypeFromExtension()
```csharp
/// <summary>
/// Возвращает MIME-тип для указанного расширения файла.
/// </summary>
/// <param name="extension">Расширение файла (например, ".txt", ".json").</param>
/// <returns>MIME-тип содержимого файла.</returns>
public static string GetContentTypeFromExtension(string extension)
```

#### SandboxContext
Корень песочницы и каталог резервных копий определяются серверной конфигурацией (`config.json`, секция `FileAccess`)
и не могут быть изменены клиентом через HTTP-запрос:

```csharp
/// <summary>
/// Возвращает корневой каталог песочницы из конфигурации (SandboxDir).
/// </summary>
public string? SandboxDir { get; }

/// <summary>
/// Возвращает каталог для резервных копий из конфигурации (BackupDir).
/// </summary>
public string? BackupDir { get; }
```

#### PathValidator.ChrootValidation()
```csharp
/// <summary>
/// Проверяет, что целевой путь находится внутри корня песочницы.
/// Предотвращает доступ за пределы разрешенного каталога (chroot).
/// </summary>
/// <param name="sandboxRoot">Корневой путь песочницы.</param>
/// <param name="targetFullPath">Полный целевой путь для проверки.</param>
/// <returns><c>true</c>, если путь находится внутри песочницы; <c>false</c> иначе.</returns>
public bool ChrootValidation(string sandboxRoot, string targetFullPath)
```

Также содержит проверку на символьные ссылки (`CheckForSymlinkInPath`) — каждый компонент пути проверяется на `ReparsePoint`.

#### PathValidator.ValidateAndGetAbsolutePath()
```csharp
/// <summary>
/// Валидирует путь и возвращает абсолютный путь к файлу/директории.
/// </summary>
/// <param name="path">Путь к файлу или директории.</param>
/// <param name="absolutePath">Выходная переменная для абсолютного пути.</param>
/// <param name="withCheckExist"><c>true</c>, чтобы проверять существование; <c>false</c>, чтобы не проверять.</param>
/// <returns><c>null</c>, если валидация успешна; OperationResult с ошибкой иначе.</returns>
public OperationResult? ValidateAndGetAbsolutePath(string path, out string absolutePath, bool withCheckExist = true)
```

---

### Services Layer

#### FileService.FileGetInfoAsync()
```csharp
/// <summary>
/// Получает информацию о файле.
/// </summary>
/// <param name="filePath">Полный путь к файлу.</param>
/// <returns>FileInfoResult с информацией (имя, расширение, тип, размер, даты создания/изменения и т.д.).</returns>
public async Task<FileInfoResult> FileGetInfoAsync(string filePath)
```

#### FileService.FileReadAsync()
```csharp
/// <summary>
/// Считывает содержимое файла.
/// </summary>
/// <param name="filePath">Полный путь к файлу.</param>
/// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
/// <returns>FileReadResult с содержимым файла и информацией о размере.</returns>
public async Task<FileReadResult> FileReadAsync(string filePath, string encoding = "utf-8")
```

#### FileService.FileWriteAsync()
```csharp
/// <summary>
/// Записывает содержимое файла. Автоматически создаёт недостающие директории и резервную копию перед перезаписью.
/// </summary>
/// <param name="filePath">Полный путь к файлу.</param>
/// <param name="body">Содержимое, которое нужно записать в файл.</param>
/// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
/// <returns>FileWriteResult с подтверждением успешной записи.</returns>
public async Task<FileWriteResult> FileWriteAsync(string filePath, string body, string encoding = "utf-8")
```

#### FileService.FileImageReadAsync()
```csharp
/// <summary>
/// Считывает изображение и возвращает его в формате Base64 data URL.
/// </summary>
/// <param name="filePath">Полный путь к файлу изображения.</param>
/// <returns>FileImageResult с Base64 data URL.</returns>
public async Task<FileImageResult> FileImageReadAsync(string filePath)
```

#### FileService.FileReadChunksAsync()
```csharp
/// <summary>
/// Считывает содержимое файла частями (блоками символов).
/// Работает с файлами любой длины строки.
/// </summary>
/// <param name="filePath">Полный путь к файлу.</param>
/// <param name="startChar">Символ, с которого начать чтение (индексация с 0, по умолчанию 0).</param>
/// <param name="charsCount">Количество символов для чтения в одном блоке (по умолчанию 1000000).</param>
/// <param name="encoding">Кодировка текста (по умолчанию "utf-8").</param>
/// <returns>FileReadChunkResult с содержимым и информацией о пагинации.</returns>
public async Task<FileReadChunkResult> FileReadChunksAsync(string filePath, long startChar = 0, int charsCount = 1000000, string encoding = "utf-8")
```

#### FileService.FileCopyAsync()
```csharp
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
```

#### FileService.FileMoveAsync()
```csharp
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
```

#### FileService.FileDeleteAsync()
```csharp
/// <summary>
/// Удаляет файл внутри песочницы. Перед удалением создаётся резервная копия.
/// </summary>
/// <param name="filePath">Полный путь к удаляемому файлу.</param>
/// <returns>OperationResult с результатом операции.</returns>
public async Task<OperationResult> FileDeleteAsync(string filePath)
```

#### DirService
```csharp
public async Task<List<FSEntrie>> GetFilesInDirectoryAsync(string path)
public async Task<List<FSEntrie>> GetDirectoryTreeAsync(string path, int nesting)
public async Task<List<FSEntrie>> GetDirectoryTreeWithFilesAsync(string path, int nesting)
public async Task<OperationResult> CreateDirectoryAsync(string path)
```

> Примечания по обходу дерева:
> - Символьные ссылки (symlink/junction) отображаются в дереве, но **не обходятся** — это исключает бесконечную рекурсию при циклических ссылках внутри песочницы.
> - Недоступные для чтения директории (UnauthorizedAccessException) пропускаются с логированием, а не роняют весь инструмент.
> - Количество записей результата ограничено опцией `MaxTreeEntries` (по умолчанию 2000) — при превышении ответ усекается с предупреждением в логе.
> - Параметры `file_read_chunks`: `startChar` должен быть >= 0, `charsCount` — в диапазоне [1..1000000], иначе возвращается ошибка.

#### BackupService.CreateBackupFileAsync()
```csharp
/// <summary>
/// Создает бэкап файла перед его перезаписью/удалением.
/// Если указан BackupDir (конфигурация), сохраняет в указанную директорию, иначе рядом с оригиналом.
/// Имя копии — {имя}.{yyyyMMdd_HHmmss}.{Guid}.bak.
/// </summary>
/// <param name="originalFilePath">Путь к файлу, который нужно скопировать.</param>
/// <returns>BackupFileResult с путём к копии (BackupPath = null, если копия не создавалась).</returns>
public async Task<BackupFileResult> CreateBackupFileAsync(string originalFilePath)
```

Также доступны `RestoreBackupFileAsync(backupFilePath, originalFilePath)` — восстановление файла из бэкапа
(используется при откате прерванной операции, см. «Откат при прерванной операции» ниже) и
`DeleteBackupFileAsync(backupFilePath)` — удаление бэкапа.

---

### Tools Layer (MCP Server Tools)

Все MCP инструменты возвращают типизированные модели из `Models/`, а не строки JSON.

#### DirTools

```csharp
[McpServerTool(Name = "directory_tree")]
public async Task<FSEntriesResult> GetDirectoryAsync(string basePath, int nesting = 5)

[McpServerTool(Name = "directory_tree_with_files")]
public async Task<FSEntriesResult> GetDirectoryTreeWithFilesAsync(string basePath, int nesting = 5)

[McpServerTool(Name = "directory_list_files")]
public async Task<FSEntriesResult> GetDirectoryListFilesAsync(string basePath = "")

[McpServerTool(Name = "create_directory")]
public async Task<OperationResult> CreateDirectoryAsync(string path = "")

[McpServerTool(Name = "search_files")]
public async Task<FileSearchResult> SearchFilesAsync(string basePath, string mask)

[McpServerTool(Name = "search_files_content")]
public async Task<FileSearchResult> SearchFilesByContentAsync(string basePath, string searchText, string mask = "", string encoding = "utf-8")
```

> **Поиск по маске (`search_files`):**
> - Маска применяется к имени файла (не к полному пути), поддерживаются `*` (любое количество символов) и `?` (один символ).
> - Поиск рекурсивный; symlink/junction-директории не обходятся (защита от циклов и выхода за пределы песочницы).
> - Результат ограничен опцией `MaxSearchResults` (по умолчанию 50, `Consts.DefaultMaxSearchResults`); при превышении результат усекается и `LimitReached = true`.
> - Маски, совпадающие со всеми файлами (`*.*`, `*`, `**`), заблокированы на уровне инструмента.

> **Поиск по содержимому (`search_files_content`):**
> - Ищет файлы, содержимое которых содержит подстроку `searchText` (сравнение с учётом регистра, `StringComparison.Ordinal`); подстрока может пересекать переносы строк.
> - `mask` — опциональная маска имени файла; пустая строка — без фильтра. Заблокированы те же маски, что и в `search_files`.
> - Файлы размером больше `MaxOneFileSizeInBytes` пропускаются; symlink-директории не обходятся.
> - Тот же лимит `MaxSearchResults` и усечение с `LimitReached = true`.
> - Поддерживается параметр кодировки (`encoding`, по умолчанию utf-8); BOM определяется автоматически.

#### FileTools

```csharp
[McpServerTool(Name = "file_read")]
public async Task<FileReadResult> ReadFileAsync(string filePath, string encoding = "utf-8")

[McpServerTool(Name = "file_write")]
public async Task<FileWriteResult> WriteFileAsync(string filePath, string content, string encoding = "utf-8")

[McpServerTool(Name = "file_append")]
public async Task<FileWriteResult> AppendToFileAsync(string filePath, string content, string encoding = "utf-8")

// file_replace_lines — ОТКЛЮЧЁН: метод закомментирован в FileTools.cs и НЕ регистрируется.
// Сервисный метод FileReplaceLinesAsync остаётся в IFileOperations/FileService (вызовы напрямую, без MCP).

[McpServerTool(Name = "files_read_multiple")]
public async Task<MultiFileReadResult> ReadMultipleFilesAsync(string[] filesPath, string encoding = "utf-8")

[McpServerTool(Name = "file_read_chunks")]
public async Task<FileReadChunkResult> ReadFileChunksAsync(string filePath, long startChar = 0, int charsCount = 500000, string encoding = "utf-8")

[McpServerTool(Name = "file_get_info")]
public async Task<FileInfoResult> GetFileInfoAsync(string filePath)

[McpServerTool(Name = "file_image_read")]
public async Task<FileImageResult> ReadImageAsync(string fileImagePath)

[McpServerTool(Name = "file_binary_read")]
public async Task<FileBinaryReadResult> ReadBinaryAsync(string filePath)

[McpServerTool(Name = "file_hash")]
public async Task<FileHashResult> FileHashAsync(string filePath, string algorithm = "sha256")

[McpServerTool(Name = "file_copy")]
public async Task<OperationResult> FileCopyAsync(string originFilePath, string newFilePath, bool isRewrite = false)

[McpServerTool(Name = "file_move")]
public async Task<OperationResult> FileMoveAsync(string originFilePath, string newFilePath, bool isRewrite = false)

[McpServerTool(Name = "file_rename")]
public async Task<OperationResult> FileRenameAsync(string originFilePath, string newFilePath, bool isRewrite = false)

[McpServerTool(Name = "file_delete")]
public async Task<OperationResult> FileDeleteAsync(string filePath, string confirmToken = "")
```

> **Примечание:** MCP-имена инструментов (`file_read`, `file_write` и т.д.) и имена C#-методов могут отличаться. Инструменты `file_copy`/`file_move`/`file_rename` проверяют **оба** пути (исходный и назначения) на нахождение в песочнице. `file_rename` — алиас `file_move` (общая реализация).

#### HealthTools

```csharp
[McpServerTool(Name = "health")]
public string HealthCheck()
```

#### Двухэтапное подтверждение удаления (`file_delete`)

Деструктивная операция `file_delete` защищена подтверждением через одноразовый токен:

1. **Первый вызов** без токена (`confirmToken = ""`): генерируется токен из **32 hex-символов** (16 случайных байт через `RandomNumberGenerator`) — криптостойкий, в отличие от `Guid`. Токен сохраняется в статический `MemoryCache` (срок действия — 5 минут), а результат возвращается с `Code = 202` и текстом, обязывающим ИИ спросить разрешение у пользователя.
2. **Второй вызов** с полученным токеном: при совпадении токена запись атомарно потребляется и удаляется из кэша, затем выполняется `FileService.FileDeleteAsync`.

Ключевые особенности реализации кэша подтверждений (`FileTools._pendingConfirmations`):

- **Статический** — токены должны переживать отдельные MCP-запросы (инструмент создаётся на каждый вызов).
- **Ограничен по размеру** — `MemoryCacheOptions.SizeLimit = Consts.MaxPendingDeleteConfirmations` (10 000), каждая запись помечена `Size = 1`. Защищает память сервера от переполнения при флуде `file_delete` уникальными путями за время жизни записи (5 минут).
- **Нормализация ключа** — `NormalizeCacheKey()`: `Path.GetFullPath` + `TrimEndingDirectorySeparator`; на Windows путь приводится к `ToUpperInvariant()`, так как файловая система не чувствительна к регистру (иначе `C:\File.txt` и `c:\file.txt` не нашли бы общий токен).
- **Атомарная операция «проверить → потратить → удалить запись»** — последовательность `TryGetValue → TryConsume → Remove` целиком выполняется в критической секции `_pendingConfirmationsLock` (статический `lock`). `ConfirmationRecord.TryConsume()` дополнительно атомарен через `Interlocked.Exchange`. Победитель в `lock` вместе с потреблением сразу удаляет запись из кэша; параллельный запрос с тем же токеном после захвата блокировки запись уже не находит и получает ответ `[ОШИБКА]` (истёкшее/отсутствующее подтверждение), а не сообщение об «уже использованном» токене. Удаление файла выполняется вне блокировки (после `lock`).

Неверный или уже использованный токен блокирует операцию (`[ОТКЛОНЕНО]`), истёкший, отсутствующий или уже потраченный (запись удалена победителем) — требует повторной инициации (`[ОШИБКА]`). Перед удалением автоматически создаётся резервная копия файла.

---

## Модели данных (Models/)

Все модели наследуются от `OperationResult`:

```csharp
// Базовый класс
internal class OperationResult
{
    public bool IsSuccess { get; set; }
    public string? Message { get; set; }
    public int? Code { get; set; }

    public static OperationResult Success() => new OperationResult { IsSuccess = true, Code = 200 };
    public static OperationResult Fail(string message) => new OperationResult { IsSuccess = false, Message = message, Code = 400 };
}
```

> `Code = 202` используется для запроса подтверждения деструктивных операций (например, `file_delete`).

```csharp
// FSEntrie — запись о директории или файле
public class FSEntrie
{
    public string Type { get; set; }  // "Dir" или "File"
    public string Name { get; set; }  // Путь
    public long? Size { get; set; }   // Размер в байтах (для файлов)
}
```

```csharp
// FileReadResult
public class FileReadResult : OperationResult
{
    public string? Path { get; set; }
    public long? SizeInBytes { get; set; }
    public string? Content { get; set; }
}

// FileWriteResult
public class FileWriteResult : OperationResult
{
    public string? Path { get; set; }
    public long? BytesWritten { get; set; }
}

// MultiFileReadResult — результат batch-чтения нескольких файлов
public class MultiFileReadResult : OperationResult
{
    public List<FileReadResult>? Files { get; set; }
}

// FileInfoResult
public class FileInfoResult : OperationResult
{
    public string? Name { get; set; }
    public string? Extension { get; set; }
    public string? FileType { get; set; }
    public long? Length { get; set; }
    public DateTime? FileCreate { get; set; }
    public DateTime? FileLastUpdate { get; set; }
    public DateTime? FileLastAccessTime { get; set; }
    public bool? IsReadOnly { get; set; }
    public bool? IsLink { get; set; }
    public string? LinkTarget { get; set; }
}

// FileImageResult
public class FileImageResult : OperationResult
{
    public string? FilePath { get; set; }
    public string? MimeType { get; set; }
    public long? SizeInBytes { get; set; }
    public string? DataUrl { get; set; }
}

// FileHashResult
public class FileHashResult : OperationResult
{
    public string? Path { get; set; }
    public string? Algorithm { get; set; }   // md5 / crc32 / sha256 / sha512
    public string? Hash { get; set; }        // hex, нижний регистр
    public long? SizeInBytes { get; set; }
}

// FileReadChunkResult
public class FileReadChunkResult : OperationResult
{
    public string? Path { get; set; }
    public long? StartChar { get; set; }
    public int? CharsReturned { get; set; }
    public long? TotalCharsReadUpTo { get; set; }
    public bool? HasMore { get; set; }
    public long? NextStartChar { get; set; }
    public string? Content { get; set; }
}
```

---

## Расширение функционала

### 1. Добавить новый сервис

#### Шаги:
1. Создать новый интерфейс в `Interfaces/`
2. Реализовать его в `Services/`
3. Добавить MCP инструмент в `Tools/`
4. Зарегистрировать в `Program.cs` в DI контейнере

#### Пример добавления сервиса для работы с архивами:

```csharp
// 1. Новый интерфейс в Interfaces/IArchiveOperations.cs
namespace FileAccessMcpServer.Interfaces
{
    internal interface IArchiveOperations
    {
        Task<OperationResult> ExtractZipAsync(string zipPath, string destDirectory);
        Task<OperationResult> CreateZipAsync(List<string> filePaths, string zipPath);
    }
}

// 2. Реализация в Services/ArchiveService.cs
namespace FileAccessMcpServer.Services
{
    internal class ArchiveService : IArchiveOperations
    {
        private readonly ILogger<ArchiveService> _logger;

        public ArchiveService(ILogger<ArchiveService> logger) => _logger = logger;

        public async Task<OperationResult> ExtractZipAsync(string zipPath, string destDirectory)
        {
            // Реализация логики извлечения архива
        }

        public async Task<OperationResult> CreateZipAsync(List<string> filePaths, string zipPath)
        {
            // Реализация логики создания архива
        }
    }
}

// 3. MCP инструмент в Tools/ArchiveTools.cs
namespace FileAccessMcpServer.Tools
{
    internal class ArchiveTools
    {
        private readonly IArchiveOperations _archiveService;
        private readonly PathValidator _pathValidator;

        public ArchiveTools(IArchiveOperations archiveService, PathValidator pathValidator)
        {
            _archiveService = archiveService;
            _pathValidator = pathValidator;
        }

        [McpServerTool(Name = "extract_zip")]
        [Description("Извлекает ZIP-архив в песочнице.")]
        public async Task<OperationResult> ExtractZipAsync(
            [Description("Путь к ZIP-архиву")] string zipPath,
            [Description("Целевая директория")] string destDirectory)
        {
            string absoluteZip;
            var result = _pathValidator.ValidateAndGetAbsolutePath(zipPath, out absoluteZip);
            if (result != null) return result;

            string absoluteDest;
            result = _pathValidator.ValidateAndGetAbsolutePath(destDirectory, out absoluteDest, false);
            if (result != null) return result;

            return await _archiveService.ExtractZipAsync(absoluteZip, absoluteDest);
        }
    }
}

// 4. Регистрация в Program.cs
builder.Services.AddScoped<IArchiveOperations, ArchiveService>();
builder.Services.AddMcpServer().WithTools<ArchiveTools>();
```

### 2. Добавить новую операцию в существующий интерфейс

#### Пример расширения IFileOperations:

```csharp
// В Interfaces/IFileOperations.cs добавить:
Task<FileReadResult> ReadFileRawAsync(string filePath, string encoding = "utf-8");

// В Services/FileService.cs реализовать:
public async Task<FileReadResult> ReadFileRawAsync(string filePath, string encoding = "utf-8")
{
    var fileInfo = new FileInfo(filePath);

    if (fileInfo.Length > _fileOptions.MaxOneFileSizeInBytes)
        return new FileReadResult()
        {
            IsSuccess = false,
            Message = $"File too large ({fileInfo.Length / Consts.Megabyte} MiB)."
        };

    Encoding textEncoding = GetEncoding(encoding);
    string fileContent = await File.ReadAllTextAsync(filePath, textEncoding);

    return new FileReadResult()
    {
        IsSuccess = true,
        Path = filePath,
        Content = fileContent
    };
}

// В Tools/FileTools.cs добавить инструмент:
[McpServerTool(Name = "read_file_raw")]
[Description("Считывает файл как строку.")]
public async Task<FileReadResult> ReadFileRawAsync(
    [Description("Абсолютный путь к файлу")] string filePath,
    [Description("Кодировка текста")] string encoding = "utf-8")
{
    string absolutePath;
    var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absolutePath);
    if (result != null) return new FileReadResult() { IsSuccess = result.IsSuccess, Message = result.Message };

    return await _fileService.ReadFileRawAsync(absolutePath, encoding);
}
```

---

## Безопасность и ограничения

### 1. Ограничения песочницы

Все операции выполняются в пределах корня песочницы. Корень задаётся на сервере в `config.json`
(опция `SandboxDir`) и не может быть изменён клиентом через HTTP-запрос:

```csharp
if (!ChrootValidation(sandboxRoot, absoluteFilePath))
{
    return OperationResult.Fail("Security Error: Attempted access outside the sandbox.");
}
```

### 2. Ограничение размера файлов

Максимальные значения определяются в `config.json` (секция `FileAccess`):

- `SandboxDir` — корневой каталог песочницы (обязательный, задаётся только на сервере)
- `BackupDir` — каталог резервных копий (при отсутствии — рядом с оригиналом)
- `MaxOneFileSizeInBytes` — размер одного файла (4 МБ)
- `MaxTotalSizeInBytes` — общий размер всех прочитанных файлов (10 МБ)
- `MaxQuantityFiles` — количество файлов одновременно (5)
- `MaxSearchResults` — количество файлов, возвращаемых поиском по маске и по содержимому (50)
- `ApiKey` — API-ключ для доступа (заголовок `X-Api-Key`; пусто — аутентификация отключена)
- `RateLimit` — ограничение частоты запросов (см. раздел "Rate limiting" ниже)

### 2.1. Rate limiting (защита от floods/DDoS)

Ограничение частоты запросов — дополнительный уровень защиты от application-level атак. Детали настройки, реализации и
поведения при превышении лимита (`429 Too Many Requests`) см. в разделе "Rate limiting (секция `RateLimit`)".
Кэш подтверждений удаления (`file_delete`) дополнительно ограничен `Consts.MaxPendingDeleteConfirmations`
(см. раздел "Двухэтапное подтверждение удаления").

### 2.2. Аутентификация по API-ключу и CORS

Доступ к серверу защищён middleware `ApiKeyMiddleware` (`Core/ApiKeyMiddleware.cs`):

- Ключ задаётся опцией `FileAccess:ApiKey` в `config.json`; передаётся в заголовке `X-Api-Key`.
- Если ключ **не задан/пуст** — проверка отключена (fail-open, обратная совместимость); при старте логируется `Warning: ApiKey is empty — running without authentication`. Если задан — каждый запрос без корректного `X-Api-Key` получает `401 Unauthorized`.
- Сравнение выполняется с **постоянным временем** (`CryptographicOperations.FixedTimeEquals`) — защита от timing-атак.
- Значение читается из `IOptionsMonitor<AppOptions>` на каждом запросе — **hot reload**: изменение ключа применяется без перезапуска.
- Middleware регистрируется через `app.UseMiddleware<ApiKeyMiddleware>()` **до** `UseRateLimiter`/`MapMcp` (неаутентифицированные запросы не тратят лимит rate limiter).

CORS-политика `AllowLocalhost` с `AllowAnyOrigin()` **удалена** (`AddCors`/`UseCors` отсутствуют) — кросс-доменные браузерные запросы не разрешены.

### 3. Проверка на символы ссылок

`PathValidator.CheckForSymlinkInPath()` проверяет каждый существующий компонент пути (включая конечный файл) на наличие symlink/reparse point и разрешает ссылку в фактическое местоположение через `ResolveLinkTarget`:

```csharp
FileSystemInfo? resolvedTarget = Directory.Exists(nextPath)
    ? Directory.ResolveLinkTarget(nextPath, returnFinalTarget: true)
    : File.ResolveLinkTarget(nextPath, returnFinalTarget: true);

if (resolvedTarget is null)
    return false; // Ссылка не разрешается — блокируем (fail-closed)

string resolvedFullPath = Path.GetFullPath(resolvedTarget.FullName);
if (!IsPathWithinRoot(resolvedRoot, resolvedFullPath, comparison))
    return false; // Symlink ведёт за пределы песочницы
```

> Важно: `Path.GetFullPath()` не разрешает symlink — только строковая нормализация. Для определения фактического местоположения ссылки обязательно используется `ResolveLinkTarget(returnFinalTarget: true)`, который разрешает и цепочки ссылок.

---

## Утилиты и инструменты (Core)

### 1. Валидация путей (PathValidator)

Всегда используйте `PathValidator.ValidateAndGetAbsolutePath()`:

```csharp
string absolutePath;
var result = _pathValidator.ValidateAndGetAbsolutePath(filePath, out absolutePath);
if (result != null) return /* error result */;

// Работайте с absolutePath
```

### 2. Получение MIME-типа (Helpers)

```csharp
string contentType = Helpers.GetContentTypeFromExtension(".json");
// Возвращает: "application/json"
```

### 3. SandboxContext

```csharp
// Значения из конфигурации (config.json): SandboxDir / BackupDir
string? sandboxDir = _sandboxContext.SandboxDir;
string? backupDir = _sandboxContext.BackupDir;
```

---

## Best Practices

### 1. Всегда используйте async/await

Все методы работают асинхронно для предотвращения блокировки потока.

### 2. Обработка исключений

Для операций с файлами используйте единый блок catch с типизированными результатами:

```csharp
try
{
    string fileContent = await File.ReadAllTextAsync(filePath, textEncoding);
    return new FileReadResult()
    {
        IsSuccess = true,
        Path = filePath,
        Content = fileContent
    };
}
catch (UnauthorizedAccessException)
{
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
```

Правила обработки исключений:

- **Контролируемые ошибки** (`File not found`, `Directory not found`, `Security error`, `File too large`, валидация параметров) возвращают клиенту понятное сообщение с путём/причиной.
- **Внутренние исключения** (`IOException`, любые другие) **не раскрывают** `ex.Message` клиенту: исключение полностью логируется (`_logger.LogError(ex, ...)` с `CorrelationId`), а в ответе — generic-текст + `Correlation ID: <id>`.
- Correlation id — `Helpers.GetCorrelationId()`: `Activity.Current.TraceId` (W3C TraceContext текущего запроса) либо случайный `Guid` вне HTTP-запроса. Позволяет связать ответ клиента с деталями в логах.

### 3. Логирование

Используйте `ILogger<T>` для логирования событий:

```csharp
_logger.LogInformation("File processed successfully: {FilePath}", filePath);
_logger.LogWarning("Failed to create backup file for {FilePath}", filePath);
_logger.LogError(ex, "Exception occurred while processing file: {FilePath}", filePath);
```

Формат вывода — **JSON-консоль** (`builder.Logging.AddJsonConsole()`), пригодный для контейнерных рантаймов и агрегаторов логов (Loki/ELK и т.п.):

- одна JSON-запись на строку: `{"Timestamp": "...", "LogLevel": "...", "Category": "...", "Message": "...", "State": {...}}`;
- временные метки — **UTC** в формате ISO 8601 (`yyyy-MM-ddTHH:mm:ss.fffZ`);
- `Debug` и выше пишутся в **stderr**, остальное — в stdout (`LogToStandardErrorThreshold`);
- структурированные параметры (`{FilePath}` и т.п.) сохраняются в поле `State` (для них поддерживается структурированный поиск).

### 4. Типизированные результаты

Всегда возвращайте типизированные модели, наследуемые от `OperationResult`, а не сырые строки JSON. MCP SDK сам сериализует результат.

---

## Тестирование

### 1. Unit-тесты

Тестируйте методы сервиса отдельно:

```csharp
[Fact]
public async Task FileReadAsync_ShouldReturnSuccessForValidFile()
{
    var service = new FileService(logger, options, backupService);
    var result = await service.FileReadAsync("/path/to/file.txt");

    Assert.True(result.IsSuccess);
    Assert.NotNull(result.Content);
}

[Fact]
public async Task FileReadAsync_ShouldReturnErrorForLargeFile()
{
    var service = new FileService(logger, options, backupService);
    // Arrange: Create large file (> MaxOneFileSizeInBytes)
    var result = await service.FileReadAsync(largeFilePath);

    Assert.False(result.IsSuccess);
    Assert.Contains("too large", result.Message);
}
```

### 2. Интеграционные тесты

Проверяйте работу через MCP протокол:

```csharp
[Fact]
public async Task DirectoryTree_ShouldReturnValidResponse()
{
    var tools = new DirTools(logger, dirService, pathValidator);
    var result = await tools.GetDirectoryAsync("/sandbox/src", nesting: 2);

    Assert.True(result.IsSuccess);
    Assert.NotNull(result.Result);
}
```

---

## Примеры использования

### 1. Чтение файла по частям

```csharp
var result = await fileTools.ReadFileChunksAsync(
    "/path/to/large/file.txt",
    startChar: 0,
    charsCount: 500000);

Assert.True(result.IsSuccess);
Assert.NotNull(result.Content);
if (result.HasMore == true)
{
    // Читаем следующую часть с result.NextStartChar
}
```

### 2. Создание резервной копии

Резервное копирование происходит автоматически при вызове `FileWriteAsync` (и других изменяющих операциях). Если нужно управлять вручную:

```csharp
var backupResult = await backupService.CreateBackupFileAsync("/path/to/file.txt");
if (backupResult.IsSuccess && backupResult.BackupPath != null)
{
    // Файл скопирован в BackupDir или рядом с оригиналом (BackupPath)
}
```

#### Откат при прерванной операции

Если критическая операция, ради которой создавался бэкап, прервана/отменена (исключение), сервис выполняет откат:

- `file_write` / `file_append` / `file_replace_lines` / `file_move` / `file_copy` → целевой файл восстанавливается из бэкапа (`RestoreBackupFileAsync`), затем бэкап удаляется (`DeleteBackupFileAsync`) — защита от частичной записи/копирования;
- `file_delete` → бэкап просто удаляется (оригинал при сбое удаления не затронут).

Ошибки восстановления/удаления не маскируют исходную ошибку и логируются отдельно (best-effort), поэтому «сиротских» `.bak` при штатном сбое не остаётся.

> **Operations: TTL/ротация бэкапов.** Успешные операции оставляют `.bak.<guid>` навсегда (TTL/ротации в коде нет). При интенсивных write-операциях `BackupDir` может переполниться — очистка старых бэкапов выполняется на уровне ops (cron/systemd timer, например `find $BACKUP_DIR -name '*.bak' -mtime +30 -delete`). Внутри процесса бэкап удаляется только при откате прерванной операции.

### 3. Многопоточное чтение файлов

```csharp
var results = await fileTools.ReadMultipleFilesAsync(
    new[] { "/file1.txt", "/file2.json" },
    encoding: "utf-8");

Assert.True(results.IsSuccess);
foreach (var fileResult in results.Files)
{
    if (fileResult.IsSuccess)
        Console.WriteLine(fileResult.Content);
}
```

### 4. Копирование и перемещение файлов

```csharp
// Копирование с перезаписью
var copyResult = await fileService.FileCopyAsync(
    "/source.txt", "/backup/source.txt", isRewrite: true);

// Перемещение (создаст недостающие директории назначения)
var moveResult = await fileService.FileMoveAsync(
    "/source.txt", "/archive/2026/source.txt", isRewrite: false);
```

### 5. Удаление с подтверждением

```csharp
// Первый вызов — запрашиваем подтверждение (Code = 202)
var request = await fileTools.FileDeleteAsync("/obsolete.txt");

// Второй вызов — выполняем удаление с токеном из первого ответа
var deleteResult = await fileTools.FileDeleteAsync("/obsolete.txt", confirmToken: token);
```

---

## Настройка конфигурации

### Hot reload через IOptionsMonitor\<T\>

Конфигурация читается из `config.json` (`reloadOnChange: true`), поэтому изменения применяются **без перезапуска сервиса**.
Сервисы должны получать опции через `IOptionsMonitor<AppOptions>` и обращаться к `CurrentValue`, а не к разово захваченному `IOptions<AppOptions>.Value`:

```csharp
public FileService(IOptionsMonitor<AppOptions> fileOptions, ...)
{
    _fileOptions = fileOptions;
}

// При каждом обращении — актуальное значение конфигурации
if (fileInfo.Length > _fileOptions.CurrentValue.MaxOneFileSizeInBytes)
```

### Валидация конфигурации

`ConfigValidator.Validate(AppOptions)` проверяет конфигурацию при старте и при каждой перезагрузке:

- при старте некорректные параметры → логируется перечень ошибок, приложение завершает работу с кодом `1`;
- при перезагрузке некорректные параметры → `IOptionsMonitor.OnChange` логирует ошибки и запускает **graceful shutdown** через `IHostApplicationLifetime.StopApplication()` (in-flight запросы успевают завершиться), код возврата процесса — `1`.

Правила (помимо путей и положительных лимитов файловых операций): если `RateLimit.Enabled = true`, то
`WindowInSeconds > 0`, `PermitLimit > 0`, `QueueLimit >= 0`.

Подключение в `Program.cs`:

```csharp
var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
var errors = ConfigValidator.Validate(appOptions);
if (errors.Count > 0)
{
    // вывод ошибок и Environment.Exit(1) — хост ещё не запущен, in-flight запросов нет
}

var optionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<AppOptions>>();
optionsMonitor.OnChange(newOptions =>
{
    var reloadErrors = ConfigValidator.Validate(newOptions);
    if (reloadErrors.Count > 0)
    {
        // вывод ошибок, затем graceful shutdown:
        // exitCode = 1; app.Lifetime.StopApplication();
    }
    // ...
});
```

### Graceful shutdown (IHostApplicationLifetime)

Остановка приложения выполняется **плавно** через `IHostApplicationLifetime` (`WebApplication.Lifetime`):

- Подписки `ApplicationStarted` / `ApplicationStopping` / `ApplicationStopped` логируют фазы жизненного цикла (старт, начало остановки с указанием таймаута, завершение с затраченным временем).
- `ApplicationStopping` → Kestrel перестаёт принимать **новые** запросы и ждёт завершения **in-flight** (активные MCP-вызовы, файловые операции) не дольше `Consts.DefaultShutdownTimeoutSeconds` (30 с), затем принудительно завершается.
- Таймаут задаётся через `HostOptions.ShutdownTimeout` (`builder.Services.Configure<HostOptions>(...)`).
- При некорректной перезагрузке конфигурации вместо жёсткого `Environment.Exit(1)` вызывается `app.Lifetime.StopApplication()` — процесс «мягко» гасится, а `Main` возвращает код `1`.
- Обычное завершение по Ctrl+C/SIGTERM — код возврата `0`.

### Переменные окружения (ENV-конфигурация)

Все параметры секции `FileAccess` можно задавать или переопределять переменными окружения. Синтаксис — стандартный для ASP.NET Core:
**двойное подчёркивание `__` = разделитель `:`**, регистр ключа не важен:

| Параметр `config.json` | Переменная окружения |
|------------------------|----------------------|
| `FileAccess:SandboxDir` | `FILEACCESS__SANDBOXDIR` |
| `FileAccess:BackupDir` | `FILEACCESS__BACKUPDIR` |
| `FileAccess:ApiKey` | `FILEACCESS__APIKEY` |
| `FileAccess:MaxTreeEntries` | `FILEACCESS__MAXTREEENTRIES` |
| `FileAccess:RateLimit:Enabled` | `FILEACCESS__RATELIMIT__ENABLED` |
| `FileAccess:RateLimit:PermitLimit` | `FILEACCESS__RATELIMIT__PERMITLIMIT` |

- **Приоритет:** ENV **выше** `config.json` — провайдер `AddEnvironmentVariables()` добавляется ПОСЛЕ файла (удобно для k8s ConfigMap/Secret, Docker, CI). Значения из ENV переопределяют JSON.
- Переменные читаются **при старте** (без hot reload, в отличие от `config.json`); стартовая валидация `ConfigValidator` учитывает значения из ENV.
- `config.json` всё равно должен существовать (даже полностью пустой/минимальный) — источник привязки секции `FileAccess`.

### Health-эндпоинты (/healthz, /readyz)

Для k8s (liveness/readiness-пробы) и обратных прокси добавлены HTTP-эндпоинты:

- `GET /healthz` — **liveness**, `GET /readyz` — **readiness**. Отвечают `200 OK` с текстом `Healthy` (ASP.NET Core Health Checks, `builder.Services.AddHealthChecks()` + `app.MapHealthChecks(...)`).
- Эндпоинты **публичные**: исключены из проверки API-ключа (`ApiKeyMiddleware.PublicPaths`) — иначе пробы k8s были бы недоступны при включённом `FileAccess:ApiKey`.
- Не проходят файловые операции и не требуют MCP-контекста; нагрузка минимальна, поэтому подходят для частых проб (противоположность `health`-инструменту MCP).

### Генерация конфига по умолчанию

`ConfigManager` отвечает за расположение файла конфигурации и генерацию дефолтного конфига:

- `ResolveConfigPath(args)` — путь из `--config <path>` или `config.json` (рабочий каталог / каталог приложения);
- `TryGenerateDefaultConfig(configPath, out error)` — создаёт конфиг по умолчанию, **если файла нет** (существующий не перезаписывается);
- `IsGenerateConfigRequested(args)` — `--generate-config` / `--init`;
- `IsHelpRequested(args)` / `PrintUsage()` — справка `--help` / `-h`.

Содержимое `config.json`:

```json
{
  "FileAccess": {
    "SandboxDir": "C:\\AI-Sandbox\\projects",
    "BackupDir": "C:\\AI-Sandbox\\backups",
    "MaxOneFileSizeInBytes": 4194304,
    "MaxQuantityFiles": 5,
    "MaxTotalSizeInBytes": 10485760,
    "MaxTreeEntries": 2000,
    "MaxSearchResults": 50,
    "ApiKey": "",
    "RateLimit": {
      "Enabled": true,
      "WindowInSeconds": 60,
      "PermitLimit": 100,
      "QueueLimit": 0
    }
  }
}
```

> `SandboxDir` обязателен: без него приложение не стартует (валидация). `BackupDir` необязателен. Каталоги `SandboxDir`/`BackupDir` создаются автоматически при старте.

### Rate limiting (секция `RateLimit`)

Ограничение частоты запросов реализовано на встроенном middleware ASP.NET Core (`AddRateLimiter`/`UseRateLimiter`, пакет `System.Threading.RateLimiting` из shared framework). Алгоритм — **fixed window по клиентскому IP** (`RemoteIpAddress`); при превышении лимита запрос получает `429 Too Many Requests`, событие логируется с уровнем `Warning`.

Вложенная модель `RateLimitOptions` (`Models/AppOptions.cs`):

| Опция | Тип | По умолчанию | Описание |
|-------|-----|--------------|----------|
| `Enabled` | `bool` | `false` | Включает/выключает ограничение запросов. |
| `WindowInSeconds` | `int` | `60` | Длительность окна в секундах. |
| `PermitLimit` | `int` | `100` | Максимум запросов от одного IP за окно. |
| `QueueLimit` | `int` | `0` | Размер очереди ожидания; `0` — без очереди, запросы сверх лимита сразу получают `429`. |

Реализация в `Program.cs`:

```csharp
builder.Services.AddRateLimiter(limiterOptions =>
{
    limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiterOptions.OnRejected = /* лог Warning с RemoteIp/Path/Endpoint */;
    limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        // Если секция RateLimit не задана или Enabled = false — GetNoLimiter
        // Партиция: RemoteIpAddress + параметры лимита
    });
});
// ...
app.UseRateLimiter(); // перед app.MapMcp()
```

Особенности:

- Лимитер **partitioned**: отдельное окно на каждый клиентский IP.
- Значения читаются из `IOptionsMonitor<AppOptions>` на каждом запросе — **hot reload работает без перезапуска**: ключ партиции включает параметры лимита (`IP|Permit|Window|Queue`), поэтому при их изменении создаётся новый лимитер.
- Защищает от **application-level** флуда (запросы к CPU/диску). От объёмных сетевых DDoS нужен обратный прокси/CDN (nginx, Cloudflare).

Валидация (в `ConfigValidator`): если `RateLimit.Enabled = true`, то `WindowInSeconds > 0`, `PermitLimit > 0`, `QueueLimit >= 0`; нарушение — как и остальные ошибки конфигурации (завершение приложения).

---

## DI регистрация (Program.cs)

Полный список регистраций в DI:

```csharp
// Конфигурация: секция FileAccess, reloadOnChange: true
builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));

// Scoped (зависят от конфигурации песочницы)
builder.Services.AddScoped<ISandboxContext, SandboxContext>();
builder.Services.AddScoped<IBackupOperations, BackupService>();
builder.Services.AddScoped<PathValidator, PathValidator>();

// Transient (stateless)
builder.Services.AddTransient<IFileOperations, FileService>();
builder.Services.AddTransient<IDirectoryOperations, DirService>();

// MCP сервер
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<FileTools>()
    .WithTools<DirTools>()
    .WithTools<HealthTools>();

// Rate limiting (fixed window по клиентскому IP; секция FileAccess:RateLimit)
builder.Services.AddRateLimiter(limiterOptions => { /* см. раздел "Rate limiting" */ });

// Middleware pipeline. CORS УДАЛЁН (не было AllowAnyOrigin).
// API-ключ проверяется ПЕРЕД rate limiter — неаутентифицированные запросы не тратят лимит.
app.UseMiddleware<ApiKeyMiddleware>(); // проверка X-Api-Key (если задан FileAccess:ApiKey)
app.UseRateLimiter();                  // fixed window по клиентскому IP
app.MapHealthChecks("/healthz");       // публичные liveness/readiness (без API-ключа)
app.MapHealthChecks("/readyz");
app.MapMcp();
```

---

## Запуск в Docker

В репозитории есть `Dockerfile` (мультистейдж) и `.dockerignore`. Приложение публикуется self-contained single-file и
запускается от непривилегированного пользователя `app` (UID 1654).

```bash
# сборка
docker build -t fileaccess-mcp:latest .

# запуск: смонтировать config.json (обязательно — в образе конфига нет!)
docker run -d --name fileaccess-mcp \
  -p 8080:8080 \
  -v "$(pwd)/config.json:/app/config.json:ro" \
  -v "$(pwd)/sandbox:/srv/sandbox:rw" \
  -e FILEACCESS__SANDBOXDIR=/srv/sandbox \
  fileaccess-mcp:latest
```

Важно:

- **config.json в образ НЕ копируется** (исключён в `.dockerignore`) — он содержит локальные пути/секреты. Смонтируйте свой
  конфиг (с Linux-путями) через volume или задайте все параметры переменными `FILEACCESS__*`.
- Каталоги `SandboxDir`/`BackupDir` должны быть доступны на запись пользователю `app` (UID 1654): смонтируйте видимые
  тома и при необходимости `chown -R 1654:1654`.
- Слушает порт `8080` (`ASPNETCORE_URLS=http://+:8080`).
- `HEALTHCHECK` в образе использует `curl http://localhost:8080/healthz` (эндпоинт публичный, API-ключ не требуется).
- **TLS завершается на ingress/обратном прокси** (nginx, Cloudflare, k8s Ingress/Gateway) — контейнер слушает обычный HTTP. Не выставляйте порт контейнера напрямую в интернет без прокси; шифрование «до прокси» — обязанность proxy/ingress-слоя.

Пример развёртывания в **k8s** (liveness/readiness через `/healthz`/`/readyz`, конфигурация через `ConfigMap`/`Secret` + ENV):

```yaml
containers:
  - name: fileaccess
    image: fileaccess-mcp:latest
    ports: [{ containerPort: 8080 }]
    env:
      - name: FILEACCESS__SANDBOXDIR
        value: /srv/sandbox
      - name: FILEACCESS__APIKEY
        valueFrom: { secretKeyRef: { name: fa-secrets, key: api-key } }
    livenessProbe:  { httpGet: { path: /healthz, port: 8080 }, initialDelaySeconds: 10, periodSeconds: 10 }
    readinessProbe: { httpGet: { path: /readyz,  port: 8080 }, initialDelaySeconds: 5,  periodSeconds: 10 }
```

---

## Тестирование (регрессия security-путей)

Проект `FileAccessMcpServer.Tests` (xUnit, `dotnet test`) покрывает security-критичные компоненты:

- **`PathValidatorTests`** — chroot (`..`, sibling-prefix, путь вне песочницы) и symlink-защита (ссылка наружу блокируется, внутрь — разрешается; при недоступности создания symlink на Windows тест пропускается).
- **`ApiKeyMiddlewareTests`** — fail-open при пустом ключе, `401` при неверном/отсутствующем ключе (в т.ч. той же длины — ветка `FixedTimeEquals`), публичные `/healthz`/`/readyz` без ключа, строгое сравнение путей.
- **`FileToolsDeleteTokenTests`** — двухэтапное подтверждение `file_delete`: 32-hex токен (`202`), одноразовое потребление, повторное использование токена → `[ОШИБКА]`, неверный токен → `[ОТКЛОНЕНО]`, параллельные вызовы с одним токеном → ровно один победитель (lock). Логика токенов тестируется на фейковом `IFileOperations` (детерминированно), плюс один сквозной сценарий с реальным `FileService`.
- **`BackupRollbackTests` / `BackupServiceTests`** — откат при прерванной операции: `RestoreBackupFileAsync` + `DeleteBackupFileAsync` вызываются, `.bak` не остаётся, `ex.Message` не утекает (generic + `Correlation ID`). Инжекция сбоя через sharing violation доступна только на Windows (тест пропускается на других ОС).

```bash
dotnet test
```

---

## Структура проекта для расширения

### При добавлении нового функционала:

1. **New Features**:
   - Новый интерфейс в `Interfaces/`
   - Реализация сервиса в `Services/`
   - MCP инструмент в `Tools/`
   - Регистрация в `Program.cs`

2. **Extensions**:
   - Расширения существующих сервисов как отдельные файлы
   - Новые модели данных в `Models/`

3. **Utilities**:
   - Новые утилиты в `Core/` (отдельные классы для больших групп функций)

---

## Контакты и поддержка

Для вопросов по расширению проекта обращайтесь к maintainer-у или создавайте issue на GitHub.

При добавлении нового функционала соблюдайте:

- Существующий стиль кода (кодировка UTF-8, отступы 4 пробела)
- Наличие XML документации для всех публичных методов
- Соблюдение принципов безопасности песочницы
- Использование типизированных моделей (не сырых строк JSON)
