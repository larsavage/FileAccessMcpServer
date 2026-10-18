using FileAccessMcpServer.Core;
using FileAccessMcpServer.Models;

using Xunit;

namespace FileAccessMcpServer.Tests
{
    /// <summary>
    /// Регрессионные тесты валидации конфигурации (ConfigValidator).
    /// </summary>
    public class ConfigValidatorTests
    {
        private static AppOptions ValidOptions() => new AppOptions
        {
            SandboxDir = Path.Combine(Path.GetTempPath(), "FileAccessMcp.Tests.Sandbox." + Guid.NewGuid().ToString("N")),
            BackupDir = Path.Combine(Path.GetTempPath(), "FileAccessMcp.Tests.Backup." + Guid.NewGuid().ToString("N")),
            MaxOneFileSizeInBytes = 1024,
            MaxQuantityFiles = 5,
            MaxTotalSizeInBytes = 4096,
            MaxTreeEntries = 100,
            MaxSearchResults = 50,
            RateLimit = new RateLimitOptions
            {
                Enabled = true,
                WindowInSeconds = 60,
                PermitLimit = 10,
                QueueLimit = 0
            }
        };

        [Fact]
        public void ValidConfiguration_HasNoErrors()
        {
            Assert.Empty(ConfigValidator.Validate(ValidOptions()));
        }

        [Fact]
        public void MissingSandboxDir_IsReported()
        {
            var options = ValidOptions();
            options.SandboxDir = "   ";

            var errors = ConfigValidator.Validate(options);

            Assert.Contains(errors, e => e.Contains("SandboxDir"));
        }

        [Fact]
        public void RelativeSandboxDir_IsReported()
        {
            var options = ValidOptions();
            options.SandboxDir = Path.Combine("relative", "sandbox");

            var errors = ConfigValidator.Validate(options);

            Assert.Contains(errors, e => e.Contains("absolute"));
        }

        [Fact]
        public void SandboxDirPointingToFile_IsReported()
        {
            string file = Path.GetTempFileName();
            try
            {
                var options = ValidOptions();
                options.SandboxDir = file;

                var errors = ConfigValidator.Validate(options);

                Assert.Contains(errors, e => e.Contains("existing file"));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void RelativeBackupDir_IsReported()
        {
            var options = ValidOptions();
            options.BackupDir = Path.Combine("relative", "backup");

            var errors = ConfigValidator.Validate(options);

            Assert.Contains(errors, e => e.Contains("BackupDir") && e.Contains("absolute"));
        }

        [Fact]
        public void NonPositiveLimits_AreReported()
        {
            var options = ValidOptions();
            options.MaxOneFileSizeInBytes = 0;
            options.MaxQuantityFiles = 0;
            options.MaxTotalSizeInBytes = 0;
            options.MaxTreeEntries = 0;
            options.MaxSearchResults = 0;

            var errors = ConfigValidator.Validate(options);

            Assert.Contains(errors, e => e.Contains("MaxOneFileSizeInBytes"));
            Assert.Contains(errors, e => e.Contains("MaxQuantityFiles"));
            Assert.Contains(errors, e => e.Contains("MaxTotalSizeInBytes"));
            Assert.Contains(errors, e => e.Contains("MaxTreeEntries"));
            Assert.Contains(errors, e => e.Contains("MaxSearchResults"));
        }

        [Fact]
        public void MaxTotalSizeLessThanMaxOneFileSize_IsReported()
        {
            var options = ValidOptions();
            options.MaxOneFileSizeInBytes = 4096;
            options.MaxTotalSizeInBytes = 1024;

            var errors = ConfigValidator.Validate(options);

            Assert.Contains(errors, e => e.Contains("cannot be less than"));
        }

        [Fact]
        public void InvalidRateLimit_WhenEnabled_IsReported()
        {
            var options = ValidOptions();
            options.RateLimit = new RateLimitOptions
            {
                Enabled = true,
                WindowInSeconds = 0,
                PermitLimit = 0,
                QueueLimit = -1
            };

            var errors = ConfigValidator.Validate(options);

            Assert.Contains(errors, e => e.Contains("WindowInSeconds"));
            Assert.Contains(errors, e => e.Contains("PermitLimit"));
            Assert.Contains(errors, e => e.Contains("QueueLimit"));
        }

        [Fact]
        public void InvalidRateLimit_WhenDisabled_IsIgnored()
        {
            var options = ValidOptions();
            options.RateLimit = new RateLimitOptions
            {
                Enabled = false,
                WindowInSeconds = 0,
                PermitLimit = 0,
                QueueLimit = -1
            };

            Assert.Empty(ConfigValidator.Validate(options));
        }
    }
}
