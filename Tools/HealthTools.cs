using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace FileAccessMcpServer.Tools
{
    /// <summary>
    /// Инструменты для проверки здоровья и доступности MCP сервера.
    /// </summary>
    internal class HealthTools
    {
        private readonly ILogger<HealthTools> _logger;

        /// <summary>
        /// Инициализирует экземпляр класса HealthTools.
        /// </summary>
        /// <param name="logger">Логгер для записи событий.</param>
        public HealthTools(ILogger<HealthTools> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Проверяет доступность MCP сервера.
        /// </summary>
        /// <returns>JSON-объект со статусом "Ok" и текущим временем.</returns>
        /// <example>
        /// {"Status": "Ok", "Timestamp": "2024-01-15T10:30:00Z"}
        /// </example>
        [McpServerTool(Name = "health")]
        [Description("Метод для проверки доступности MCP")]
        public string HealthCheck()
        {
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string json = JsonSerializer.Serialize(new
            {
                Status = "Ok",
                Timestamp = timestamp
            });

            _logger.LogInformation("Health check requested. Timestamp='{Timestamp}'", timestamp);
            return json;
        }
    }
}
