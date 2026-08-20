namespace FileAccessMcpServer.Models
{
    /// <summary>
    /// Базовый класс результата операции.
    /// Используется как базовый для всех типизированных результатов.
    /// </summary>
    internal class OperationResult
    {
        /// <summary>
        /// Флаг успешности операции.
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// Сообщение о результате операции (ошибка или описание).
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// Код ответа операции
        /// </summary>
        public int? Code { get; set; }

        /// <summary>
        /// Создает результат с флагом успеха.
        /// </summary>
        public static OperationResult Success() => new OperationResult { IsSuccess = true, Code = 200 };

        /// <summary>
        /// Создает результат с флагом ошибки и сообщением.
        /// </summary>
        /// <param name="message">Сообщение об ошибке.</param>
        public static OperationResult Fail(string message) => new OperationResult { IsSuccess = false, Message = message, Code = 400 };
    }
}
