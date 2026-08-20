using FileAccessMcpServer.Models;
using FileAccessMcpServer.Services;
using FileAccessMcpServer.Tests.Infra;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace FileAccessMcpServer.Tests
{
    /// <summary>
    /// Регрессионные тесты флоу отката (RollbackAsync) в FileService:
    /// при прерывании критической операции бэкап восстанавливается/удаляется,
    /// «сиротских» .bak не остаётся, клиенту не утекает ex.Message.
    /// </summary>
    public class BackupRollbackTests
    {
        private const string FilePath = "file.txt";

        private static (FileService service, TempSandbox sandbox, RecordingBackupOperations recording) CreateHarness()
        {
            var sandbox = new TempSandbox();
            var options = new FakeOptionsMonitor(new AppOptions
            {
                SandboxDir = sandbox.SandboxDir,
                BackupDir = sandbox.BackupDir,
                MaxOneFileSizeInBytes = 4 * 1024 * 1024,
                MaxQuantityFiles = 5,
                MaxTotalSizeInBytes = 10 * 1024 * 1024,
                MaxTreeEntries = 2000,
                MaxSearchResults = 50
            });

            var context = new FakeSandboxContext { SandboxDir = sandbox.SandboxDir, BackupDir = sandbox.BackupDir };
            var realBackup = new BackupService(context, NullLogger<BackupService>.Instance);
            var recording = new RecordingBackupOperations(realBackup);
            var fileService = new FileService(NullLogger<FileService>.Instance, options, recording);

            return (fileService, sandbox, recording);
        }

        private static int CountBackups(string backupDir)
            => Directory.Exists(backupDir) ? Directory.GetFiles(backupDir, "*.bak").Length : 0;

        [Fact]
        public async Task WriteSuccess_KeepsBackup_NoRollbackCalls()
        {
            var (service, sandbox, recording) = CreateHarness();
            using (sandbox)
            {
                string file = sandbox.CreateFile(FilePath, "v1");

                var result = await service.FileWriteAsync(file, "v2");

                Assert.True(result.IsSuccess);
                Assert.Equal("v2", await File.ReadAllTextAsync(file));
                Assert.Equal(0, recording.RestoreCalls);
                Assert.Equal(0, recording.DeleteCalls);
                Assert.Equal(1, CountBackups(sandbox.BackupDir));
            }
        }

        [Fact]
        public async Task WriteFailure_RestoresAndDeletesBackup_ReturnsGenericError()
        {
            // Инжекция сбоя — sharing violation (только Windows); на других ОС пропускаем.
            if (!OperatingSystem.IsWindows())
                return;

            var (service, sandbox, recording) = CreateHarness();
            using (sandbox)
            {
                string file = sandbox.CreateFile(FilePath, "v1");

                // Файл открыт с запретом записи: бэкап (чтение) создастся, запись упадёт.
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var result = await service.FileWriteAsync(file, "v2");

                    Assert.False(result.IsSuccess);
                    // Generic-текст + correlation id, без деталей исключения.
                    Assert.StartsWith("An input/output error occurred.", result.Message);
                    Assert.Contains("Correlation ID:", result.Message);

                    // Откат: восстановление + удаление бэкапа, файл не тронут.
                    Assert.Equal(1, recording.RestoreCalls);
                    Assert.Equal(1, recording.DeleteCalls);
                    Assert.Equal(0, CountBackups(sandbox.BackupDir));
                    Assert.Equal("v1", await File.ReadAllTextAsync(file));
                }
            }
        }

        [Fact]
        public async Task DeleteFailure_DeletesBackupWithoutRestore()
        {
            if (!OperatingSystem.IsWindows())
                return;

            var (service, sandbox, recording) = CreateHarness();
            using (sandbox)
            {
                string file = sandbox.CreateFile(FilePath, "v1");

                // Файл открыт с запретом удаления: бэкап создастся, File.Delete упадёт.
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var result = await service.FileDeleteAsync(file);

                    Assert.False(result.IsSuccess);
                    Assert.StartsWith("An input/output error occurred.", result.Message);
                    Assert.Contains("Correlation ID:", result.Message);

                    // Delete не восстанавливает оригинал — только удаляет бэкап.
                    Assert.Equal(0, recording.RestoreCalls);
                    Assert.Equal(1, recording.DeleteCalls);
                    Assert.Equal(0, CountBackups(sandbox.BackupDir));
                    Assert.True(File.Exists(file));
                }
            }
        }
    }
}