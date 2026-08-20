using FileAccessMcpServer.Interfaces;

namespace FileAccessMcpServer.Tests.Infra
{
    /// <summary>
    /// Тестовая реализация ISandboxContext с настраиваемыми путями.
    /// </summary>
    internal sealed class FakeSandboxContext : ISandboxContext
    {
        public string? SandboxDir { get; set; }

        public string? BackupDir { get; set; }
    }
}