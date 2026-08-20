using FileAccessMcpServer.Core;
using FileAccessMcpServer.Models;
using FileAccessMcpServer.Tests.Infra;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace FileAccessMcpServer.Tests
{
    /// <summary>
    /// Регрессионные тесты ApiKeyMiddleware: fail-open, постоянное время сравнения (FixedTimeEquals),
    /// публичные health-эндпоинты.
    /// </summary>
    public class ApiKeyMiddlewareTests
    {
        private static async Task<(int StatusCode, bool NextCalled)> InvokeAsync(string path, string? apiKey, string? headerKey)
        {
            var options = new FakeOptionsMonitor(new AppOptions { ApiKey = apiKey });
            bool nextCalled = false;

            var middleware = new ApiKeyMiddleware(
                next: ctx => { nextCalled = true; ctx.Response.StatusCode = 200; return Task.CompletedTask; },
                options: options,
                logger: NullLogger<ApiKeyMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Path = path;
            if (headerKey != null)
                context.Request.Headers[ApiKeyMiddleware.HeaderName] = headerKey;

            await middleware.InvokeAsync(context);
            return (context.Response.StatusCode, nextCalled);
        }

        [Fact]
        public async Task NoKeyConfigured_AllowsAllRequests()
        {
            var (status, nextCalled) = await InvokeAsync("/mcp", apiKey: null, headerKey: null);

            Assert.True(nextCalled);
            Assert.Equal(200, status);
        }

        [Fact]
        public async Task ValidKey_AllowsRequest()
        {
            var (status, nextCalled) = await InvokeAsync("/mcp", "secret-key", "secret-key");

            Assert.True(nextCalled);
            Assert.Equal(200, status);
        }

        [Fact]
        public async Task MissingHeader_Returns401()
        {
            var (status, nextCalled) = await InvokeAsync("/mcp", "secret-key", headerKey: null);

            Assert.False(nextCalled);
            Assert.Equal(401, status);
        }

        [Fact]
        public async Task WrongKey_Returns401()
        {
            var (status, nextCalled) = await InvokeAsync("/mcp", "secret-key", "wrong-key");

            Assert.False(nextCalled);
            Assert.Equal(401, status);
        }

        [Fact]
        public async Task WrongKey_SameLengthAsValid_Returns401()
        {
            // Одинаковая длина — чтобы ветка FixedTimeEquals (не ранний exit по длине) была задействована.
            var (status, nextCalled) = await InvokeAsync("/mcp", "secret-key-0123456789", "secret-key-9876543210");

            Assert.False(nextCalled);
            Assert.Equal(401, status);
        }

        [Fact]
        public async Task KeyComparison_IsCaseSensitive()
        {
            var (status, nextCalled) = await InvokeAsync("/mcp", "secret-key", "SECRET-KEY");

            Assert.False(nextCalled);
            Assert.Equal(401, status);
        }

        [Theory]
        [InlineData("/healthz")]
        [InlineData("/readyz")]
        public async Task PublicPaths_DoNotRequireKey(string path)
        {
            var (status, nextCalled) = await InvokeAsync(path, "secret-key", headerKey: null);

            Assert.True(nextCalled);
            Assert.Equal(200, status);
        }

        [Fact]
        public async Task SimilarButNotPublicPath_StillRequiresKey()
        {
            // /healthz/extra — НЕ публичный путь (строгое сравнение), требует ключ.
            var (status, nextCalled) = await InvokeAsync("/healthz/extra", "secret-key", headerKey: null);

            Assert.False(nextCalled);
            Assert.Equal(401, status);
        }
    }
}