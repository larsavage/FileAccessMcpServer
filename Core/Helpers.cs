using System.Diagnostics;
using System.Text;

using Microsoft.AspNetCore.StaticFiles;

/// <summary>
/// Предоставляет вспомогательные методы для работы с контекстом запроса, валидации путей и управления файлами.
/// </summary>
namespace FileAccessMcpServer.Core
{
    /// <summary>
    /// Вспомогательный класс с методами для определения MIME-типов файлов и кодировок.
    /// </summary>
    internal class Helpers
    {
        public static string GetContentTypeFromExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return "application/octet-stream";

            var provider = new FileExtensionContentTypeProvider();

            if (!extension.StartsWith(".")) extension = "." + extension;

            string? mimeType;
            if (provider.TryGetContentType(extension, out mimeType))
            {
                return mimeType;
            }

            return "application/octet-stream";
        }

        /// <summary>
        /// Возвращает кодировку по имени или null, если кодировка не поддерживается.
        /// Кодовые страницы (windows-1251, cp866 и др.) доступны благодаря регистрации
        /// CodePagesEncodingProvider в Program.cs.
        /// </summary>
        /// <param name="encodingName">Имя кодировки (например, "utf-8", "utf-16", "windows-1251").</param>
        /// <returns>Объект Encoding или null, если кодировка не найдена.</returns>
        public static Encoding? TryGetEncoding(string encodingName)
        {
            try
            {
                return Encoding.GetEncoding(encodingName);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// Возвращает correlation id текущего запроса: TraceId активного Activity (W3C TraceContext),
        /// либо новый случайный идентификатор, если Activity недоступен (например, вне HTTP-запроса).
        /// Используется для связи ответа клиенту с деталями исключения в логах.
        /// </summary>
        /// <returns>Строка correlation id (hex).</returns>
        public static string GetCorrelationId()
        {
            return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }
    }
}
