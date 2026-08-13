using System.Security;
using SamsungSwitchWatch.Viewer.Setup.Infrastructure;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class PhysicalViewerSetupFileSystemTests
{
    [Fact]
    public void WriteAllTextAtomic_ReplacesExistingFileAndLeavesNoTemporaryFile()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "journal.json");
        File.WriteAllText(path, "old");
        var fileSystem = new PhysicalViewerSetupFileSystem();

        fileSystem.WriteAllTextAtomic(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(
            workspace.Root,
            ".journal.json.*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(SecurityException))]
    public void DeleteTemporaryFileBestEffort_DoesNotMaskScannerCleanupFailure(
        Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var result = Record.Exception(() =>
            PhysicalViewerSetupFileSystem.DeleteTemporaryFileBestEffort(
                "temporary.tmp",
                _ => true,
                _ => throw exception));

        Assert.Null(result);
    }

    [Fact]
    public void DeleteTemporaryFileBestEffort_DoesNotDeleteWhenTemporaryIsAbsent()
    {
        var deleteCalled = false;

        PhysicalViewerSetupFileSystem.DeleteTemporaryFileBestEffort(
            "temporary.tmp",
            _ => false,
            _ => deleteCalled = true);

        Assert.False(deleteCalled);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(SecurityException))]
    public void FlushCommittedFileBestEffort_DoesNotReportCommittedWriteAsFailed(
        Type exceptionType)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "committed.json");
        File.WriteAllText(path, "committed");
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var result = Record.Exception(() =>
            PhysicalViewerSetupFileSystem.FlushCommittedFileBestEffort(
                path,
                _ => throw exception));

        Assert.Null(result);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    public void FlushCommittedFileBestEffort_MissingDestinationRemainsFailure(
        Type exceptionType)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "missing.json");
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Throws(exceptionType, () =>
            PhysicalViewerSetupFileSystem.FlushCommittedFileBestEffort(
                path,
                _ => throw exception));
    }

    [Fact]
    public void ReadUtf8TextBounded_AcceptsUtf8BomWithoutLeakingItIntoJson()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "manifest.json");
        File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, (byte)'{', (byte)'}']);

        var result = PhysicalViewerSetupFileSystem.ReadUtf8TextBounded(path, 1024);

        Assert.Equal("{}", result);
    }
}
