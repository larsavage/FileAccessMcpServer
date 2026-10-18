using FileAccessMcpServer.Models;
using FileAccessMcpServer.Services;
using FileAccessMcpServer.Tests.Infra;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace FileAccessMcpServer.Tests
{
    /// <summary>
    /// Регрессионные тесты ограничения размера файлов (MaxOneFileSizeInBytes)
    /// для чтения текста/изображений/бинарников и записи/дозаписи.
    /// </summary>
    public class FileServiceLimitsTests
    {
        private const int LimitBytes = 64;

        private static (FileService service, TempSandbox sandbox) CreateHarness()
        {
            var sandbox = new TempSandbox();
            var options = new AppOptions
            {
                SandboxDir = sandbox.SandboxDir,
                BackupDir = sandbox.BackupDir,
                MaxOneFileSizeInBytes = LimitBytes,
                MaxQuantityFiles = 5,
                MaxTotalSizeInBytes = 10 * 1024 * 1024,
                MaxTreeEntries = 2000,
                MaxSearchResults = 50
            };
            var context = new FakeSandboxContext { SandboxDir = sandbox.SandboxDir, BackupDir = sandbox.BackupDir };
            var backup = new BackupService(context, NullLogger<BackupService>.Instance);
            var service = new FileService(NullLogger<FileService>.Instance, new FakeOptionsMonitor(options), backup);
            return (service, sandbox);
        }

        private static string CreateBinaryFile(TempSandbox sandbox, string name, int size)
        {
            string path = Path.Combine(sandbox.SandboxDir, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        [Fact]
        public async Task FileReadAsync_FileExceedsLimit_ReturnsFailure()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string path = CreateBinaryFile(sandbox, "big.txt", LimitBytes + 1);

                var result = await service.FileReadAsync(path);

                Assert.False(result.IsSuccess);
                Assert.Contains("too large", result.Message!);
            }
        }

        [Fact]
        public async Task FileReadAsync_FileWithinLimit_Succeeds()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string path = sandbox.CreateFile("small.txt", new string('a', LimitBytes));

                var result = await service.FileReadAsync(path);

                Assert.True(result.IsSuccess);
                Assert.Equal(LimitBytes, result.Content!.Length);
            }
        }

        [Fact]
        public async Task FileImageReadAsync_FileExceedsLimit_ReturnsFailure()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string path = CreateBinaryFile(sandbox, "big.png", LimitBytes + 1);

                var result = await service.FileImageReadAsync(path);

                Assert.False(result.IsSuccess);
                Assert.Contains("too large", result.Message!);
                Assert.Null(result.DataUrl);
            }
        }

        [Fact]
        public async Task FileBinaryReadAsync_FileExceedsLimit_ReturnsFailure()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string path = CreateBinaryFile(sandbox, "big.bin", LimitBytes + 1);

                var result = await service.FileBinaryReadAsync(path);

                Assert.False(result.IsSuccess);
                Assert.Contains("too large", result.Message!);
                Assert.Null(result.Base64Data);
            }
        }

        [Fact]
        public async Task FileWriteAsync_ContentExceedsLimit_ReturnsFailure_AndDoesNotCreateFile()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string path = Path.Combine(sandbox.SandboxDir, "written.txt");

                var result = await service.FileWriteAsync(path, new string('x', LimitBytes + 1));

                Assert.False(result.IsSuccess);
                Assert.Contains("too large", result.Message!);
                Assert.False(File.Exists(path));
            }
        }

        [Fact]
        public async Task FileWriteAsync_ContentWithinLimit_Succeeds()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string path = Path.Combine(sandbox.SandboxDir, "written.txt");

                var result = await service.FileWriteAsync(path, new string('x', LimitBytes));

                Assert.True(result.IsSuccess);
                // GetEncoding("utf-8") пишет BOM, поэтому сравниваем содержимое, а не длину файла.
                Assert.Equal(new string('x', LimitBytes), File.ReadAllText(path));
            }
        }

        [Fact]
        public async Task FileAppendAsync_ExistingPlusContentExceedsLimit_ReturnsFailure_AndKeepsOriginal()
        {
            var (service, sandbox) = CreateHarness();
            using (sandbox)
            {
                string original = new string('a', LimitBytes - 16);
                string path = sandbox.CreateFile("append.txt", original);

                var result = await service.FileAppendAsync(path, new string('b', 32));

                Assert.False(result.IsSuccess);
                Assert.Contains("too large", result.Message!);
                Assert.Equal(original, File.ReadAllText(path));
            }
        }
    }
}
