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
    /// Регрессионные тесты инструментов директорий: блокировка масок, chroot-валидация,
    /// усечение по MaxTreeEntries/MaxSearchResults и пропуск больших файлов при поиске по содержимому.
    /// </summary>
    public class DirToolsTests
    {
        private static (DirTools tools, TempSandbox sandbox) CreateHarness(
            int maxTreeEntries = 2000,
            int maxSearchResults = 50,
            long maxOneFileSizeInBytes = 4 * 1024 * 1024)
        {
            var sandbox = new TempSandbox();
            var options = new AppOptions
            {
                SandboxDir = sandbox.SandboxDir,
                BackupDir = sandbox.BackupDir,
                MaxOneFileSizeInBytes = maxOneFileSizeInBytes,
                MaxQuantityFiles = 5,
                MaxTotalSizeInBytes = 10 * 1024 * 1024,
                MaxTreeEntries = maxTreeEntries,
                MaxSearchResults = maxSearchResults
            };
            var context = new FakeSandboxContext { SandboxDir = sandbox.SandboxDir, BackupDir = sandbox.BackupDir };
            var dirService = new DirService(NullLogger<DirService>.Instance, new FakeOptionsMonitor(options));
            var pathValidator = new PathValidator(NullLogger<PathValidator>.Instance, context);
            var tools = new DirTools(NullLogger<DirTools>.Instance, dirService, pathValidator);
            return (tools, sandbox);
        }

        [Theory]
        [InlineData("*")]
        [InlineData("*.*")]
        [InlineData("**")]
        public async Task SearchFiles_BlockedMask_ReturnsFailure(string mask)
        {
            var (tools, sandbox) = CreateHarness();
            using (sandbox)
            {
                var result = await tools.SearchFilesAsync(".", mask);

                Assert.False(result.IsSuccess);
                Assert.Contains("blocked", result.Message!);
            }
        }

        [Fact]
        public async Task SearchFiles_NormalMask_FindsMatchingFilesOnly()
        {
            var (tools, sandbox) = CreateHarness();
            using (sandbox)
            {
                sandbox.CreateFile("a.txt", "a");
                sandbox.CreateFile("b.log", "b");
                sandbox.CreateFile(Path.Combine("sub", "c.txt"), "c");

                var result = await tools.SearchFilesAsync(".", "*.txt");

                Assert.True(result.IsSuccess);
                Assert.NotNull(result.Result);
                Assert.Equal(2, result.Result!.Count);
                Assert.All(result.Result!, e => Assert.EndsWith(".txt", e.Name));
            }
        }

        [Fact]
        public async Task SearchFiles_OutsideSandbox_IsRejected()
        {
            var (tools, sandbox) = CreateHarness();
            using (sandbox)
            {
                string outsideDir = sandbox.Root;

                var result = await tools.SearchFilesAsync(outsideDir, "*.txt");

                Assert.False(result.IsSuccess);
                Assert.Contains("Security Error", result.Message!);
            }
        }

        [Fact]
        public async Task SearchFiles_RespectsMaxSearchResults()
        {
            var (tools, sandbox) = CreateHarness(maxSearchResults: 2);
            using (sandbox)
            {
                for (int i = 0; i < 5; i++)
                    sandbox.CreateFile($"file{i}.txt", "x");

                var result = await tools.SearchFilesAsync(".", "*.txt");

                Assert.True(result.IsSuccess);
                Assert.Equal(2, result.Result!.Count);
                Assert.True(result.LimitReached);
            }
        }

        [Fact]
        public async Task DirectoryListFiles_RespectsMaxTreeEntries()
        {
            var (tools, sandbox) = CreateHarness(maxTreeEntries: 2);
            using (sandbox)
            {
                for (int i = 0; i < 5; i++)
                    sandbox.CreateFile($"file{i}.txt", "x");

                var result = await tools.GetDirectoryListFilesAsync(".");

                Assert.True(result.IsSuccess);
                Assert.Equal(2, result.Result!.Count);
            }
        }

        [Fact]
        public async Task DirectoryListFiles_WithoutArguments_UsesSandboxRoot()
        {
            var (tools, sandbox) = CreateHarness();
            using (sandbox)
            {
                sandbox.CreateFile("root-file.txt", "x");
                sandbox.CreateFile(Path.Combine("sub", "nested.txt"), "y");

                var result = await tools.GetDirectoryListFilesAsync();

                Assert.True(result.IsSuccess);
                Assert.NotNull(result.Result);
                Assert.Contains(result.Result!, e => e.Name.EndsWith("root-file.txt"));
                Assert.DoesNotContain(result.Result!, e => e.Name.Contains("nested.txt"));
            }
        }

        [Fact]
        public async Task SearchFilesByContent_FindsSubstring_AndSkipsOversizedFiles()
        {
            var (tools, sandbox) = CreateHarness(maxOneFileSizeInBytes: 16);
            using (sandbox)
            {
                sandbox.CreateFile("small.txt", "needle");
                sandbox.CreateFile("big.txt", "needle" + new string('x', 32));

                var result = await tools.SearchFilesByContentAsync(".", "needle");

                Assert.True(result.IsSuccess);
                Assert.NotNull(result.Result);
                Assert.Single(result.Result!);
                Assert.EndsWith("small.txt", result.Result![0].Name);
            }
        }

        [Fact]
        public async Task CreateDirectory_OutsideSandbox_IsRejected()
        {
            var (tools, sandbox) = CreateHarness();
            using (sandbox)
            {
                string outsideDir = Path.Combine(sandbox.Root, "outside-new-dir");

                var result = await tools.CreateDirectoryAsync(outsideDir);

                Assert.False(result.IsSuccess);
                Assert.Contains("Security Error", result.Message!);
                Assert.False(Directory.Exists(outsideDir));
            }
        }

        [Fact]
        public async Task CreateDirectory_InsideSandbox_Succeeds()
        {
            var (tools, sandbox) = CreateHarness();
            using (sandbox)
            {
                var result = await tools.CreateDirectoryAsync("new/sub");

                Assert.True(result.IsSuccess);
                Assert.True(Directory.Exists(Path.Combine(sandbox.SandboxDir, "new", "sub")));
            }
        }
    }
}
