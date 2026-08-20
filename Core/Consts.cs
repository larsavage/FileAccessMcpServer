/// <summary>
/// Содержит константы проекта для использования в бизнес-логике.
/// </summary>
namespace FileAccessMcpServer.Core
{
    /// <summary>
    /// Константы проекта для работы с файлами и директориями.
    /// </summary>
    internal class Consts
    {
        /// <summary>
        /// Размер мегабайта в байтах (1024 * 1024).
        /// Используется при ограничении размера файлов и директорий.
        /// </summary>
        public const int Megabyte = 1024 * 1024;

        /// <summary>
        /// Максимальный размер блока в символах, читаемого одним вызовом чтения файла по частям.
        /// Ограничивает объём данных, передаваемых в контекст модели за один запрос.
        /// </summary>
        public const int MaxChunkChars = 1_000_000;

        /// <summary>
        /// Количество файлов, возвращаемых поиском по маске по умолчанию.
        /// Используется, если значение MaxSearchResults не задано в конфигурации.
        /// </summary>
        public const int DefaultMaxSearchResults = 50;

        /// <summary>
        /// Длительность окна rate limiting по умолчанию в секундах.
        /// Используется при генерации конфига по умолчанию.
        /// </summary>
        public const int DefaultRateLimitWindowInSeconds = 60;

        /// <summary>
        /// Количество разрешённых запросов на клиентский IP за окно по умолчанию.
        /// Используется при генерации конфига по умолчанию.
        /// </summary>
        public const int DefaultRateLimitPermitLimit = 100;

        /// <summary>
        /// Значение параметра <c>QueueLimit</c> по умолчанию — ожидание очереди выключено.
        /// </summary>
        public const int DefaultRateLimitQueueLimit = 0;

        /// <summary>
        /// Максимальное количество ожидающих подтверждений удаления файлов в MemoryCache.
        /// Ограничивает рост кэша под нагрузкой (защита от исчерпания памяти).
        /// </summary>
        public const int MaxPendingDeleteConfirmations = 10_000;

        /// <summary>
        /// Таймаут graceful shutdown в секундах.
        /// Столько хост ждёт завершения in-flight запросов после сигнала остановки (SIGINT/SIGTERM
        /// или HostApplicationLifetime.StopApplication), прежде чем принудительно завершиться.
        /// </summary>
        public const int DefaultShutdownTimeoutSeconds = 30;
    }
}
