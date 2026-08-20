# FileAccessMcpServer - MCP Сервер для работы с файлами в песочнице

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![MCP Server](https://img.shields.io/badge/MCP%20Server-Active-green.svg)]()

## Описание проекта

**FileAccessMcpServer** — это сервер Model Context Protocol (MCP), который предоставляет безопасное взаимодействие с файлами в изолированной песочнице. Сервер позволяет читать, писать, управлять директориями и обрабатывать файлы через MCP инструменты с полной защитой от выхода за пределы разрешенного каталога.

## Ключевые функции

- **Безопасная работа с файлами** — все операции выполняются в пределах песочницы
- **Построение дерева директорий** — рекурсивное получение списка файлов и поддиректорий
- **Чтение файлов** — поддержка чтения текста с разными кодировками (utf-8, utf-16 и т.д.)
- **Запись файлов** — создание новых файлов или перезапись существующих (с автоматическим резервным копированием)
- **Добавление в конец файла** — append-запись без перезаписи существующего содержимого (с резервным копированием)
- **Замена строк** — замена указанного диапазона строк файла новыми строками (с резервным копированием); *MCP-инструмент `file_replace_lines` в текущей версии отключён (метод закомментирован), сервисный метод доступен только в коде*
- **Получение информации о файлах** — имя, размер, даты создания/изменения, тип контента, symlink
- **Чтение нескольких файлов одновременно** — batch операции с контролем общего размера
- **Управление директориями** — создание, получение списка файлов в директории
- **Управление файлами** — копирование, перемещение и удаление файлов внутри песочницы (с автоматическим резервным копированием и созданием недостающих директорий)
- **Подтверждение деструктивных операций** — удаление файла выполняется только после явного подтверждения через одноразовый токен
- **Поддержка изображений** — чтение и возврат в формате Base64 data URL (PNG, JPG, BMP, GIF, WebP)
- **Чтение бинарных файлов** — чтение произвольных бинарных файлов (PDF, ZIP, DLL и др.) с возвратом в формате Base64 и MIME-типом
- **Чтение файлов по частям** — paginated reading для больших файлов
- **Хеширование файлов** — вычисление контрольных сумм (MD5, CRC32, SHA256, SHA512)
- **Переименование файлов** — удобный алиас `file_rename` для `file_move`
- **Hot reload конфигурации** — изменения `config.json` применяются без перезапуска сервиса
- **Валидация конфигурации** — некорректные параметры завершают работу приложения
- **Генерация конфига** — аргумент `--generate-config` создаёт конфиг по умолчанию, если файла нет
- **Поиск файлов по маске** — рекурсивный поиск с поддержкой `*` и `?` (результат ограничен настраиваемым лимитом)
- **Поиск по содержимому** — поиск файлов, содержащих заданную подстроку, с опциональной маской имени (результат ограничен настраиваемым лимитом)
- **Rate limiting** — защита от DDoS/флуда на уровне приложения: фиксированное окно запросов на клиентский IP (код `429` при превышении)
- **Аутентификация по API-ключу** — middleware проверяет заголовок `X-Api-Key` (опция `ApiKey` в конфиге); при пустом ключе проверка отключена
- **Graceful shutdown** — плавная остановка через `IHostApplicationLifetime`: in-flight запросы завершаются в течение таймаута (30 с), фазы жизненного цикла логируются
- **Конфигурация через ENV** — параметры (`FILEACCESS__*`) можно переопределять переменными окружения: удобно для k8s ConfigMap/Secret и Docker (ENV приоритетнее `config.json`)
- **Health-эндпоинты** — `/healthz` и `/readyz` для k8s (liveness/readiness) и обратных прокси; публичные, работают без API-ключа
- **JSON-логирование** — одна JSON-запись на строку (`AddJsonConsole`), UTC ISO 8601; корректно обрабатывается контейнерными рантаймами и агрегаторами (Loki/ELK)
- **Docker** — готовый `Dockerfile` (мультистейдж, non-root, self-contained single-file)

## Архитектура

```
FileAccessMcpServer/
├── Core/                   # Базовый слой
│   ├── Consts.cs           # Константы проекта
│   ├── ApiKeyMiddleware.cs # Middleware проверки API-ключа (X-Api-Key)
│   ├── Helpers.cs          # Вспомогательные методы (MIME-типы)
│   ├── PathValidator.cs    # Валидация путей, chroot + symlink защита
│   ├── SandboxContext.cs   # Корень песочницы из конфигурации (SandboxDir/BackupDir)
│   ├── ConfigValidator.cs  # Валидация конфигурации при старте и перезагрузке
│   └── ConfigManager.cs    # Расположение конфига, генерация конфига по умолчанию
├── Models/                 # Модели данных
│   ├── AppOptions.cs       # Конфигурационные опции
│   ├── OperationResult.cs  # Базовый класс результата
│   ├── FSEntrie.cs         # Запись о файле/директории
│   ├── FSEntriesResult.cs
│   ├── FileReadResult.cs
│   ├── FileReadChunkResult.cs
│   ├── FileWriteResult.cs
│   ├── FileInfoResult.cs
│   ├── FileImageResult.cs
│   ├── FileBinaryReadResult.cs
│   ├── FileHashResult.cs   # Результат вычисления хеша файла
│   └── MultiFileReadResult.cs
├── Interfaces/             # Контракты для реализации
│   ├── ISandboxContext.cs
│   ├── IDirectoryOperations.cs
│   ├── IFileOperations.cs
│   └── IBackupOperations.cs
├── Services/               # Реализация бизнес-логики
│   ├── DirService.cs       # Операции с директориями
│   ├── FileService.cs      # Операции с файлами
│   └── BackupService.cs    # Резервное копирование перед записью
├── Tools/                  # MCP инструменты
│   ├── DirTools.cs         # Инструменты работы с директориями (6 инструментов)
│   ├── FileTools.cs        # Инструменты работы с файлами (13 инструментов)
│   └── HealthTools.cs      # Проверка здоровья сервера (1 инструмент)
├── config.example.json     # Пример конфигурации сервера
├── Program.cs              # Точка входа, CLI-аргументы, DI, настройка MCP
├── FileAccessMcpServer.csproj
└── FileAccessMcpServer.slnx
```

## Инструменты MCP Server

### Directory Tools

| Название инструмента | Описание | Параметры |
|---------------------|----------|-----------|
| `directory_tree` | Построение дерева директорий без файлов | basePath, nesting (default: 5, max: 10) |
| `directory_tree_with_files` | Построение дерева с файлами и их метаданными | basePath, nesting (default: 5, max: 10) |
| `directory_list_files` | Получение списка файлов в директории | basePath (default: "") |
| `create_directory` | Создание новой директории | path (default: "") |
| `search_files` | Рекурсивный поиск файлов по маске (`*` и `?`); маски `*.*`, `*`, `**` заблокированы; результат ограничен (default: 50, `MaxSearchResults`) | basePath, mask |
| `search_files_content` | Рекурсивный поиск файлов по подстроке в содержимом (с учётом регистра); опциональная маска имени; маски `*.*`, `*`, `**` заблокированы; результат ограничен (default: 50, `MaxSearchResults`); файлы > `MaxOneFileSizeInBytes` пропускаются | basePath, searchText, mask (default: ""), encoding (default: "utf-8") |

### File Tools

| Название инструмента | Описание | Параметры |
|---------------------|----------|-----------|
| `file_read` | Чтение содержимого файла | filePath, encoding (default: "utf-8") |
| `file_write` | Создание или запись в файл | filePath, content, encoding (default: "utf-8") |
| `file_append` | Добавление текста в конец файла (создаёт файл, если его нет) | filePath, content, encoding (default: "utf-8") |
| `file_replace_lines` *(отключён)* | Замена диапазона строк новыми строками (нумерация с 1) — инструмент закомментирован и НЕ регистрируется | — |
| `files_read_multiple` | Считывание нескольких файлов одновременно | filesPath[], encoding (default: "utf-8") |
| `file_get_info` | Получение информации о файле | filePath |
| `file_hash` | Вычисление хеша файла (md5, crc32, sha256, sha512) | filePath, algorithm (default: "sha256") |
| `file_read_chunks` | Чтение файла по частям (блоками символов, для файлов любой длины строки) | filePath, startChar (>= 0), charsCount (1–1000000), encoding |
| `file_image_read` | Чтение изображения и возвращение Base64 | fileImagePath |
| `file_binary_read` | Чтение бинарного файла и возвращение Base64 с MIME-типом | filePath |
| `file_copy` | Копирование файла внутри песочницы | originFilePath, newFilePath, isRewrite (default: false) |
| `file_move` | Перемещение файла внутри песочницы | originFilePath, newFilePath, isRewrite (default: false) |
| `file_rename` | Переименование файла (алиас file_move) | originFilePath, newFilePath, isRewrite (default: false) |
| `file_delete` | Удаление файла (двухэтапное подтверждение токеном) | filePath, confirmToken (default: "") |

### Health Tools

| Название инструмента | Описание | Параметры |
|---------------------|----------|-----------|
| `health` | Проверка доступности сервера | - |

## Безопасность

Проект реализует несколько уровней защиты:

1. **Chroot защита** — все операции выполняются в пределах корня песочницы
2. **Серверная конфигурация песочницы** — корневой каталог задаётся только в `config.json` (`SandboxDir`); клиент не может изменить границы песочницы через запрос
3. **Проверка на символы ссылок (symlinks)** — предотвращение выхода через symlink (проверка каждого компонента пути)
4. **Ограничение размера файлов** — проверка размера каждого файла перед чтением/записью
5. **Валидация путей** — каждый путь проверяется на соответствие правилам песочницы; для операций копирования/перемещения проверяются оба пути (исходный и назначения)
6. **Автоматическое резервное копирование** — перед перезаписью или удалением файла создаётся `.bak` копия (каталог бэкапов задаётся в конфигурации `BackupDir`)
7. **Двухэтапное подтверждение удаления** — деструктивная операция выполняется только после подтверждения одноразовым токеном (32 hex-символа, срок действия 5 минут)
8. **Rate limiting** — ограничение частоты запросов по клиентскому IP (fixed window); при превышении лимита возвращается `429`
9. **Аутентификация по API-ключу** — middleware проверяет заголовок `X-Api-Key` (`ApiKey` в конфиге); CORS-политика `AllowAnyOrigin` удалена

## Использование

### Запуск сервера

1. Скомпилируйте проект:
```bash
dotnet build
```

2. Запустите сервер MCP:
```bash
dotnet run
```

3. Подключитесь к серверу через MCP клиента. Корень песочницы задаётся в конфигурации (`SandboxDir`).

### Аргументы командной строки

| Аргумент | Описание |
|----------|----------|
| `--config <path>` | Путь к файлу конфигурации (по умолчанию `config.json` в рабочем каталоге; если его нет — ищется рядом с исполняемым файлом). |
| `--generate-config` | Сгенерировать конфиг по умолчанию, **если файла нет**, и завершить работу. Существующий файл не перезаписывается. |
| `--init` | Алиас для `--generate-config`. |
| `--help`, `-h` | Показать справку и завершить работу. |

Пример генерации конфига по умолчанию:

```bash
dotnet run -- --generate-config
```

Пример запуска с нестандартным расположением конфига:

```bash
dotnet run -- --config /etc/fileaccess/config.json
```

### Примеры вызовов инструментов

#### Построение дерева директорий

```json
{
  "name": "directory_tree",
  "arguments": {
    "basePath": "/sandbox/src/projects",
    "nesting": 2
  }
}
```

#### Чтение файла

```json
{
  "name": "file_read",
  "arguments": {
    "filePath": "/sandbox/config.json",
    "encoding": "utf-8"
  }
}
```

#### Создание файла

```json
{
  "name": "file_write",
  "arguments": {
    "filePath": "/sandbox/newfile.txt",
    "content": "Hello, World!",
    "encoding": "utf-8"
  }
}
```

#### Копирование файла

```json
{
  "name": "file_copy",
  "arguments": {
    "originFilePath": "/sandbox/source.txt",
    "newFilePath": "/sandbox/backup/source-copy.txt",
    "isRewrite": false
  }
}
```

#### Удаление файла (двухэтапное подтверждение)

Первый вызов запрашивает токен подтверждения (код ответа `202`):

```json
{
  "name": "file_delete",
  "arguments": {
    "filePath": "/sandbox/old-file.txt",
    "confirmToken": ""
  }
}
```

Ответ содержит одноразовый токен из **32 hex-символов** (например, `3F9A7C2B...`). Второй вызов с этим токеном выполняет удаление:

```json
{
  "name": "file_delete",
  "arguments": {
    "filePath": "/sandbox/old-file.txt",
    "confirmToken": "3F9A7C2B1E8D4A56D0F2C48E6B9A1C7D3"
  }
}
```

Токен действителен 5 минут и используется только один раз.

## Конфигурация

Сервер читает конфигурацию из `config.json`:

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

Параметры:
- `SandboxDir` — корневой каталог песочницы (обязательный). Задаётся только на сервере; границы песочницы не могут быть изменены клиентом
- `BackupDir` — каталог резервных копий. Если не задан, `.bak` создаётся рядом с оригиналом

> **Ops: накопление бэкапов.** Успешные операции оставляют `.bak.<guid>` без TTL/ротации (внутри процесса бэкап удаляется только при откате прерванной операции). При интенсивных write-операциях `BackupDir` может переполниться — очистку старых бэкапов выполняйте на уровне ops: cron/systemd timer, например `find $BACKUP_DIR -name '*.bak' -mtime +30 -delete`.

- `MaxOneFileSizeInBytes` — макс. размер одного файла (4 МБ)
- `MaxQuantityFiles` — макс. количество файлов для batch операции (5)
- `MaxTotalSizeInBytes` — общий лимит для batch чтения (10 МБ)
- `MaxTreeEntries` — макс. количество записей в результате обхода дерева/директории (2000); при превышении ответ усекается
- `MaxSearchResults` — макс. количество файлов, возвращаемых поиском по маске и по содержимому (50); при превышении результат усекается (флаг `LimitReached`)
- `ApiKey` — API-ключ доступа (заголовок `X-Api-Key`). Пусто/не задан — аутентификация отключена (fail-open); задан — каждый запрос без корректного ключа получает `401`
- `RateLimit` — настройки ограничения частоты запросов (защита от DDoS):
  - `Enabled` — включает/выключает ограничение (`false` — выключено)
  - `WindowInSeconds` — длительность окна в секундах (60)
  - `PermitLimit` — макс. количество запросов от одного IP за окно (100)
  - `QueueLimit` — размер очереди ожидания; `0` — запросы сверх лимита сразу получают `429 Too Many Requests`

Если секция `RateLimit` не задана или `Enabled = false`, ограничение не применяется.

### Rate limiting и DDoS

Лимит реализован на встроенном middleware ASP.NET Core (`AddRateLimiter`/`UseRateLimiter`): **fixed window по клиентскому IP**.
При превышении лимита запрос получает код `429 Too Many Requests`, событие логируется с уровнем `Warning`.
Параметры читаются через `IOptionsMonitor<AppOptions>` на каждом запросе, поэтому **изменения применяются без перезапуска**
(hot reload).

Важно: rate limiting защищает от application-level флуда (запросы, нагружающие CPU/диск). Для защиты от объёмных
сетевых DDoS-атак требуется обратный прокси/CDN (nginx, Cloudflare) и ограничения на сетевом уровне.

### Аутентификация по API-ключу и CORS

Доступ к серверу защищён middleware `ApiKeyMiddleware` (`Core/ApiKeyMiddleware.cs`):

- Ключ задаётся опцией `ApiKey`; запросы передают его в заголовке `X-Api-Key`.
- `ApiKey` пуст/не задан → проверка отключена (fail-open, обратная совместимость); при старте логируется `Warning: ApiKey is empty — running without authentication`. Задан → запрос без корректного `X-Api-Key` получает `401 Unauthorized`.
- Сравнение ключей — с постоянным временем (`CryptographicOperations.FixedTimeEquals`), защита от timing-атак.
- Ключ читается из `IOptionsMonitor<AppOptions>` на каждом запросе — смена ключа в `config.json` применяется без перезапуска (hot reload).

### Terminate TLS on ingress (reverse proxy)

Сервер слушает **обычный HTTP** (`ASPNETCORE_URLS=http://+:8080` в контейнере) — **TLS завершается на ingress/обратном прокси** (nginx, Traefik, Cloudflare, k8s Ingress/Gateway). Не публикуйте порт сервера напрямую в интернет: без прокси не будет шифрования и (при включённом `ApiKey`) внешний трафик пойдёт открытым текстом. Между клиентом и прокси используется HTTPS, между прокси и сервером — HTTP (доверенная сеть/внутренний Docker-сеть).

**nginx:**

```nginx
# HTTP → HTTPS
server {
    listen 80;
    server_name mcp.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name mcp.example.com;

    ssl_certificate     /etc/letsencrypt/live/mcp.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/mcp.example.com/privkey.pem;
    ssl_protocols       TLSv1.2 TLSv1.3;

    location / {
        proxy_pass         http://fileaccess-mcp:8080;
        proxy_http_version 1.1;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
}
```

**Traefik** (docker-compose labels, автоматический сертификат через ACME):

```yaml
services:
  fileaccess-mcp:
    image: fileaccess-mcp:latest
    ports: ["8080:8080"]
    labels:
      - "traefik.enable=true"
      # HTTPS (websecure) с certresolver
      - "traefik.http.routers.mcp.rule=Host(`mcp.example.com`)"
      - "traefik.http.routers.mcp.entrypoints=websecure"
      - "traefik.http.routers.mcp.tls=true"
      - "traefik.http.routers.mcp.tls.certresolver=letsencrypt"
      - "traefik.http.services.mcp.loadbalancer.server.port=8080"
      # HTTP → HTTPS redirect
      - "traefik.http.routers.mcp-http.rule=Host(`mcp.example.com`)"
      - "traefik.http.routers.mcp-http.entrypoints=web"
      - "traefik.http.routers.mcp-http.middlewares=redirect-to-https"
      - "traefik.http.middlewares.redirect-to-https.redirectscheme.scheme=https"
```

> Примечание: для корректного определения клиентского IP за прокси (rate limiting по IP, логи) в приложении следует включить `app.UseForwardedHeaders()` (доверяйте заголовки `X-Forwarded-For`/`X-Forwarded-Proto` только от прокси). Прокси обязан выставлять эти заголовки (в примерах выше — `proxy_set_header X-Forwarded-*` для nginx; Traefik добавляет их автоматически).

CORS-политика **удалена** (ранее `AllowAnyOrigin`): кросс-доменные браузерные запросы не разрешены — клиенты MCP работают напрямую по HTTP без CORS.

### Конфигурация через переменные окружения (ENV)

Любой параметр секции `FileAccess` можно переопределить переменной окружения: **двойное подчёркивание `__` = разделитель `:`**, регистр не важен.

| Параметр `config.json` | Переменная окружения |
|------------------------|----------------------|
| `FileAccess:SandboxDir` | `FILEACCESS__SANDBOXDIR` |
| `FileAccess:BackupDir` | `FILEACCESS__BACKUPDIR` |
| `FileAccess:ApiKey` | `FILEACCESS__APIKEY` |
| `FileAccess:RateLimit:Enabled` | `FILEACCESS__RATELIMIT__ENABLED` |
| `FileAccess:RateLimit:PermitLimit` | `FILEACCESS__RATELIMIT__PERMITLIMIT` |

- ENV имеет **приоритет** над `config.json` (удобно для k8s ConfigMap/Secret и Docker).
- Переменные читаются **при старте** (без hot reload); `config.json` должен существовать как источник привязки секции.

### Hot reload (перезагрузка без перезапуска)

Изменения `config.json` применяются **без перезапуска сервиса** (`reloadOnChange: true` + `IOptionsMonitor<AppOptions>`).
Достаточно сохранить файл — новые значения подхватываются на следующих запросах.

### Валидация конфигурации

При старте и при каждой перезагрузке конфигурация проходит валидацию. Некорректные параметры завершают работу приложения с ненулевым кодом (для старта — `Environment.Exit(1)`, хост ещё не запущен) или запускают плавную остановку (для перезагрузки — `IHostApplicationLifetime.StopApplication()`, in-flight запросы успевают завершиться, код возврата `1`). Правила:

- `SandboxDir` — обязателен, должен быть абсолютным путём и указывать на директорию (не файл)
- `BackupDir` — если задан, должен быть абсолютным путём и указывать на директорию (не файл)
- `MaxOneFileSizeInBytes`, `MaxQuantityFiles`, `MaxTotalSizeInBytes`, `MaxTreeEntries`, `MaxSearchResults` — должны быть положительными числами
- `MaxTotalSizeInBytes` не может быть меньше `MaxOneFileSizeInBytes`
- Если `RateLimit.Enabled = true`: `WindowInSeconds > 0`, `PermitLimit > 0`, `QueueLimit >= 0`

Каталоги `SandboxDir`/`BackupDir` создаются автоматически при старте, если отсутствуют.

### Health-эндпоинты (/healthz, /readyz)

- `GET /healthz` — liveness, `GET /readyz` — readiness; отвечают `200 OK` (`Healthy`).
- Публичные: **не требуют API-ключа** (исключены в `ApiKeyMiddleware`) — рассчитаны на k8s-пробы и обратные прокси.
- Не используют файловые операции; минимальная нагрузка.

## Запуск в Docker

Мультистейдж `Dockerfile` публикует приложение как self-contained single-file и запускает его от пользователя `app` (UID 1654) на порту `8080`.

```bash
docker build -t fileaccess-mcp:latest .
```

**Важно:** `config.json` в образ **не включается** (исключён в `.dockerignore` — он содержит локальные пути/секреты). Смонтируйте свой конфиг или задайте параметры через ENV:

```bash
docker run -d --name fileaccess-mcp \
  -p 8080:8080 \
  -v "$(pwd)/config.json:/app/config.json:ro" \
  -v "$(pwd)/sandbox:/srv/sandbox:rw" \
  -e FILEACCESS__SANDBOXDIR=/srv/sandbox \
  -e FILEACCESS__APIKEY=my-secret-key \
  fileaccess-mcp:latest
```

- Каталоги `SandboxDir`/`BackupDir` должны быть доступны на запись пользователю `app` (UID 1654): смонтируйте тома и при необходимости `chown -R 1654:1654`.
- `HEALTHCHECK` в образе проверяет `GET /healthz` (редактируйте под свои k8s-пробы: `livenessProbe.path=/healthz`, `readinessProbe.path=/readyz`).
- **Логи — JSON** (одна запись на строку, UTC): `docker logs`/k8s вывод готов к агрегации (Loki/ELK) без парсинга текста.
- **TLS завершается на ingress/обратном прокси** — контейнер слушает HTTP (`ASPNETCORE_URLS=http://+:8080`); не публикуйте порт напрямую в интернет без прокси (см. «Terminate TLS on ingress» выше).

## Документация разработчика

Для расширения функционала проекта изучите:

- **developer-sdk.md** — полное руководство по API и расширению функционала
- **api-reference.md** — подробный справочник по всем инструментам, сервисам, моделям и конфигурации
- Архитектурные паттерны в исходном коде с XML документацией

## Тестирование

Проект `FileAccessMcpServer.Tests` покрывает **security-критичные пути** регрессионными тестами (xUnit):

- `PathValidatorTests` — chroot-проверка (`..`, sibling-prefix атаки, путь вне песочницы) и symlink-защита;
- `ApiKeyMiddlewareTests` — fail-open при пустом ключе, постоянное время сравнения (`FixedTimeEquals`), публичные `/healthz`/`/readyz`;
- `FileToolsDeleteTokenTests` — двухэтапное подтверждение удаления: 32-hex токен, одноразовое потребление, lock-сериализация (один победитель из параллельных вызовов);
- `BackupRollbackTests` + `BackupServiceTests` — флоу отката: при прерванной операции бэкап восстанавливается и удаляется, «сиротских» `.bak` не остаётся.

```bash
dotnet test
```

## Технологии

- [.NET 8.0](https://dotnet.microsoft.com/)
- [Model Context Protocol (MCP)](https://modelcontextprotocol.io/)
- [ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)
- [Microsoft.Extensions.Logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging)
- [System.Text.Json](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/)

## Ссылки

- [Model Context Protocol](https://modelcontextprotocol.io/) — официальный сайт MCP
- [.NET Documentation](https://learn.microsoft.com/en-us/dotnet/) — документация .NET

## Исходный код

Все исходные файлы находятся в каталоге проекта. Для анализа архитектуры и расширения функционала используйте `developer-sdk.md`.

---

**Версия:** 0.1.0-beta
**Последнее обновление:** 2026
