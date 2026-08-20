# FileAccessMcpServer — API Reference

Подробный справочник по API MCP-сервера **FileAccessMcpServer** (.NET 8). Документ описывает:

- MCP-инструменты (слой `Tools/`) — сигнатуры, параметры, возвращаемые модели, коды ответов и примеры вызовов;
- сервисы и контракты (`Services/`, `Interfaces/`);
- модели данных (`Models/`);
- базовые утилиты (`Core/`);
- конфигурацию (`config.json`).

> Актуально для версии **0.1.0-beta** (2026).

---

## 1. Общие сведения

- **Транспорт:** MCP over HTTP, stateless (`o.Stateless = true`).
- **HTTP-эндпоинты:** публичные `GET /healthz` (liveness) и `GET /readyz` (readiness) — без API-ключа, для k8s и обратных прокси (§2.7).
- **Регистрация инструментов:** `Program.cs` → `builder.Services.AddMcpServer().WithTools<FileTools>().WithTools<DirTools>().WithTools<HealthTools>()`.
- **Язык кода:** C# 12, `internal` типы, `Nullable enable`, `ImplicitUsings enable`.
- **Все инструменты возвращают типизированные модели** (наследники `OperationResult`); исключение — `health`, возвращает строку JSON.
- **Все пути** проходят через `PathValidator.ValidateAndGetAbsolutePath()` перед обращением к файловой системе.
- **Конфигурация:** `config.json` (hot reload) + переопределение через переменные окружения `FILEACCESS__*` (приоритет ENV, применяются при старте; §2.8).
- **Логирование:** JSON-консоль (`AddJsonConsole`) — одна JSON-запись на строку, метки времени UTC ISO 8601, `Debug`+ → stderr (§8).

### 1.1. Базовый тип результата

```csharp
internal class OperationResult
{
    public bool   IsSuccess { get; set; }   // успех операции
    public string? Message  { get; set; }   // сообщение об ошибке или описание
    public int?   Code      { get; set; }   // код ответа (см. ниже)
}
```

Фабрики:

```csharp
OperationResult.Success()            // IsSuccess = true,  Code = 200
OperationResult.Fail(string message) // IsSuccess = false, Code = 400, Message = message
```

### 1.2. Коды ответа

| Code | Значение |
|------|----------|
| `200` | Успех |
| `202` | Требуется подтверждение деструктивной операции (двухэтапное удаление) |
| `400` | Ошибка валидации параметров / общая ошибка (`OperationResult.Fail`) |
| `404` | Файл или директория не найдены |
| `null` | Код не задан (например, отклонённый/истёкший токен подтверждения в `file_delete`) |

Успех операции всегда проверяется по `IsSuccess == true`.

---

## 2. Конфигурация

Секция `"FileAccess"` в `config.json`. Файл читается с `reloadOnChange: true`, изменения применяются **без перезапуска сервиса**
(опции доступны через `IOptionsMonitor<AppOptions>`).

```json
{
  "FileAccess": {
    "SandboxDir": "D:\\AI-Sandbox\\",
    "BackupDir": "D:\\AI-Backup\\",
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

### Опции (`AppOptions`)

| Опция | Тип | По умолчанию | Описание |
|-------|-----|--------------|----------|
| `SandboxDir` | `string?` | — | Корневой каталог песочницы. **Обязателен**: без него все операции возвращают ошибку. Задаётся только на сервере. |
| `BackupDir` | `string?` | — | Каталог резервных копий. Если не задан — `.bak` создаётся рядом с оригиналом. |
| `MaxOneFileSizeInBytes` | `long` | — | Макс. размер одного файла (4 МБ). Применяется к чтению, записи, изображениям, бинарным файлам, поиску по содержимому. |
| `MaxQuantityFiles` | `int` | — | Макс. количество файлов в batch-чтении (5). |
| `MaxTotalSizeInBytes` | `long` | — | Суммарный лимит batch-чтения (10 МБ). |
| `MaxTreeEntries` | `int` | — | Лимит записей дерева/списка (2000); при превышении ответ усекается (флаг не выставляется, только лог). |
| `MaxSearchResults` | `int` | `50` | Лимит результатов поиска (`Consts.DefaultMaxSearchResults`); при превышении `LimitReached = true`. |
| `ApiKey` | `string?` | пусто | API-ключ доступа. Если пуст/не задан — аутентификация отключена (fail-open). Если задан — каждый запрос должен передавать заголовок `X-Api-Key`, иначе `401` (§2.5). |
| `RateLimit` | `RateLimitOptions?` | — | Настройки rate limiting (§2.2). Если секция не задана или `Enabled = false` — ограничение не применяется. |

### 2.1. Hot reload (перезагрузка без перезапуска)

`config.json` подключается через `AddJsonFile(path, optional: false, reloadOnChange: true)`, а все потребители используют
`IOptionsMonitor<AppOptions>.CurrentValue` (`SandboxContext`, `FileService`, `DirService`, `FileTools`).
Достаточно сохранить файл — новые значения применяются на следующих запросах.

### 2.2. Rate limiting (защита от floods/DDoS)

Ограничение частоты запросов реализовано на встроенном middleware ASP.NET Core
(`AddRateLimiter` / `UseRateLimiter`, пакет `System.Threading.RateLimiting` из shared framework).
Алгоритм — **fixed window по клиентскому IP** (`RemoteIpAddress`). При превышении лимита запрос получает код `429 Too Many Requests`, событие логируется с уровнем `Warning`.

Вложенная секция `"FileAccess:RateLimit"` (`RateLimitOptions`):

| Опция | Тип | По умолчанию | Описание |
|-------|-----|--------------|----------|
| `Enabled` | `bool` | `false` | Включает/выключает ограничение запросов. |
| `WindowInSeconds` | `int` | `60` | Длительность окна в секундах. |
| `PermitLimit` | `int` | `100` | Максимум запросов от одного IP за окно (`Consts.DefaultRateLimitPermitLimit`). |
| `QueueLimit` | `int` | `0` | Размер очереди ожидания. `0` — без очереди, запросы сверх лимита сразу получают `429`. |

Особенности реализации:

- Лимитер **partitioned**: отдельное окно на каждый клиентский IP.
- Параметры читаются из `IOptionsMonitor<AppOptions>` в момент запроса, поэтому **hot reload конфигурации работает без перезапуска** — при изменении `PermitLimit`/`WindowInSeconds`/`QueueLimit` для IP создаётся новый лимитер.
- Защищает от **application-level** флуда (запросы к CPU/диску). От volumetric DDoS на сетевом уровне нужен обратный прокси/CDN (nginx, Cloudflare).

### 2.3. Валидация конфигурации

`ConfigValidator.Validate(AppOptions)` (см. §7.5) проверяет параметры:

| Правило | При нарушении |
|---------|---------------|
| `SandboxDir` задан, абсолютный путь, директория (не файл) | приложение завершает работу с кодом `1` |
| `BackupDir` (если задан) — абсолютный путь, директория (не файл) | то же |
| `MaxOneFileSizeInBytes`, `MaxQuantityFiles`, `MaxTotalSizeInBytes`, `MaxTreeEntries`, `MaxSearchResults` — положительные | то же |
| `MaxTotalSizeInBytes >= MaxOneFileSizeInBytes` | то же |
| Если `RateLimit.Enabled = true`: `WindowInSeconds > 0`, `PermitLimit > 0`, `QueueLimit >= 0` | то же |

- **При старте:** список ошибок выводится в консоль, вызывается `Environment.Exit(1)` (хост ещё не запущен, in-flight запросов нет).
- **При перезагрузке:** подписка `IOptionsMonitor<AppOptions>.OnChange` логирует ошибки и запускает **graceful shutdown** через `IHostApplicationLifetime.StopApplication()` (in-flight запросы успевают завершиться); `Main` возвращает код `1`.
- Каталоги `SandboxDir`/`BackupDir` создаются автоматически при старте и после перезагрузки.

### 2.4. Генерация конфига по умолчанию и CLI

Если `config.json` отсутствует, его можно сгенерировать:

| Аргумент | Описание |
|----------|----------|
| `--generate-config` / `--init` | Создать конфиг по умолчанию, **если файла нет**, и выйти. Существующий файл не перезаписывается. |
| `--config <path>` | Путь к файлу конфигурации (по умолчанию `config.json`: рабочий каталог → каталог приложения). |
| `--help`, `-h` | Справка и выход. |

Генерируется конфиг со значениями по умолчанию (`SandboxDir`/`BackupDir` — каталоги `sandbox`/`backup` рядом с конфигом,
`MaxOneFileSizeInBytes = 4 MiB`, `MaxQuantityFiles = 5`, `MaxTotalSizeInBytes = 10 MiB`, `MaxTreeEntries = 2000`, `MaxSearchResults = 50`,
`ApiKey = ""`, `RateLimit = { Enabled: true, WindowInSeconds: 60, PermitLimit: 100, QueueLimit: 0 }`).

### 2.5. Аутентификация и CORS

Доступ защищён middleware `ApiKeyMiddleware` (`Core/ApiKeyMiddleware.cs`):

- Ключ — опция `FileAccess:ApiKey`; заголовок — `X-Api-Key`.
- `ApiKey` пуст/не задан → проверка **отключена** (fail-open, обратная совместимость); при старте логируется `Warning: ApiKey is empty — running without authentication`. Ключ задан → неверный/отсутствующий `X-Api-Key` → `401 Unauthorized`, запрос завершается до MCP-эндпоинта.
- Сравнение — `CryptographicOperations.FixedTimeEquals` (постоянное время, защита от timing-атак).
- Значение читается из `IOptionsMonitor<AppOptions>` на каждом запросе — hot reload (смена ключа без перезапуска).
- Регистрация: `app.UseMiddleware<ApiKeyMiddleware>()` **до** `UseRateLimiter` и `MapMcp`.
- **CORS удалён**: политика `AllowLocalhost` (`AllowAnyOrigin`) убрана — `AddCors`/`UseCors` не используются.

### 2.6. Graceful shutdown (IHostApplicationLifetime)

Остановка приложения выполняется плавно через `IHostApplicationLifetime` (`WebApplication.Lifetime`):

- Хост вызывает подписки `ApplicationStarted` → `ApplicationStopping` → `ApplicationStopped`; каждая фаза логируется (старт, начало остановки с таймаутом, завершение с затраченным временем).
- Начиная с `ApplicationStopping`, Kestrel не принимает **новые** запросы и ждёт завершения **in-flight** (активные MCP-вызовы/файловые операции) в течение `Consts.DefaultShutdownTimeoutSeconds` (30 с), после чего принудительно завершается. Таймаут задаётся `HostOptions.ShutdownTimeout` в `Program.cs`.
- Обычное завершение (Ctrl+C/SIGTERM) → код `0`; graceful-остановка из-за некорректной перезагрузки конфигурации → код `1`.

### 2.7. Health-эндпоинты (/healthz, /readyz)

Для k8s (liveness/readiness-пробы) и обратных прокси добавлены HTTP-эндпоинты:

- `GET /healthz` — **liveness**, `GET /readyz` — **readiness**; оба возвращают `200 OK` и текст `Healthy` (ASP.NET Core Health Checks: `AddHealthChecks()` + `MapHealthChecks("/healthz")` / `MapHealthChecks("/readyz")`).
- Эндпоинты **публичные** — исключены из проверки API-ключа (`ApiKeyMiddleware.PublicPaths`), поэтому пробы k8s работают даже при включённом `FileAccess:ApiKey`.
- Не проходят через MCP-инструменты и файловые операции; минимальная нагрузка (в отличие от MCP-инструмента `health`).

### 2.8. Переменные окружения (ENV-конфигурация)

Параметры секции `FileAccess` можно задавать/переопределять переменными окружения — стандартный синтаксис ASP.NET Core
(**двойное подчёркивание `__` = разделитель `:`**, регистр не важен):

| Параметр `config.json` | Переменная окружения |
|------------------------|----------------------|
| `FileAccess:SandboxDir` | `FILEACCESS__SANDBOXDIR` |
| `FileAccess:ApiKey` | `FILEACCESS__APIKEY` |
| `FileAccess:MaxTreeEntries` | `FILEACCESS__MAXTREEENTRIES` |
| `FileAccess:RateLimit:Enabled` | `FILEACCESS__RATELIMIT__ENABLED` |
| `FileAccess:RateLimit:PermitLimit` | `FILEACCESS__RATELIMIT__PERMITLIMIT` |

- Провайдер `builder.Configuration.AddEnvironmentVariables()` добавляется **после** `config.json` → ENV имеет **приоритет** над файлом.
- Переменные читаются при старте, **без hot reload** (в отличие от `config.json`); стартовая валидация учитывает значения из ENV.
- `config.json` всё равно должен существовать как источник привязки секции (минимальный файл достаточно).

---

## 3. MCP Tools

### 3.1. DirTools — работа с директориями

#### `directory_tree`

Построение дерева каталогов **без** файлов (только директории).

```scharp
GetDirectoryAsync(string basePath, int nesting = 5)
→ FSEntriesResult
```

| Параметр | Тип | Обязат. | Описание |
|----------|-----|---------|----------|
| `basePath` | `string` | да | Путь к корневой директории (абсолютный или относительно песочницы). |
| `nesting` | `int` | нет (`5`) | Уровень вложенности. Вне диапазона `1..10` принудительно выставляется `10`. |

Поведение:

- Корневая запись имеет `Type = "RootDirectory"`.
- Символьные ссылки (symlink/junction) отображаются, но **не обходятся**.
- Лимит записей — `MaxTreeEntries`.

Пример вызова:

```json
{ "name": "directory_tree", "arguments": { "basePath": "/sandbox/projects", "nesting": 2 } }
```

#### `directory_tree_with_files`

Дерево каталогов **с файлами** и их размерами.

```
GetDirectoryTreeWithFilesAsync(string basePath, int nesting = 5)
→ FSEntriesResult
```

Параметры — как у `directory_tree`. Для файлов заполняется `Size` (байты).

#### `directory_list_files`

Список файлов в указанной директории (без рекурсии).

```
GetDirectoryListFilesAsync(string basePath = "")
→ FSEntriesResult
```

`basePath` пустая строка — корень песочницы.

#### `create_directory`

Создание директории (и всех недостающих родительских).

```
CreateDirectoryAsync(string path = "")
→ OperationResult
```

Пример вызова:

```json
{ "name": "create_directory", "arguments": { "path": "/sandbox/projects/new-app" } }
```

#### `search_files`

Рекурсивный поиск файлов по маске имени.

```
SearchFilesAsync(string basePath, string mask)
→ FileSearchResult
```

| Параметр | Тип | Обязат. | Описание |
|----------|-----|---------|----------|
| `basePath` | `string` | да | Директория, с которой начинается поиск. |
| `mask` | `string` | да | Маска имени файла; `*` — любое количество символов, `?` — один символ. |

Правила:
- Маска применяется к **имени** файла (не к пути).
- Маски `*`, `*.*`, `**` **заблокированы** (совпадают со всеми файлами).
- Регистр сравнения зависит от ОС (Windows/macOS — без учёта регистра, Linux — с учётом).
- symlink-директории не обходятся.
- Результат ограничен `MaxSearchResults`; при превышении `LimitReached = true`.

Пример вызова:

```json
{ "name": "search_files", "arguments": { "basePath": "/sandbox/src", "mask": "*.cs" } }
```

#### `search_files_content`

Рекурсивный поиск файлов по подстроке в содержимом (с учётом регистра).

```
SearchFilesByContentAsync(string basePath, string searchText, string mask = "", string encoding = "utf-8")
→ FileSearchResult
```

| Параметр | Тип | Обязат. | Описание |
|----------|-----|---------|----------|
| `basePath` | `string` | да | Директория поиска. |
| `searchText` | `string` | да | Подстрока для поиска (`StringComparison.Ordinal`). |
| `mask` | `string` | нет (`""`) | Опциональная маска имени; пусто — без фильтра. |
| `encoding` | `string` | нет (`"utf-8"`) | Кодировка файлов; BOM определяется автоматически. |

Правила: файлы размером больше `MaxOneFileSizeInBytes` пропускаются; те же ограничения на маски и symlink; лимит `MaxSearchResults`.

### 3.2. FileTools — работа с файлами

#### `file_read`

Чтение текстового содержимого файла.

```
ReadFileAsync(string filePath, string encoding = "utf-8")
→ FileReadResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `filePath` | `string` | Абсолютный путь к файлу. |
| `encoding` | `string` | Кодировка текста (utf-8, utf-16, windows-1251, cp866 и т.д.). |

Ошибки: файл > `MaxOneFileSizeInBytes` → `400`; файл не найден → `404`.

Пример:

```json
{ "name": "file_read", "arguments": { "filePath": "D:\\AI-Sandbox\\config.json", "encoding": "utf-8" } }
```

#### `file_write`

Создание нового файла или полная перезапись существующего. Автоматически создаёт недостающие директории; перед перезаписью создаёт резервную копию.

```
WriteFileAsync(string filePath, string content, string encoding = "utf-8")
→ FileWriteResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `filePath` | `string` | Абсолютный путь к файлу (файл может не существовать). |
| `content` | `string` | Полное содержимое файла. |
| `encoding` | `string` | Кодировка текста. |

Ошибки: размер содержимого > `MaxOneFileSizeInBytes` → `400`; нет прав → `400`.

#### `file_append`

Добавление текста **в конец** файла без перезаписи существующего содержимого. Если файла нет — создаёт его. Перед изменением существующего файла создаёт резервную копию.

```
AppendToFileAsync(string filePath, string content, string encoding = "utf-8")
→ FileWriteResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `filePath` | `string` | Абсолютный путь к файлу (может не существовать). |
| `content` | `string` | Текст, добавляемый в конец. |
| `encoding` | `string` | Кодировка текста. |

Ограничение: итоговый размер (существующий + добавляемый) ≤ `MaxOneFileSizeInBytes`.

#### `file_replace_lines` — отключён (не регистрируется)

> **Недоступно в текущей версии:** MCP-инструмент закомментирован в `Tools/FileTools.cs` и **не** входит в набор инструментов сервера.
> Сервисный метод `FileReplaceLinesAsync` существует в `IFileOperations`/`FileService` (§4.1), но через MCP не вызывается.
> Описание сигнатуры оставлено для справки.

Замена указанного диапазона строк файла новыми строками. Нумерация строк — с **1**. Перед изменением создаёт резервную копию.

```
ReplaceLinesAsync(string filePath, long startLine, long lineCount, string newContent, string encoding = "utf-8")
→ FileWriteResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `filePath` | `string` | Абсолютный путь к файлу. |
| `startLine` | `long` | Номер первой строки для замены (≥ 1). |
| `lineCount` | `long` | Количество заменяемых строк (≥ 1). |
| `newContent` | `string` | Новые строки. Может содержать несколько строк (через `\n`/`\r\n`). Пустая строка — удаление диапазона. |
| `encoding` | `string` | Кодировка текста. |

Поведение:
- Диапазон `startLine..startLine+lineCount-1` должен лежать внутри файла, иначе `400` с количеством строк.
- Сохраняется стиль переносов (CRLF/LF) и концевой перенос исходного файла.
- Итоговый размер файла ограничен `MaxOneFileSizeInBytes`.

Пример:

```json
{
  "name": "file_replace_lines",
  "arguments": {
    "filePath": "D:\\AI-Sandbox\\app\\Program.cs",
    "startLine": 10,
    "lineCount": 3,
    "newContent": "var x = 1;\nvar y = 2;"
  }
}
```

#### `files_read_multiple`

Пакетное чтение нескольких файлов (последовательно).

```
ReadMultipleFilesAsync(string[] filesPath, string encoding = "utf-8")
→ MultiFileReadResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `filesPath` | `string[]` | Массив путей (не более `MaxQuantityFiles`). |
| `encoding` | `string` | Кодировка текста. |

Правила:
- Пустой список → `400`.
- Превышение `MaxQuantityFiles` → `400`.
- Суммарный размер ≤ `MaxTotalSizeInBytes`; превысившие лимит файлы пропускаются с пометкой `[Skipped]` (без падения операции).
- `IsSuccess = true` всегда при корректном вызове; результат по каждому файлу — в `Files[]` (у каждого собственный `IsSuccess`).

#### `file_read_chunks`

Чтение файла по частям (блоками символов). Работает с файлами любой длины строки.

```
ReadFileChunksAsync(string filePath, long startChar = 0, int charsCount = 500_000, string encoding = "utf-8")
→ FileReadChunkResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `filePath` | `string` | Абсолютный путь к файлу. |
| `startChar` | `long` | Символ начала чтения (с 0). Отрицательное значение заменяется на 0. |
| `charsCount` | `int` | Размер блока. ≤ 0 → 500 000; > 1 000 000 → 1 000 000. |
| `encoding` | `string` | Кодировка текста. |

Возврат: `StartChar`, `CharsReturned`, `TotalCharsReadUpTo`, `HasMore`, `NextStartChar`, `Content`. Блок не обрезает surrogate-пары (дочитываются).

> В интерфейсе/сервисе значение по умолчанию `charsCount = 1_000_000`; в инструменте — `500_000`. Сервис возвращает ошибку при `startChar < 0` или `charsCount` вне `[1..1_000_000]`.

#### `file_get_info`

Метаданные файла.

```
GetFileInfoAsync(string filePath)
→ FileInfoResult
```

Возвращает: `Name`, `Extension`, `FileType` (MIME), `Length`, `FileCreate`, `FileLastUpdate`, `FileLastAccessTime`, `IsReadOnly`, `IsLink`, `LinkTarget`.

#### `file_hash`

Вычисление хеша (контрольной суммы) файла.

```
FileHashAsync(string filePath, string algorithm = "sha256")
→ FileHashResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `filePath` | `string` | Абсолютный путь к файлу (должен существовать). |
| `algorithm` | `string` | Алгоритм: `md5`, `crc32`, `sha256` (по умолчанию) или `sha512`. Регистр не важен. |

Неподдерживаемый алгоритм → `400`. Файл не найден → `404`.

Возвращает: `Path`, `Algorithm` (нормализованное имя), `Hash` (hex, нижний регистр), `SizeInBytes`.

Пример:

```json
{ "name": "file_hash", "arguments": { "filePath": "D:\\AI-Sandbox\\archive.zip", "algorithm": "sha256" } }
```

#### `file_image_read`

Чтение изображения → Base64 data URL (для мультимодальных ИИ).

```
ReadImageAsync(string fileImagePath)
→ FileImageResult
```

Ограничение размера: `MaxOneFileSizeInBytes`. Возвращает `FilePath`, `MimeType`, `SizeInBytes`, `DataUrl` вида `data:{mimeType};base64,{data}`.

#### `file_binary_read`

Чтение произвольного бинарного файла (PDF, ZIP, DLL и т.д.) → Base64.

```
ReadBinaryAsync(string filePath)
→ FileBinaryReadResult
```

Ограничение размера: `MaxOneFileSizeInBytes`. Возвращает `FilePath`, `MimeType`, `SizeInBytes`, `Base64Data`.

#### `file_copy`

Копирование файла внутри песочницы. Проверяются **оба** пути (источник и назначение).

```
FileCopyAsync(string originFilePath, string newFilePath, bool isRewrite = false)
→ OperationResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `originFilePath` | `string` | Исходный файл (должен существовать). |
| `newFilePath` | `string` | Файл назначения. |
| `isRewrite` | `bool` | Перезаписывать при существовании назначения (по умолчанию `false`). |

Поведение: создаются недостающие директории назначения; при `isRewrite=true` и существующем назначении создаётся резервная копия; `originFilePath == newFilePath` → `400`.

#### `file_move`

Перемещение/переименование файла внутри песочницы. Проверяются **оба** пути.

```
FileMoveAsync(string originFilePath, string newFilePath, bool isRewrite = false)
→ OperationResult
```

Поведение — как у `file_copy` (создание директорий, резервная копия при перезаписи).

#### `file_rename`

Алиас `file_move` для читаемости. Полностью идентичен по сигнатуре и поведению.

```
FileRenameAsync(string originFilePath, string newFilePath, bool isRewrite = false)
→ OperationResult
```

| Параметр | Тип | Описание |
|----------|-----|----------|
| `originFilePath` | `string` | Текущий путь к файлу (должен существовать). |
| `newFilePath` | `string` | Новый путь или новое имя файла. |
| `isRewrite` | `bool` | Перезаписывать при существовании назначения (по умолчанию `false`). |

Пример:

```json
{ "name": "file_rename", "arguments": { "originFilePath": "D:\\AI-Sandbox\\old.txt", "newFilePath": "D:\\AI-Sandbox\\new.txt" } }
```

#### `file_delete`

Удаление файла с **двухэтапным подтверждением** одноразовым токеном (TTL — 5 минут).

```
FileDeleteAsync(string filePath, string confirmToken = "")
→ OperationResult
```

Алгоритм:

1. **Первый вызов** с пустым `confirmToken`: генерируется токен из **32 hex-символов** (16 случайных байт через `RandomNumberGenerator`, криптостойкий), записывается в `MemoryCache` для `filePath`; ответ `IsSuccess = true, Code = 202` с сообщением, требующим спросить пользователя и повторить вызов с токеном.
2. **Второй вызов** с корректным токеном: последовательность «проверить токен → потратить (`TryConsume`, атомарно через `Interlocked.Exchange`) → удалить запись из кэша» выполняется в критической секции `_pendingConfirmationsLock` (`lock`); после этого файл удаляется (перед удалением — резервная копия).
3. Неверный токен → `IsSuccess = false` (сообщение `[ОТКЛОНЕНО]`).
4. Истёкший, отсутствующий или уже потраченный (запись удалена победителем гонки) токен → `IsSuccess = false` (сообщение `[ОШИБКА]`).

Кэш подтверждений (`_pendingConfirmations`):
- **статический** — токены переживают отдельные MCP-запросы, записи истекают через 5 минут (`SetAbsoluteExpiration`);
- **ограничен** `SizeLimit = Consts.MaxPendingDeleteConfirmations` (10 000) — защита памяти от флуда `file_delete` уникальными путями; каждая запись имеет `Size = 1`;
- ключ нормализуется `NormalizeCacheKey` — `Path.GetFullPath` + `TrimEndingDirectorySeparator`, на Windows без учёта регистра (`ToUpperInvariant`), чтобы `C:\File.txt` и `c:\file.txt` находили один токен;
- удаление защищено от гонки двух параллельных подтверждений с одинаковым токеном: `TryGetValue → TryConsume → Remove` целиком выполняются под `lock`, победитель сразу удаляет запись, поэтому второй запрос запись уже не находит и получает `[ОШИБКА]` (а не «уже использованный код»). Удаление файла — вне блокировки.

Пример (шаг 1):

```json
{ "name": "file_delete", "arguments": { "filePath": "D:\\AI-Sandbox\\old.txt", "confirmToken": "" } }
```

### 3.3. HealthTools

#### `health`

Проверка доступности сервера. Возвращает **строку** JSON.

```
HealthCheck()
→ string  // {"Status":"Ok","Timestamp":"2026-01-01T00:00:00Z"}
```

---

## 4. Слой Services

### 4.1. `FileService : IFileOperations`

| Метод | Сигнатура | Возврат |
|-------|-----------|---------|
| Информация | `FileGetInfoAsync(string filePath)` | `Task<FileInfoResult>` |
| Чтение | `FileReadAsync(string filePath, string encoding = "utf-8")` | `Task<FileReadResult>` |
| Запись | `FileWriteAsync(string filePath, string content, string encoding = "utf-8")` | `Task<FileWriteResult>` |
| Дописывание | `FileAppendAsync(string filePath, string content, string encoding = "utf-8")` | `Task<FileWriteResult>` |
| Замена строк | `FileReplaceLinesAsync(string filePath, long startLine, long lineCount, string newContent, string encoding = "utf-8")` | `Task<FileWriteResult>` (в MCP не экспонируется — инструмент `file_replace_lines` отключён) |
| Поблочное чтение | `FileReadChunksAsync(string filePath, long startChar = 0, int charsCount = 1_000_000, string encoding = "utf-8")` | `Task<FileReadChunkResult>` |
| Изображение | `FileImageReadAsync(string filePath)` | `Task<FileImageResult>` |
| Бинарный файл | `FileBinaryReadAsync(string filePath)` | `Task<FileBinaryReadResult>` |
| Копирование | `FileCopyAsync(string filePath, string newFilePath, bool isRewrite = false)` | `Task<OperationResult>` |
| Перемещение | `FileMoveAsync(string filePath, string newFilePath, bool isRewrite = false)` | `Task<OperationResult>` |
| Удаление | `FileDeleteAsync(string filePath)` | `Task<OperationResult>` |
| Хеш | `FileHashAsync(string filePath, string algorithm)` | `Task<FileHashResult>` |

Зависимости: `ILogger<FileService>`, `IOptionsMonitor<AppOptions>`, `IBackupOperations`.

### 4.2. `DirService : IDirectoryOperations`

| Метод | Сигнатура | Возврат |
|-------|-----------|---------|
| Список файлов | `GetFilesInDirectoryAsync(string path)` | `Task<List<FSEntrie>>` |
| Дерево | `GetDirectoryTreeAsync(string path, int nesting)` | `Task<List<FSEntrie>>` |
| Дерево с файлами | `GetDirectoryTreeWithFilesAsync(string path, int nesting)` | `Task<List<FSEntrie>>` |
| Создание директории | `CreateDirectoryAsync(string path)` | `Task<OperationResult>` |
| Поиск по маске | `SearchFilesAsync(string path, string mask)` | `Task<FileSearchResult>` |
| Поиск по содержимому | `SearchFilesByContentAsync(string path, string searchText, string? mask = null, string encoding = "utf-8")` | `Task<FileSearchResult>` |

Зависимости: `ILogger<DirService>`, `IOptionsMonitor<AppOptions>`.

### 4.3. `BackupService : IBackupOperations`

| Метод | Сигнатура | Возврат |
|-------|-----------|---------|
| Резервная копия | `CreateBackupFileAsync(string originalFilePath)` | `Task<BackupFileResult>` (поле `BackupPath`) |
| Восстановление | `RestoreBackupFileAsync(string backupFilePath, string originalFilePath)` | `Task<OperationResult>` |
| Удаление копии | `DeleteBackupFileAsync(string backupFilePath)` | `Task<OperationResult>` |

Поведение: если файл не существует — успех без копии (`BackupPath = null`); имя копии — `{имя}.{yyyyMMdd_HHmmss}.{Guid}.bak`; сохраняется в `BackupDir` (или рядом с оригиналом, если не задан).

**Откат при прерванной операции:** если критическая операция, ради которой создавался бэкап, была прервана/отменена (исключение), сервис:
- для `file_write`/`file_append`/`file_replace_lines`/`file_move`/`file_copy` — восстанавливает целевой файл из бэкапа (`RestoreBackupFileAsync`), затем удаляет бэкап (защита от частичной записи/копирования);
- для `file_delete` — просто удаляет бэкап (оригинал при сбое удаления не затронут).

Ошибки восстановления/удаления не скрывают исходную ошибку, а логируются отдельно (best-effort).

> **Operations-заметка (TTL/ротация):** бэкапы `.bak.<guid>` накапливаются без TTL/ротации — при интенсивных write-операциях `BackupDir` может переполниться. Очистка старых бэкапов — задача оператора: cron/systemd timer (например, `find $BACKUP_DIR -name '*.bak' -mtime +30 -delete`) или лимит по месту. Внутри процесса бэкап удаляется только при откате прерванной операции.

Зависимости: `ISandboxContext`, `ILogger<BackupService>`.

---

## 5. Слой Interfaces

| Интерфейс | Методы |
|-----------|--------|
| `ISandboxContext` | `string? SandboxDir { get; }`, `string? BackupDir { get; }` |
| `IFileOperations` | см. §4.1 |
| `IDirectoryOperations` | см. §4.2 |
| `IBackupOperations` | `Task<BackupFileResult> CreateBackupFileAsync(string originalFilePath)`, `Task<OperationResult> RestoreBackupFileAsync(string backupFilePath, string originalFilePath)`, `Task<OperationResult> DeleteBackupFileAsync(string backupFilePath)` |

---

## 6. Модели данных (`Models/`)

Все модели результата наследуют `OperationResult` (`IsSuccess`, `Message`, `Code`).

| Модель | Поля |
|--------|------|
| `AppOptions` | `MaxOneFileSizeInBytes`, `MaxQuantityFiles`, `MaxTotalSizeInBytes`, `MaxTreeEntries`, `SandboxDir`, `BackupDir`, `MaxSearchResults`, `ApiKey?`, `RateLimit?`; `SectionName = "FileAccess"` |
| `RateLimitOptions` | `Enabled`, `WindowInSeconds`, `PermitLimit`, `QueueLimit` (§2.2) |
| `FSEntrie` | `Type` ("Dir"/"File"/"RootDirectory"), `Name` (путь), `Size?` (байты) |
| `FSEntriesResult` | `Path?`, `Result?` (`List<FSEntrie>`) |
| `FileReadResult` | `Path?`, `SizeInBytes?`, `Content?` |
| `FileReadChunkResult` | `Path?`, `StartChar?`, `CharsReturned?`, `TotalCharsReadUpTo?`, `HasMore?`, `NextStartChar?`, `Content?` |
| `FileWriteResult` | `Path?`, `BytesWritten?` |
| `FileInfoResult` | `Name?`, `Extension?`, `FileType?`, `Length?`, `FileCreate?`, `FileLastUpdate?`, `FileLastAccessTime?`, `IsReadOnly?`, `IsLink?`, `LinkTarget?` |
| `FileHashResult` | `Path?`, `Algorithm?`, `Hash?`, `SizeInBytes?` |
| `FileImageResult` | `FilePath?`, `MimeType?`, `SizeInBytes?`, `DataUrl?` |
| `FileBinaryReadResult` | `FilePath?`, `MimeType?`, `SizeInBytes?`, `Base64Data?` |
| `MultiFileReadResult` | `Files?` (`List<FileReadResult>`) |
| `FileSearchResult` | `BasePath?`, `Mask?`, `SearchText?`, `TotalFound?`, `LimitReached?`, `Result?` (`List<FSEntrie>`) |
| `BackupFileResult` | `BackupPath?` (путь к созданной копии; `null` — копия не создавалась) |

---

## 7. Базовый слой (`Core/`)

### 7.1. `PathValidator`

Валидация путей в пределах песочницы.

| Метод | Сигнатура | Описание |
|-------|-----------|----------|
| Проверка внутри песочницы | `bool ChrootValidation(string sandboxRoot, string targetFullPath)` | Chroot-проверка + проверка symlink в каждом компоненте пути (fail-closed). |
| Главная точка входа | `OperationResult? ValidateAndGetAbsolutePath(string path, out string absolutePath, bool withCheckExist = true)` | Возвращает `null` при успехе и `absolutePath`; иначе `OperationResult` с ошибкой (`404` для несуществующего пути). |

Зависимости: `ILogger<PathValidator>`, `ISandboxContext`.

### 7.2. `Helpers`

| Метод | Сигнатура | Описание |
|-------|-----------|----------|
| MIME-тип | `static string GetContentTypeFromExtension(string extension)` | Возвращает MIME-тип по расширению; неизвестное — `application/octet-stream`. |
| Кодировка | `static Encoding? TryGetEncoding(string encodingName)` | Возвращает `Encoding` по имени или `null` (включая кодовые страницы windows-1251/cp866). |

### 7.3. `Consts`

| Константа | Значение | Назначение |
|-----------|----------|------------|
| `Megabyte` | `1024 * 1024` | Размер мегабайта. |
| `MaxChunkChars` | `1_000_000` | Макс. размер блока символов для поблочного чтения. |
| `DefaultMaxSearchResults` | `50` | Лимит результатов поиска по умолчанию. |
| `DefaultRateLimitWindowInSeconds` | `60` | Окно rate limiting по умолчанию (сек). |
| `DefaultRateLimitPermitLimit` | `100` | Лимит запросов на IP за окно по умолчанию. |
| `DefaultRateLimitQueueLimit` | `0` | Очередь rate limiting по умолчанию (выключена). |
| `MaxPendingDeleteConfirmations` | `10_000` | Макс. число ожидающих подтверждений `file_delete` в `MemoryCache` (защита памяти). |
| `DefaultShutdownTimeoutSeconds` | `30` | Таймаут graceful shutdown (`HostOptions.ShutdownTimeout`) — ожидание завершения in-flight запросов. |

### 7.4. `SandboxContext : ISandboxContext`

Возвращает `SandboxDir` и `BackupDir` из конфигурации (пустое значение → `null`). Зависимость: `IOptionsMonitor<AppOptions>`
(значения читаются через `CurrentValue` — поддерживает hot reload).

### 7.5. `ConfigValidator`

Статический валидатор конфигурации (`AppOptions`).

| Метод | Сигнатура | Описание |
|-------|-----------|----------|
| Валидация | `static List<string> Validate(AppOptions options)` | Возвращает список ошибок; пустой список — конфигурация корректна. |

### 7.6. `ConfigManager`

Статический помощник для расположения и генерации файла конфигурации.

| Метод | Сигнатура | Описание |
|-------|-----------|----------|
| Путь из аргументов | `static string? TryGetConfigPathFromArgs(string[] args)` | `--config <path>` / `--config=<path>`. |
| Флаг генерации | `static bool IsGenerateConfigRequested(string[] args)` | `--generate-config` / `--init`. |
| Флаг справки | `static bool IsHelpRequested(string[] args)` | `--help` / `-h`. |
| Разрешение пути | `static string ResolveConfigPath(string[] args)` | `--config` → рабочий каталог → каталог приложения. |
| Генерация | `static bool TryGenerateDefaultConfig(string configPath, out string? error)` | Создаёт дефолтный конфиг, если файла нет. |
| Справка | `static void PrintUsage()` | Вывод справки по CLI. |

### 7.7. `ApiKeyMiddleware`

ASP.NET Core middleware аутентификации по API-ключу (§2.5).

| Член | Значение |
|------|----------|
| Константа | `HeaderName = "X-Api-Key"` — имя HTTP-заголовка. |
| Конструктор | `ApiKeyMiddleware(RequestDelegate next, IOptionsMonitor<AppOptions> options, ILogger<ApiKeyMiddleware> logger)` |
| Обработка | `InvokeAsync(HttpContext)` — пропускает запрос при корректном ключе (или когда ключ не настроен); иначе `401` без тела. Ключ читается из `IOptionsMonitor<AppOptions>` (hot reload). |
| Сравнение | статический `IsValidKey` — `CryptographicOperations.FixedTimeEquals` по UTF-8 байтам (защита от timing-атак). |

Регистрация: `app.UseMiddleware<ApiKeyMiddleware>()` в `Program.cs` (до `UseRateLimiter` / `MapMcp`).

---

## 8. DI-регистрация (`Program.cs`)

| Lifetime | Регистрация |
|----------|-------------|
| Конфигурация | `AddJsonFile(configPath, optional: false, reloadOnChange: true)` + `AddEnvironmentVariables()` (ENV `FILEACCESS__*` имеет приоритет, §2.8) + `Configure<AppOptions>(FileAccess)` |
| Scoped | `ISandboxContext → SandboxContext`, `IBackupOperations → BackupService`, `PathValidator → PathValidator` |
| Transient | `IFileOperations → FileService`, `IDirectoryOperations → DirService` |
| — | `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<FileTools, DirTools, HealthTools>()` |
| — | `AddRateLimiter(...)`: partitioned fixed-window лимитер по клиентскому IP (§2.2), middleware `app.UseRateLimiter()` |
| — | `app.UseMiddleware<ApiKeyMiddleware>()` — проверка `X-Api-Key`, если задан `FileAccess:ApiKey` (§2.5); вызывается **до** `UseRateLimiter`. CORS (`AllowAnyOrigin`) **удалён** |
| — | `Configure<HostOptions>(ShutdownTimeout = Consts.DefaultShutdownTimeoutSeconds)` + подписки `app.Lifetime.ApplicationStarted/Stopping/Stopped` — graceful shutdown (§2.6) |
| — | `AddHealthChecks()` + `app.MapHealthChecks("/healthz")` / `MapHealthChecks("/readyz")` — публичные health-эндпоинты (§2.7) |
| — | `AddJsonConsole()` + `Configure<ConsoleLoggerOptions>` (`LogToStandardErrorThreshold = Debug`) + `Configure<JsonConsoleFormatterOptions>` (`UseUtcTimestamp`, `TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"`) — JSON-логирование, контейнер-ready |

Все потребители опций используют `IOptionsMonitor<AppOptions>` (регистрируется автоматически через `Configure<AppOptions>`)
— сервисы получают актуальные значения при перезагрузке конфигурации.

---

## 9. Соглашения об ошибках

- При любом исключении сервис возвращает типизированный результат (`IsSuccess = false`), а не выбрасывает исключение наружу.
- **Контролируемые ошибки** (`File not found`, `Directory not found`, `Security error`, `File too large`, валидация параметров) возвращают понятные клиенту сообщения с путём/причиной.
- **Внутренние исключения** (`IOException`, любые другие) **не раскрываются**: полная информация (включая `ex.Message` и стек) пишется в лог через `_logger.LogError(ex, ...)` с `CorrelationId`, а клиенту возвращается generic-текст + `Correlation ID: <id>` (например, `An internal error occurred. Correlation ID: 3f9a...`). Correlation id берётся из `Activity.Current.TraceId` (W3C) или генерируется `Guid` — по нему можно найти детали в логах.
- Пути в логах и сообщениях — абсолютные (с результата после нормализации).
- `file_delete` — единственный инструмент с двухэтапным подтверждением (токен, 5 минут, одноразовый).

## 10. Безопасность (кратко)

1. Chroot-проверка с нормализацией пути и защитой от подмены префикса.
2. Проверка symlink/reparse point в каждом компоненте пути через `ResolveLinkTarget(true)`.
3. Корень песочницы задаётся только в `config.json`.
4. Лимиты размера файлов и количества записей.
5. Rate limiting по клиентскому IP (fixed window, `429` при превышении; §2.2).
6. Резервное копирование перед перезаписью/удалением.
7. Подтверждение деструктивных операций одноразовым токеном (32 hex-символа; §3.2 `file_delete`).
8. Аутентификация по API-ключу `X-Api-Key` (`FileAccess:ApiKey`), fail-open при пустом ключе (§2.5). CORS-политика `AllowAnyOrigin` удалена.
9. **TLS завершается на ingress/обратном прокси** (nginx, Cloudflare, k8s Ingress/Gateway) — сервер слушает обычный HTTP (`ASPNETCORE_URLS`, по умолчанию `http://+:8080` в Docker). Шифрование между клиентом и прокси — обязанность proxy-слоя; не выставляйте порт сервера напрямую в интернет без прокси.
