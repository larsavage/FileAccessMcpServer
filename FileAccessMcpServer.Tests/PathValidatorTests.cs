using FileAccessMcpServer.Core;
using FileAccessMcpServer.Tests.Infra;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace FileAccessMcpServer.Tests
{
    /// <summary>
    /// Регрессионные тесты chroot/symlink-защиты PathValidator.
    /// </summary>
    public class PathValidatorTests
    {
        private static PathValidator CreateValidator(string sandboxDir)
        {
            var context = new FakeSandboxContext { SandboxDir = sandboxDir };
            return new PathValidator(NullLogger<PathValidator>.Instance, context);
        }

        [Fact]
        public void ChrootValidation_PathInsideSandbox_ReturnsTrue()
        {
            using var sb = new TempSandbox();
            var validator = CreateValidator(sb.SandboxDir);
            string target = Path.Combine(sb.SandboxDir, "sub", "file.txt");

            Assert.True(validator.ChrootValidation(sb.SandboxDir, target));
        }

        [Fact]
        public void ChrootValidation_TargetEqualToSandboxRoot_ReturnsTrue()
        {
            using var sb = new TempSandbox();
            var validator = CreateValidator(sb.SandboxDir);

            Assert.True(validator.ChrootValidation(sb.SandboxDir, sb.SandboxDir));
        }

        [Fact]
        public void ChrootValidation_PathOutsideSandbox_ReturnsFalse()
        {
            using var sb = new TempSandbox();
            var validator = CreateValidator(sb.SandboxDir);
            string target = Path.GetFullPath(Path.Combine(sb.Root, "..", "outside", "file.txt"));

            Assert.False(validator.ChrootValidation(sb.SandboxDir, target));
        }

        [Fact]
        public void ChrootValidation_SiblingPrefixAttack_ReturnsFalse()
        {
            // «sandbox-secret» начинается с «sandbox», но это соседний каталог — должен быть заблокирован.
            using var sb = new TempSandbox();
            string sibling = sb.SandboxDir + "-secret";
            Directory.CreateDirectory(sibling);
            var validator = CreateValidator(sb.SandboxDir);

            Assert.False(validator.ChrootValidation(sb.SandboxDir, Path.Combine(sibling, "file.txt")));
        }

        [Fact]
        public void ChrootValidation_TraversalResolvedInsideSandbox_Passes()
        {
            using var sb = new TempSandbox();
            var validator = CreateValidator(sb.SandboxDir);
            string target = Path.GetFullPath(Path.Combine(sb.SandboxDir, "sub", "..", "file.txt"));

            Assert.True(validator.ChrootValidation(sb.SandboxDir, target));
        }

        [Fact]
        public void ChrootValidation_EmptyRootOrTarget_ReturnsFalse()
        {
            using var sb = new TempSandbox();
            var validator = CreateValidator(sb.SandboxDir);

            Assert.False(validator.ChrootValidation(string.Empty, Path.Combine(sb.SandboxDir, "f.txt")));
            Assert.False(validator.ChrootValidation(sb.SandboxDir, string.Empty));
        }

        [Fact]
        public void ValidateAndGetAbsolutePath_OutsideSandbox_ReturnsSecurityError()
        {
            using var sb = new TempSandbox();
            var validator = CreateValidator(sb.SandboxDir);
            string outside = Path.GetFullPath(Path.Combine(sb.Root, "..", "outside.txt"));

            var error = validator.ValidateAndGetAbsolutePath(outside, out _);

            Assert.NotNull(error);
            Assert.False(error!.IsSuccess);
            Assert.Contains("Security Error", error.Message);
        }

        [Fact]
        public void ValidateAndGetAbsolutePath_NonexistentInsideSandbox_Returns404()
        {
            using var sb = new TempSandbox();
            var validator = CreateValidator(sb.SandboxDir);

            var error = validator.ValidateAndGetAbsolutePath("missing.txt", out _);

            Assert.NotNull(error);
            Assert.Equal(404, error!.Code);
        }

        [Fact]
        public void ValidateAndGetAbsolutePath_ValidFile_ReturnsAbsolutePath()
        {
            using var sb = new TempSandbox();
            sb.CreateFile("file.txt", "hello");
            var validator = CreateValidator(sb.SandboxDir);

            var error = validator.ValidateAndGetAbsolutePath("file.txt", out string absolutePath);

            Assert.Null(error);
            Assert.True(Path.IsPathRooted(absolutePath));
            Assert.StartsWith(sb.SandboxDir, absolutePath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SymlinkPointingOutsideSandbox_IsRejected()
        {
            using var sb = new TempSandbox();
            string outsideFile = Path.Combine(sb.Root, "outside-secret.txt");
            File.WriteAllText(outsideFile, "secret");
            string link = Path.Combine(sb.SandboxDir, "link.txt");

            // Симлинки на Windows требуют dev mode/прав администратора — при недоступности пропускаем.
            if (!TryCreateFileSymlink(outsideFile, link))
                return;

            var validator = CreateValidator(sb.SandboxDir);

            Assert.False(validator.ChrootValidation(sb.SandboxDir, link));
            Assert.NotNull(validator.ValidateAndGetAbsolutePath("link.txt", out _));
        }

        [Fact]
        public void SymlinkPointingInsideSandbox_IsAllowed()
        {
            using var sb = new TempSandbox();
            string innerTarget = sb.CreateFile("real.txt", "content");
            string link = Path.Combine(sb.SandboxDir, "alias.txt");

            if (!TryCreateFileSymlink(innerTarget, link))
                return;

            var validator = CreateValidator(sb.SandboxDir);

            Assert.True(validator.ChrootValidation(sb.SandboxDir, link));
            Assert.Null(validator.ValidateAndGetAbsolutePath("alias.txt", out _));
        }

        private static bool TryCreateFileSymlink(string target, string link)
        {
            try
            {
                File.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                return false;
            }
        }
    }
}