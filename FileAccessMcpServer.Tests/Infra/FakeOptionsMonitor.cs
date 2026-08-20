using FileAccessMcpServer.Models;

using Microsoft.Extensions.Options;

namespace FileAccessMcpServer.Tests.Infra
{
    /// <summary>
    /// Простой IOptionsMonitor для тестов: возвращает фиксированные опции.
    /// </summary>
    internal sealed class FakeOptionsMonitor : IOptionsMonitor<AppOptions>
    {
        private readonly AppOptions _current;

        public FakeOptionsMonitor(AppOptions current) => _current = current;

        public AppOptions CurrentValue => _current;

        public AppOptions Get(string? name) => _current;

        public IDisposable? OnChange(Action<AppOptions, string?> listener) => null;
    }
}