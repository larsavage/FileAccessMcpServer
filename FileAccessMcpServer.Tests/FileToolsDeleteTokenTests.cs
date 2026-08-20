using System.Text.RegularExpressions;

using FileAccessMcpServer.Core;
using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;
using FileAccessMcpServer.Services;
using FileAccessMcpServer.Tests.Infra;
using FileAccessMcpServer.Tools;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace FileAccessMcpServer.Tests
{
    /// <summary>
    /// Регрессионные тесты двухэтапного подтверждения удаления (file_delete):
    /// генерация токена, одноразовое потребление, lock-сериализация.
    /// Логика токенов тестируется на фейковом IFileOperations (детерминированно),
    /// плюс один сквозной сценарий с реальным FileService (файл реально удаляется).
    /// </summary>
    public class FileToolsDeleteTokenTests
    {
        private const string FilePath = "target.txt";

        private static (FileTools tools, TempSandbox sandbox) CreateHarness(IFileOperations fileOperations)
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
            var pathValidator = new PathValidator(NullLogger<PathValidator>.Instance, context);
            var tools = new FileTools(NullLogger<FileTools>.Instance, fileOperations, options, pathValidator);

            return (tools, sandbox);
        }

        private static (FileTools tools, TempSandbox sandbox, RecordingFileOperations fake) CreateTokenHarness()
        {
            var fake = new RecordingFileOperations();
            var (tools, sandbox) = CreateHarness(fake);
            return (tools, sandbox, fake);
        }

        private static string ExtractToken(string message)
        {
            var match = Regex.Match(message, @"confirmToken значение:\s*([0-9A-Fa-f]{32})");
            Assert.True(match.Success, $"No 32-hex token found in message: {message}");
            return match.Groups[1].Value;
        }

        [Fact]
        public async Task FirstCall_Returns202_With32HexToken()
        {
            var (tools, sandbox, _) = CreateTokenHarness();
            using (sandbox)
            {
                sandbox.CreateFile(FilePath, "hello");

                var result = await tools.FileDeleteAsync(FilePath);

                Assert.True(result.IsSuccess);
                Assert.Equal(202, result.Code);
                string token = ExtractToken(result.Message!);
                Assert.Equal(32, token.Length);
                Assert.Matches(new Regex("^[0-9A-Fa-f]{32}$"), token);
            }
        }

        [Fact]
        public async Task SecondCall_WithValidToken_InvokesDeletionOnce()
        {
            var (tools, sandbox, fake) = CreateTokenHarness();
            using (sandbox)
            {
                sandbox.CreateFile(FilePath, "hello");
                var first = await tools.FileDeleteAsync(FilePath);
                string token = ExtractToken(first.Message!);

                var second = await tools.FileDeleteAsync(FilePath, token);

                Assert.True(second.IsSuccess);
                Assert.Equal(1, fake.DeleteCalls);
            }
        }

        [Fact]
        public async Task SecondCall_WithWrongToken_Rejected_NoDeletion()
        {
            var (tools, sandbox, fake) = CreateTokenHarness();
            using (sandbox)
            {
                sandbox.CreateFile(FilePath, "hello");
                var first = await tools.FileDeleteAsync(FilePath);
                _ = ExtractToken(first.Message!);

                var second = await tools.FileDeleteAsync(FilePath, new string('A', 32));

                Assert.False(second.IsSuccess);
                Assert.Contains("[ОТКЛОНЕНО]", second.Message);
                Assert.Equal(0, fake.DeleteCalls);
            }
        }

        [Fact]
        public async Task ReuseOfConsumedToken_ReturnsExpiredError()
        {
            var (tools, sandbox, fake) = CreateTokenHarness();
            using (sandbox)
            {
                sandbox.CreateFile(FilePath, "hello");
                var first = await tools.FileDeleteAsync(FilePath);
                string token = ExtractToken(first.Message!);

                var second = await tools.FileDeleteAsync(FilePath, token);
                Assert.True(second.IsSuccess);

                var third = await tools.FileDeleteAsync(FilePath, token);

                Assert.False(third.IsSuccess);
                Assert.Contains("[ОШИБКА]", third.Message);
                Assert.Equal(1, fake.DeleteCalls);
            }
        }

        [Fact]
        public async Task ConcurrentSecondCalls_SameToken_OnlyOneWins()
        {
            var (tools, sandbox, fake) = CreateTokenHarness();
            using (sandbox)
            {
                sandbox.CreateFile(FilePath, "hello");
                var first = await tools.FileDeleteAsync(FilePath);
                string token = ExtractToken(first.Message!);

                var call1 = tools.FileDeleteAsync(FilePath, token);
                var call2 = tools.FileDeleteAsync(FilePath, token);
                var results = await Task.WhenAll(call1, call2);

                Assert.Equal(1, results.Count(r => r.IsSuccess));
                Assert.Equal(1, results.Count(r => !r.IsSuccess && r.Message!.Contains("[ОШИБКА]")));
                Assert.Equal(1, fake.DeleteCalls);
            }
        }

        [Fact]
        public async Task PathOutsideSandbox_Rejected_BeforeTokenGeneration()
        {
            var (tools, sandbox, fake) = CreateTokenHarness();
            using (sandbox)
            {
                string outside = Path.GetFullPath(Path.Combine(sandbox.Root, "..", "outside.txt"));

                var result = await tools.FileDeleteAsync(outside);

                Assert.False(result.IsSuccess);
                Assert.Contains("Security Error", result.Message);
                Assert.Equal(0, fake.DeleteCalls);
            }
        }

        [Fact]
        public async Task SecondCall_WithValidToken_DeletesRealFile_EndToEnd()
        {
            using var sandbox = new TempSandbox();
            var context = new FakeSandboxContext { SandboxDir = sandbox.SandboxDir, BackupDir = sandbox.BackupDir };
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
            var backup = new BackupService(context, NullLogger<BackupService>.Instance);
            var fileService = new FileService(NullLogger<FileService>.Instance, options, backup);
            var pathValidator = new PathValidator(NullLogger<PathValidator>.Instance, context);
            var tools = new FileTools(NullLogger<FileTools>.Instance, fileService, options, pathValidator);

            string file = sandbox.CreateFile(FilePath, "hello");
            var first = await tools.FileDeleteAsync(FilePath);
            string token = ExtractToken(first.Message!);

            var second = await tools.FileDeleteAsync(FilePath, token);

            Assert.True(second.IsSuccess);
            Assert.False(File.Exists(file));
        }
    }
}