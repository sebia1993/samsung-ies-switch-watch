using System.Text;
using System.Security;

namespace SamsungSwitchWatch.Viewer.Services;

internal static class ViewerStoreFileLimits
{
    public const int SettingsBytes = 1 * 1024 * 1024;
    public const int ManagedDevicesBytes = 8 * 1024 * 1024;
    public const int MonitoringStateBytes = 16 * 1024 * 1024;
}

internal sealed class ViewerStoreSizeLimitException(string code) : IOException(code);

internal static class BoundedUtf8File
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string? ReadIfExists(string path, int maximumBytes, string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > maximumBytes)
            {
                throw new ViewerStoreSizeLimitException(errorCode);
            }

            var bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }

            // Re-check after reading so a concurrently growing file cannot
            // bypass the pre-allocation limit.
            if (stream.ReadByte() != -1)
            {
                throw new ViewerStoreSizeLimitException(errorCode);
            }

            var start = offset >= 3
                        && bytes[0] == 0xef
                        && bytes[1] == 0xbb
                        && bytes[2] == 0xbf
                ? 3
                : 0;
            return StrictUtf8.GetString(bytes, start, offset - start);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}

internal static class AtomicUtf8File
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void Write(
        string path,
        string content,
        int maximumBytes,
        string invalidPathCode,
        string sizeLimitCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidPathCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(sizeLimitCode);
        if (Utf8WithoutBom.GetByteCount(content) > maximumBytes)
        {
            throw new ViewerStoreSizeLimitException(sizeLimitCode);
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException(invalidPathCode);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       Utf8WithoutBom,
                       bufferSize: 4096,
                       leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(
                    temporaryPath,
                    fullPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            FlushCommittedFileBestEffort(fullPath, static committedPath =>
            {
                using var committedStream = new FileStream(
                    committedPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                committedStream.Flush(flushToDisk: true);
            });
        }
        finally
        {
            DeleteTemporaryBestEffort(temporaryPath);
        }
    }

    internal static void FlushCommittedFileBestEffort(
        string path,
        Action<string> flush)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(flush);
        try
        {
            flush(path);
        }
        catch (Exception exception) when (
            IsIgnorablePostCommitFlushFailure(path, exception))
        {
            // The temp file was durably flushed before the atomic replacement.
            // AV/EDR can deny only this post-commit re-open; reporting failure
            // now would leave disk and in-memory state disagreeing even though
            // the destination already contains the new complete document.
        }
    }

    private static bool IsIgnorablePostCommitFlushFailure(
        string path,
        Exception exception) =>
        exception is not FileNotFoundException
        && exception is not DirectoryNotFoundException
        && exception is IOException or UnauthorizedAccessException or SecurityException
        && File.Exists(path);

    private static void DeleteTemporaryBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The destination result is authoritative. AV/EDR can retain the
            // temporary name briefly and must not hide that result.
        }
    }
}
