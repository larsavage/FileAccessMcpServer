using FileAccessMcpServer.Core;
using FileAccessMcpServer.Models;
using FileAccessMcpServer.Services;
using FileAccessMcpServer.Tests.Infra;
using FileAccessMcpServer.Tools;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace FileAccessMcpServer.Tests
{
    /// <summary>
    /// Регрессионные тесты инструмента files_read_multiple:
    /// каждый путь проходит chroot-валидацию, лимиты количества и суммарного размера соблюдаются.
    /// </summary>
    public class FileToolsReadMultipleTests
    {
        private static (FileTools tools, TempSandbox sandbox) CreateHarness(int maxQuantityFiles = 5, long maxTotalSizeInBytes = 10 * 1024 * 1024)
        {
            var sandbox = new TempSandbox();
            var options = new AppOptions
            {
                SandboxDir = sandbox.SandboxDir,
                BackupDir = sandbox.BackupDir,
                MaxOneFileSizeInBytes = 4 * 1024 * 1024,
                MaxQuantityFiles = maxQuantityFiles,
                MaxTotalSizeInBytes = maxTotalSizeInBytes,
                MaxTreeEntries = 2000,
                MaxSearchResults = 50
            };
            var context = new FakeSandboxContext { SandboxDir = sandbox.SandboxDir, BackupDir = sandbox.BackupDir };
            var backup = new BackupService(context, NullLogger<BackupService>.Instance);
            var fileService = new FileService(NullLogger<FileService>.Instance, new FakeOptionsMonitor(options), backup);
            var pathValidator = new PathValidator(NullLogger<PathValidator>.Instance, context);
            var tools = new FileTools(NullLogger<FileTools>.Instance, fileService, new FakeOptionsMonitor(options), pathValidator);
            return (tools, sandbox);
        }

        [Fact]
        public async Task OutsideSandboxPath_IsRejectedPerFile_AndContentIsNotLeaked()
        {
            var (tools, sandbox) = CreateHarness();
            using (sandbox)
            {
                string outsideFile = Path.Combine(sandbox.Root, "outside-secret.txt");
                File.WriteAllText(outsideFile, "TOP-SECRET-CONTENT");
                sandbox.CreateFile("inside.txt", "inside-ok");

                var result = await tools.ReadMultipleFilesAsync(new[] { outsideFile, "inside.txt" });

                Assert.NotNull(result.Files);
                Assert.Equal(2, result.Files!.Count);

                Assert.False(result.Files[0].IsSuccess);
                Assert.Contains("Security Error", result.Files[0].Message!);
                Assert.Null(result.Files[0].Content);
                Assert.DoesNotContain("TOP-SECRET-CONTENT", result.Files[0].Message!);

                Assert.True(result.Files[1].IsSuccess);
                Assert.Equal("inside-ok", result.Files[1].Content);
            }
        }

        [Fact]
        public async Task TooManyFiles_ReturnsFailure()
        {
            var (tools, sandbox) = CreateHarness(maxQuantityFiles: 2);
            using (sandbox)
            {
                sandbox.CreateFile("a.txt", "a");
                sandbox.CreateFile("b.txt", "b");
                sandbox.CreateFile("c.txt", "c");

                var result = await tools.ReadMultipleFilesAsync(new[] { "a.txt", "b.txt", "c.txt" });

                Assert.False(result.IsSuccess);
                Assert.Contains("maximum number of files", result.Message!);
            }
        }

        [Fact]
        public async Task EmptyPathList_ReturnsFailure()
        {
            var (tools, sandbox) = CreateHarness();
            using (sandbox)
            {
                var result = await tools.ReadMultipleFilesAsync(Array.Empty<string>());

                Assert.False(result.IsSuccess);
                Assert.Contains("cannot be empty", result.Message!);
            }
        }

        [Fact]
        public async Task TotalSizeLimit_SkipsFileExceedingRemainingBudget_AndReadsFittingOnes()
        {
            // Бюджет 10 байт: первый файл 6 байт читается, второй (6 байт) уже не влезает.
            var (tools, sandbox) = CreateHarness(maxTotalSizeInBytes: 10);
            using (sandbox)
            {
                sandbox.CreateFile("first.txt", "123456");
                sandbox.CreateFile("second.txt", "abcdef");

                var result = await tools.ReadMultipleFilesAsync(new[] { "first.txt", "second.txt" });

                Assert.NotNull(result.Files);
                Assert.True(result.Files![0].IsSuccess);
                Assert.Equal("123456", result.Files[0].Content);

                Assert.False(result.Files[1].IsSuccess);
                Assert.Contains("Skipped", result.Files[1].Message!);
            }
        }
    }
}
