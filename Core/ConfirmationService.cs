using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FileAccessMcpServer.Core
{
    /// <summary>
    /// Запрашивает у пользователя подтверждение деструктивной операции через MCP elicitation
    /// (<c>elicitation/create</c>). Работает как по stdio, так и по Streamable HTTP
    /// (в HTTP требуется stateful-сессия).
    /// </summary>
    /// <remarks>
    /// Если клиент не объявил capability <c>elicitation</c> или запрос завершился ошибкой,
    /// возвращается <see cref="ConfirmationOutcome.Unsupported"/>: вызывающий инструмент
    /// должен применить резервный сценарий (токен подтверждения).
    /// </remarks>
    internal static class ConfirmationService
    {
        /// <summary>Имя поля формы, в котором клиент возвращает решение.</summary>
        public const string ConfirmFieldName = "confirm";

        /// <summary>Результат запроса подтверждения.</summary>
        public enum ConfirmationOutcome
        {
            /// <summary>Пользователь подтвердил операцию.</summary>
            Confirmed,

            /// <summary>Пользователь отклонил операцию.</summary>
            Declined,

            /// <summary>Клиент не поддерживает elicitation (нужен fallback).</summary>
            Unsupported
        }

        /// <summary>
        /// Проверяет, поддерживает ли подключённый клиент elicitation.
        /// </summary>
        public static bool IsSupported(McpServer? server)
        {
            return server?.ClientCapabilities?.Elicitation is not null;
        }

        /// <summary>
        /// Запрашивает подтверждение у пользователя.
        /// </summary>
        /// <param name="server">Сервер MCP текущего запроса.</param>
        /// <param name="logger">Логгер.</param>
        /// <param name="title">Короткий заголовок (например, «Удаление файла»).</param>
        /// <param name="message">Подробное описание операции.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Решение пользователя либо признак неподдерживаемого elicitation.</returns>
        public static async Task<ConfirmationOutcome> RequestConfirmationAsync(
            McpServer? server,
            ILogger logger,
            string title,
            string message,
            CancellationToken cancellationToken = default)
        {
            if (!IsSupported(server))
            {
                logger.LogDebug("Elicitation is not supported by the client. Falling back to token confirmation.");
                return ConfirmationOutcome.Unsupported;
            }

            var request = new ElicitRequestParams
            {
                Message = message,
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        [ConfirmFieldName] = new ElicitRequestParams.BooleanSchema
                        {
                            Title = title,
                            Description = message,
                            Default = false
                        }
                    },
                    Required = new List<string> { ConfirmFieldName }
                }
            };

            try
            {
                ElicitResult result = await server!.ElicitAsync(request, cancellationToken);

                if (!result.IsAccepted)
                {
                    logger.LogWarning("Destructive operation declined by user. Action='{Action}', Action='{ElicitAction}'", title, result.Action);
                    return ConfirmationOutcome.Declined;
                }

                if (result.Content is not null &&
                    result.Content.TryGetValue(ConfirmFieldName, out JsonElement element) &&
                    element.ValueKind is JsonValueKind.True)
                {
                    logger.LogInformation("Destructive operation confirmed by user via elicitation. Action='{Action}'", title);
                    return ConfirmationOutcome.Confirmed;
                }

                logger.LogWarning("Elicitation completed without explicit confirmation. Action='{Action}'", title);
                return ConfirmationOutcome.Declined;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Клиент объявил capability, но запрос не удался (нет stateful-сессии, ошибка UI и т.п.).
                logger.LogWarning(ex, "Elicitation request failed. Falling back to token confirmation. Action='{Action}'", title);
                return ConfirmationOutcome.Unsupported;
            }
        }
    }
}
