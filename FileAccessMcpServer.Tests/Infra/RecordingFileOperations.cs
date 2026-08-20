using FileAccessMcpServer.Interfaces;
using FileAccessMcpServer.Models;

namespace FileAccessMcpServer.Tests.Infra
{
    /// <summary>
    /// Фейковый IFileOperations: записывает вызовы FileDeleteAsync, не трогая файловую систему.
    /// Нужен для детерминированных тестов логики токенов (файл «остаётся на месте»).
    /// </summary>
    internal sealed class RecordingFileOperations : IFileOperations
    {
        public int DeleteCalls { get; private set; }

        public OperationResult DeleteResult { get; set; } = OperationResult.Success();

        public Task<OperationResult> FileDeleteAsync(string filePath)
        {
            DeleteCalls++;
            return Task.FromResult(DeleteResult);
        }

        public Task<FileInfoResult> FileGetInfoAsync(string filePath) => throw new NotSupportedException();
        public Task<FileReadResult> FileReadAsync(string filePath, string encoding = "utf-8") => throw new NotSupportedException();
        public Task<FileWriteResult> FileWriteAsync(string filePath, string content, string encoding = "utf-8") => throw new NotSupportedException();
        public Task<FileWriteResult> FileAppendAsync(string filePath, string content, string encoding = "utf-8") => throw new NotSupportedException();
        public Task<FileWriteResult> FileReplaceLinesAsync(string filePath, long startLine, long lineCount, string newContent, string encoding = "utf-8") => throw new NotSupportedException();
        public Task<FileReadChunkResult> FileReadChunksAsync(string filePath, long startChar = 0, int charsCount = 1000000, string encoding = "utf-8") => throw new NotSupportedException();
        public Task<FileImageResult> FileImageReadAsync(string filePath) => throw new NotSupportedException();
        public Task<FileBinaryReadResult> FileBinaryReadAsync(string filePath) => throw new NotSupportedException();
        public Task<OperationResult> FileCopyAsync(string filePath, string newFilePath, bool isRewrite = false) => throw new NotSupportedException();
        public Task<OperationResult> FileMoveAsync(string filePath, string newFilePath, bool isRewrite = false) => throw new NotSupportedException();
        public Task<FileHashResult> FileHashAsync(string filePath, string algorithm) => throw new NotSupportedException();
    }
}