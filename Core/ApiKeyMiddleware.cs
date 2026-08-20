using System.Security.Cryptography;
using System.Text;

using FileAccessMcpServer.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileAccessMcpServer.Core
{
    /// <summary>
    /// Проверяет наличие API-ключа в HTTP-заголовке запроса.
    /// Если ключ не задан в конфигурации (FileAccess:ApiKey) — проверка отключена,
    /// все запросы пропускаются (fail-open, обратная совместимость).
    /// Если ключ задан — каждый запрос обязан содержать корректный X-Api-Key.
    /// Сравнение выполняется с постоянным временем (защита от timing-атак).
    /// </summary>
    internal sealed class ApiKeyMiddleware
    {
        /// <summary>
        /// Имя HTTP-заголовка, в котором передаётся API-ключ.
        /// </summary>
        public const string HeaderName = "X-Api-Key";

        /// <summary>
        /// Публичные пути, не требующие API-ключа (k8s liveness/readiness, обратные прокси).
        /// </summary>
        private static readonly PathString[] PublicPaths = { "/healthz", "/readyz" };

        private readonly RequestDelegate _next;
        private readonly IOptionsMonitor<AppOptions> _options;
        private readonly ILogger<ApiKeyMiddleware> _logger;

        public ApiKeyMiddleware(RequestDelegate next, IOptionsMonitor<AppOptions> options, ILogger<ApiKeyMiddleware> logger)
        {
            _next = next;
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// Выполняет проверку ключа и либо пропускает запрос дальше по конвейеру, либо завершает его кодом 401.
        /// </summary>
        public async Task InvokeAsync(HttpContext context)
        {
            // Публичные пути доступны без ключа.
            if (IsPublicPath(context.Request.Path))
            {
                await _next(context);
                return;
            }

            string? configuredKey = _options.CurrentValue.ApiKey;

            // Ключ не задан — авторизация выключена.
            if (string.IsNullOrWhiteSpace(configuredKey))
            {
                await _next(context);
                return;
            }

            string? providedKey = context.Request.Headers[HeaderName].FirstOrDefault();
            if (IsValidKey(configuredKey.Trim(), providedKey?.Trim()))
            {
                await _next(context);
                return;
            }

            _logger.LogWarning("Request rejected: missing or invalid API key. Path='{Path}' RemoteIp='{RemoteIp}'", context.Request.Path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        /// <summary>
        /// Проверяет, является ли путь публичным (health-эндпоинты).
        /// </summary>
        private static bool IsPublicPath(PathString path)
        {
            foreach (var publicPath in PublicPaths)
            {
                if (path.Equals(publicPath, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Постоянное по времени сравнение ключей. Возвращает <c>false</c>, если ключи разной длины.
        /// </summary>
        private static bool IsValidKey(string configuredKey, string? providedKey)
        {
            if (string.IsNullOrEmpty(providedKey))
                return false;

            ReadOnlySpan<byte> expected = Encoding.UTF8.GetBytes(configuredKey);
            ReadOnlySpan<byte> actual = Encoding.UTF8.GetBytes(providedKey);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }
}