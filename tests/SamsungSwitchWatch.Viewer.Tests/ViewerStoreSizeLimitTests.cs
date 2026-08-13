using System.IO;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerStoreSizeLimitTests
{
    [Fact]
    public void PostCommitFlush_AccessFailureDoesNotTurnCommittedWriteIntoFailure()
    {
        var path = Path.GetTempFileName();
        try
        {
            AtomicUtf8File.FlushCommittedFileBestEffort(
                path,
                _ => throw new UnauthorizedAccessException("synthetic EDR lock"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PostCommitFlush_MissingDestinationRemainsFailure()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ssw-missing-" + Guid.NewGuid().ToString("N") + ".json");

        Assert.Throws<FileNotFoundException>(() =>
            AtomicUtf8File.FlushCommittedFileBestEffort(
                path,
                _ => throw new FileNotFoundException("synthetic quarantine", path)));
    }

    [Fact]
    public void PostCommitFlush_UnexpectedProgrammingFailureStillPropagates()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AtomicUtf8File.FlushCommittedFileBestEffort(
                "viewer-settings.json",
                _ => throw new InvalidOperationException("synthetic invariant")));
    }

    [Fact]
    public void AtomicUtf8File_CreateAndReplaceLeaveOneBomlessCommittedFile()
    {
        var folder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ssw-atomic-store-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(folder, "state.json");
        try
        {
            AtomicUtf8File.Write(path, "첫 번째", 1024, "INVALID", "TOO_LARGE");
            AtomicUtf8File.Write(path, "두 번째", 1024, "INVALID", "TOO_LARGE");

            Assert.Equal("두 번째", File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)));
            Assert.DoesNotContain(
                Directory.GetFiles(folder),
                item => System.IO.Path.GetFileName(item).Contains(".tmp", StringComparison.Ordinal));
            Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(
                new byte[] { 0xEF, 0xBB, 0xBF }));
        }
        finally
        {
            DeleteDirectoryBestEffort(folder);
        }
    }

    [Fact]
    public void BoundedUtf8File_AcceptsLegacyUtf8BomWithoutLeakingItIntoJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, (byte)'{', (byte)'}']);

            var result = BoundedUtf8File.ReadIfExists(path, 1024, "TOO_LARGE");

            Assert.Equal("{}", result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AtomicUtf8File_RejectsOversizedContentBeforeReplacingExistingFile()
    {
        var folder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ssw-atomic-limit-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(folder, "state.json");
        try
        {
            AtomicUtf8File.Write(path, "preserved", 1024, "INVALID", "TOO_LARGE");

            var exception = Assert.Throws<ViewerStoreSizeLimitException>(() =>
                AtomicUtf8File.Write(path, new string('가', 400), 1024, "INVALID", "TOO_LARGE"));

            Assert.Equal("TOO_LARGE", exception.Message);
            Assert.Equal("preserved", File.ReadAllText(path));
        }
        finally
        {
            DeleteDirectoryBestEffort(folder);
        }
    }

    [Fact]
    public void SettingsStore_OversizedPhysicalFileIsRejectedBeforeJsonParsing()
    {
        var (folder, path) = CreateOversizedFile(
            "viewer-settings.json",
            ViewerStoreFileLimits.SettingsBytes);
        try
        {
            var store = new ViewerSettingsStore(path);

            _ = store.Load();

            Assert.Equal(ViewerSettingsLoadStatus.Corrupt, store.LastLoadStatus);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "viewer-settings.json.corrupt-*"));
        }
        finally
        {
            DeleteDirectoryBestEffort(folder);
        }
    }

    [Fact]
    public void ManagedDeviceStore_OversizedPhysicalFileIsRejectedBeforeDpapiOrJsonWork()
    {
        var (folder, path) = CreateOversizedFile(
            "viewer-devices.json",
            ViewerStoreFileLimits.ManagedDevicesBytes);
        try
        {
            var store = new ManagedDeviceStore(path);

            var result = store.LoadWithStatus();

            Assert.Empty(result.Devices);
            Assert.Equal(ManagedDeviceLoadStatus.Corrupt, result.Status);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "viewer-devices.json.corrupt-*"));
        }
        finally
        {
            DeleteDirectoryBestEffort(folder);
        }
    }

    [Fact]
    public void MonitoringStore_OversizedPhysicalFileIsRejectedBeforeStateAllocation()
    {
        var (folder, path) = CreateOversizedFile(
            "viewer-monitor-state.json",
            ViewerStoreFileLimits.MonitoringStateBytes);
        try
        {
            var store = new ViewerMonitoringStore(path);

            Assert.Equal(ViewerMonitoringLoadStatus.Corrupt, store.LastLoadStatus);
            Assert.False(store.IsOperational);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "viewer-monitor-state.json.corrupt-*"));
        }
        finally
        {
            DeleteDirectoryBestEffort(folder);
        }
    }

    private static (string Folder, string Path) CreateOversizedFile(
        string fileName,
        int maximumBytes)
    {
        var folder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ssw-store-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength((long)maximumBytes + 1);
        return (folder, path);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
