namespace FileAccessMcpServer.Tests.Infra
{
    /// <summary>
    /// Создаёт временную песочницу (sandbox + backup) в системной temp-директории.
    /// Уникальный каталог на каждый экземпляр — тесты не пересекаются по путям.
    /// </summary>
    internal sealed class TempSandbox : IDisposable
    {
        public string Root { get; }

        public string SandboxDir { get; }

        public string BackupDir { get; }

        public TempSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "FileAccessMcp.Tests." + Guid.NewGuid().ToString("N"));
            SandboxDir = Path.Combine(Root, "sandbox");
            BackupDir = Path.Combine(Root, "backup");
            Directory.CreateDirectory(SandboxDir);
            Directory.CreateDirectory(BackupDir);
        }

        /// <summary>
        /// Создаёт файл внутри песочницы (с созданием недостающих поддиректорий).
        /// </summary>
        public string CreateFile(string relativePath, string content)
        {
            string path = Path.Combine(SandboxDir, relativePath);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}