namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Содержит конфигурационные опции для работы с файлами и директориями.
    /// </summary>
    internal class AppOptions
    {
        /// <summary>
        /// Имя раздела в конфигурации (для использования с библиотеками типа Microsoft.Extensions.Configuration).
        /// </summary>
        public const string SectionName = "FileAccess";

        /// <summary>
        /// Максимальный размер одного файла в байтах.
        /// Используется при проверке размера файлов перед чтением/записью.
        /// </summary>
        public long MaxOneFileSizeInBytes { get; set; }

        /// <summary>
        /// Максимальное количество файлов, которое можно обработать одновременно.
        /// Используется при операции чтения нескольких файлов.
        /// </summary>
        public int MaxQuantityFiles { get; set; }

        /// <summary>
        /// Максимальный общий размер всех файлов в байтах.
        /// Используется для ограничения общего объема операций с файлами.
        /// </summary>
        public long MaxTotalSizeInBytes { get; set; }

        /// <summary>
        /// Максимальное количество записей (файлов и директорий) в результатах обхода дерева
        /// и списка файлов директории. Предотвращает чрезмерный объём ответа.
        /// </summary>
        public int MaxTreeEntries { get; set; }

        /// <summary>
        /// Корневой каталог песочницы. Задаётся серверной конфигурацией; без него все операции
        /// возвращают ошибку. Клиент не может изменить песочницу через HTTP-запрос.
        /// </summary>
        public string? SandboxDir { get; set; }

        /// <summary>
        /// Каталог для хранения резервных копий файлов перед перезаписью/удалением.
        /// Если не задан, резервная копия создаётся рядом с оригинальным файлом.
        /// </summary>
        public string? BackupDir { get; set; }

        /// <summary>
        /// Максимальное количество файлов, возвращаемых поиском по маске.
        /// По умолчанию 50 (см. Consts.DefaultMaxSearchResults); при превышении результат усекается.
        /// </summary>
        public int MaxSearchResults { get; set; }

        /// <summary>
        /// API-ключ для доступа к серверу (middleware проверяет заголовок X-Api-Key).
        /// Если пуст или не задан — авторизация отключена. Рекомендуется задавать секретную строку.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Настройки rate limiting (защита от floods/DDoS на уровне приложения).
        /// Если секция не задана или <see cref="RateLimitOptions.Enabled"/> равен <c>false</c>,
        /// ограничение запросов не применяется.
        /// </summary>
        public RateLimitOptions? RateLimit { get; set; }

        /// <summary>
        /// Режим HTTP-сессий Streamable HTTP: "statefulForInitializeClients" (по умолчанию),
        /// "stateful" или "stateless".
        /// Начиная с ревизии 2026-07-28 Streamable HTTP НЕ использует сессии, поэтому режим
        /// "stateful" несовместим с протоколом 2026-07-28. Значение по умолчанию
        /// "statefulForInitializeClients" даёт stateful-сессии клиентам с initialize-хендшейком
        /// (нужны для elicitation) и обслуживает клиентов 2026-07-28 без сессий.
        /// "stateless" — для горизонтального масштабирования без session affinity.
        /// </summary>
        public string HttpSessionMode { get; set; } = "statefulForInitializeClients";
    }

    /// <summary>
    /// Конфигурация ограничения частоты запросов (rate limit) в секции "FileAccess:RateLimit".
    /// Реализовано на встроенном middleware ASP.NET Core (fixed window, ключ — клиентский IP).
    /// </summary>
    internal class RateLimitOptions
    {
        /// <summary>
        /// Включает/выключает ограничение запросов. По умолчанию <c>false</c> (ограничение отключено,
        /// пока явно не включено в конфигурации).
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Длительность окна в секундах, в течение которого клиент может выполнить
        /// <see cref="PermitLimit"/> запросов. Должно быть больше 0, если лимит включён.
        /// </summary>
        public int WindowInSeconds { get; set; }

        /// <summary>
        /// Максимальное количество запросов от одного клиентского IP за окно
        /// <see cref="WindowInSeconds"/>. Должно быть больше 0, если лимит включён.
        /// </summary>
        public int PermitLimit { get; set; }

        /// <summary>
        /// Максимальный размер очереди ожидающих запросов при превышении лимита.
        /// <c>0</c> — запросы сверх лимита сразу отвечают кодом 429 (без ожидания).
        /// Отрицательным быть не может.
        /// </summary>
        public int QueueLimit { get; set; }
    }
}
