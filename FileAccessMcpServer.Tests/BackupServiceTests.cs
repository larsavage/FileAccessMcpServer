using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Services;
using FileAccessMcpServer.Tests.Infra;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace FileAccessMcpServer.Tests
{
    /// <summary>
    /// Регрессионные тесты примитивов BackupService (create/restore/delete).
    /// </summary>
    public class BackupServiceTests
    {
        private static (BackupService service, TempSandbox sandbox) CreateHarness()
        {
            var sandbox = new TempSandbox();
            var context = new FakeSandboxContext { SandboxDir = sandbox.SandboxDir, BackupDir = sandbox.BackupDir };
            var service = new BackupService(context, NullLogger<BackupService>.Instance);
            return (service, sandbox);
        }

        [Fact]
        public async Task CreateBackup_ReturnsPath_WithBakSuffix_AndContent()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string file = sandbox.CreateFile("f.txt", "content");

                var result = await service.CreateBackupFileAsync(file);

                Assert.True(result.IsSuccess);
                Assert.NotNull(result.BackupPath);
                Assert.EndsWith(".bak", result.BackupPath);
                Assert.True(File.Exists(result.BackupPath));
                Assert.Equal("content", await File.ReadAllTextAsync(result.BackupPath!));
            }
        }

        [Fact]
        public async Task CreateBackup_MissingSource_ReturnsSuccessWithNullPath()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                var result = await service.CreateBackupFileAsync(Path.Combine(sandbox.SandboxDir, "nope.txt"));

                Assert.True(result.IsSuccess);
                Assert.Null(result.BackupPath);
            }
        }

        [Fact]
        public async Task RestoreBackup_RestoresOriginalContent()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string file = sandbox.CreateFile("f.txt", "v1");
                var created = await service.CreateBackupFileAsync(file);
                await File.WriteAllTextAsync(file, "corrupted");

                var restored = await service.RestoreBackupFileAsync(created.BackupPath!, file);

                Assert.True(restored.IsSuccess);
                Assert.Equal("v1", await File.ReadAllTextAsync(file));
            }
        }

        [Fact]
        public async Task DeleteBackup_RemovesFile()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string file = sandbox.CreateFile("f.txt", "content");
                var created = await service.CreateBackupFileAsync(file);
                Assert.True(File.Exists(created.BackupPath!));

                var deleted = await service.DeleteBackupFileAsync(created.BackupPath!);

                Assert.True(deleted.IsSuccess);
                Assert.False(File.Exists(created.BackupPath!));
            }
        }

        [Fact]
        public async Task DeleteBackup_MissingFile_ReturnsSuccess()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                var deleted = await service.DeleteBackupFileAsync(Path.Combine(sandbox.BackupDir, "ghost.bak"));

                Assert.True(deleted.IsSuccess);
            }
        }
    }
}