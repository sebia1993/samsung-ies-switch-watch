using System.Security.Cryptography;
using System.Security;
using System.Text;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Infrastructure;

public sealed class PhysicalViewerSetupFileSystem : IViewerSetupFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateTopLevelFiles(string path) =>
        Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);

    public IReadOnlyList<string> EnumerateTopLevelDirectories(string path) =>
        Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);

    public string ReadAllText(string path) =>
        File.ReadAllText(path, new UTF8Encoding(false, true));

    public string ReadAllTextBounded(string path, int maximumBytes) =>
        ReadUtf8TextBounded(path, maximumBytes);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    internal static string ReadUtf8TextBounded(string path, int maximumBytes)
    {
        if (maximumBytes <= 0 || maximumBytes == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var buffer = GC.AllocateUninitializedArray<byte>(maximumBytes + 1);
        var totalRead = 0;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 64 * 1024,
                   FileOptions.SequentialScan))
        {
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }
        }

        if (totalRead > maximumBytes)
        {
            throw new IOException("The text file exceeds the allowed size.");
        }

        var offset = totalRead >= 3
                     && buffer[0] == 0xef
                     && buffer[1] == 0xbb
                     && buffer[2] == 0xbf
            ? 3
            : 0;
        return new UTF8Encoding(false, true).GetString(
            buffer,
            offset,
            totalRead - offset);
    }

    public string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string source, string destination, bool overwrite)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.Copy(source, destination, overwrite);
    }

    public void MoveDirectory(string source, string destination) =>
        Directory.Move(source, destination);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void WriteAllTextAtomic(string path, string contents) =>
        WriteAllBytesAtomic(path, new UTF8Encoding(false).GetBytes(contents));

    public void WriteAllBytesAtomic(string path, byte[] contents) =>
        WriteAtomic(path, contents);

    public void EnsureDirectoryWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".viewer-setup-write-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(
                    probe,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathNotWritable,
                "Viewer 설치 또는 작업 기록 폴더에 쓸 수 없습니다.",
                exception);
        }
    }

    public bool DirectoryHasEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path).Any();

    public bool IsReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    public bool DirectoryTreeContainsReparsePoint(string path) =>
        ContainsReparsePoint(new DirectoryInfo(path));

    public void DeleteDirectoryTreeNoFollow(string path)
    {
        if (!Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException("The quarantine cleanup target is not a directory.");
        }

        DeleteDirectoryContentsNoFollow(path);
    }

    private static void WriteAtomic(string path, byte[] contents)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new IOException("The destination parent directory is missing.");
        }

        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // Replace the journal in one filesystem operation. A backup is
                // intentionally omitted because transaction recovery owns the
                // previous state and EDR cleanup of an unused backup must not
                // turn a committed write into a false failure.
                File.Replace(
                    temporary,
                    path,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }

            FlushCommittedFileBestEffort(path, FlushCommittedFile);
        }
        finally
        {
            DeleteTemporaryFileBestEffort(temporary, File.Exists, File.Delete);
        }
    }

    internal static void FlushCommittedFileBestEffort(
        string path,
        Action<string> flush)
    {
        ArgumentNullException.ThrowIfNull(flush);
        try
        {
            flush(path);
        }
        catch (Exception exception) when (
            IsIgnorablePostCommitFlushFailure(path, exception))
        {
            // File.Replace/File.Move already committed the new journal. A
            // scanner can temporarily block only the durability re-open; do
            // not report a false write failure after the visible state changed.
        }
    }

    private static bool IsIgnorablePostCommitFlushFailure(
        string path,
        Exception exception) =>
        exception is not FileNotFoundException
        && exception is not DirectoryNotFoundException
        && exception is IOException or UnauthorizedAccessException or SecurityException
        && File.Exists(path);

    private static void FlushCommittedFile(string path)
    {
        using var committedStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        committedStream.Flush(flushToDisk: true);
    }

    internal static void DeleteTemporaryFileBestEffort(
        string path,
        Func<string, bool> exists,
        Action<string> delete)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(delete);
        if (!exists(path))
        {
            return;
        }

        try
        {
            delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The destination write is already authoritative. A scanner may
            // retain the temporary name briefly; do not hide the original
            // outcome or report a committed journal replacement as failed.
        }
    }

    private static bool ContainsReparsePoint(DirectoryInfo directory)
    {
        directory.Refresh();
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            entry.Refresh();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0 &&
                ContainsReparsePoint((DirectoryInfo)entry))
            {
                return true;
            }
        }

        return false;
    }

    private static void DeleteDirectoryContentsNoFollow(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SecurityException("A reparse point was found in quarantine data.");
        }

        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            entry.Refresh();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SecurityException("A reparse point was found in quarantine data.");
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryContentsNoFollow(entry.FullName);
            }
            else
            {
                File.Delete(entry.FullName);
            }
        }

        // Re-check immediately before deleting the directory. If another
        // process replaced it with a junction, fail without following it.
        attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SecurityException("Quarantine data changed during cleanup.");
        }

        Directory.Delete(path, recursive: false);
    }
}
