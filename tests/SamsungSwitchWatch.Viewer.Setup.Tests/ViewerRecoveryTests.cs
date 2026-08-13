using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class ViewerRecoveryTests
{
    [Fact]
    public async Task Recover_BeforeBackupMove_KeepsExistingInstallAndCleansStaging()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: false,
            stagingActivated: false);
        Directory.CreateDirectory(journal.StagingDirectory);
        TestWorkspace.Write(
            Path.Combine(journal.StagingDirectory, "partial.tmp"),
            "partial");
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.StagingDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_BackupMoveIntentWithoutMove_KeepsOriginalInstall()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: false) with
        {
            Stage = "backup-move-intent"
        };
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Recover_InvalidInstallPending_RestoresExactOriginalAndDiagnostic()
    {
        using var workspace = new TestWorkspace();
        var journal = CreateInvalidJournal(workspace);
        TestWorkspace.Write(
            Path.Combine(journal.BackupDirectory, "foreign.txt"),
            "restore-me");
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(
            "restore-me",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                "foreign.txt")));
        Assert.False(Directory.Exists(journal.BackupDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(
            ViewerSetupDiagnosticPreviousInstallState.Invalid,
            result.Diagnostic!.PreviousInstallState);
        Assert.Equal(
            ViewerSetupDiagnosticQuarantineState.Restored,
            result.Diagnostic.QuarantineState);
        Assert.Equal(
            ViewerSetupDiagnosticRollbackState.Succeeded,
            result.Diagnostic.RollbackState);
    }

    [Fact]
    public async Task Recover_InvalidInstallPendingWithMissingOriginal_FailsWithDiagnostic()
    {
        using var workspace = new TestWorkspace();
        var journal = CreateInvalidJournal(workspace);
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(
            ViewerSetupDiagnosticPreviousInstallState.Invalid,
            result.Diagnostic!.PreviousInstallState);
        Assert.Equal(
            ViewerSetupDiagnosticQuarantineState.RestoreFailed,
            result.Diagnostic.QuarantineState);
        Assert.Equal(
            ViewerSetupDiagnosticRollbackState.Failed,
            result.Diagnostic.RollbackState);
        Assert.Equal(
            ViewerSetupDiagnosticStage.Recovery,
            result.Diagnostic.FailedStage);
    }

    [Fact]
    public async Task Recover_AfterActivation_IsolatesNewInstallAndRestoresBackup()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            version: "0.11.4-poc",
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.FailedDirectory));
        Assert.False(Directory.Exists(journal.BackupDirectory));
    }

    [Fact]
    public async Task Recover_CorruptedActiveInstall_QuarantinesItAndRestoresValidBackup()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            version: "0.11.4-poc",
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(
            Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName),
            "corrupted-current");

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.FailedDirectory));
        Assert.False(Directory.Exists(journal.BackupDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_FirstInstallCorruptedActiveInstall_QuarantinesAndRemovesIt()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            version: "0.11.4-poc",
            viewerContents: "viewer-new");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: false,
            installMovedToBackup: false,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(
            Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName),
            "corrupted-current");

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.False(Directory.Exists(workspace.InstallDirectory));
        Assert.False(Directory.Exists(journal.FailedDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_CorruptedActiveQuarantineTransientFailure_RetriesAndRestoresBackup()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            version: "0.11.4-poc",
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(
            Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName),
            "corrupted-current");
        var fileSystem = new FaultInjectingViewerSetupFileSystem(workspace.FileSystem)
        {
            MoveFailuresRemaining = 1,
            MoveFailurePredicate = (source, destination) =>
                string.Equals(
                    source,
                    workspace.InstallDirectory,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    destination,
                    journal.FailedDirectory,
                    StringComparison.OrdinalIgnoreCase)
        };

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).RecoverAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(2, fileSystem.MatchingMoveAttempts);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_FirstInstallActivationIntentBeforeMove_CleansStaging()
    {
        using var workspace = new TestWorkspace();
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: false,
            installMovedToBackup: false,
            stagingActivated: true) with
        {
            Stage = "activation-move-intent"
        };
        Directory.CreateDirectory(journal.StagingDirectory);
        TestWorkspace.Write(
            Path.Combine(journal.StagingDirectory, "new-viewer.tmp"),
            "staged");
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(journal.StagingDirectory));
        Assert.False(Directory.Exists(workspace.InstallDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_CommittedCleanupPending_KeepsNewInstallAndOnlyCleansEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "committed",
            NormalLaunchObserved = true
        };
        WriteJournal(workspace, journal);

        var inspection = workspace.CreateOrchestrator().InspectPendingRecovery();
        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(inspection.Exists);
        Assert.True(inspection.CanRecover);
        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-new",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.BackupDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_RollbackRestoredJournal_IsIdempotentCleanupOnly()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "rollback-restored"
        };
        Directory.CreateDirectory(journal.FailedDirectory);
        TestWorkspace.Write(
            Path.Combine(journal.FailedDirectory, "viewer-new.tmp"),
            "new");
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.FailedDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_CorruptedBackup_PreservesRecoveryEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(
            Path.Combine(
                journal.BackupDirectory,
                ViewerSetupConstants.ViewerExecutableName),
            "corrupted-backup");

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        Assert.True(Directory.Exists(journal.BackupDirectory));
        Assert.Equal(
            "viewer-new",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Recover_AmbiguousFailedPathFile_FailsClosedAndPreservesEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(journal.FailedDirectory, "ambiguous-file");

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        Assert.True(Directory.Exists(journal.BackupDirectory));
        Assert.Equal("ambiguous-file", File.ReadAllText(journal.FailedDirectory));
        Assert.Equal(
            "viewer-new",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Recover_AmbiguousFailedDirectory_FailsClosedWithoutDeletingEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(
            Path.Combine(journal.FailedDirectory, "preserve.txt"),
            "recovery-evidence");

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        Assert.True(Directory.Exists(journal.BackupDirectory));
        Assert.Equal(
            "recovery-evidence",
            File.ReadAllText(Path.Combine(journal.FailedDirectory, "preserve.txt")));
        Assert.Equal(
            "viewer-new",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Recover_CommittedCorruptedInstall_DoesNotDeleteBackup()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "committed",
            NormalLaunchObserved = true
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(
            Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName),
            "corrupted-current");

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        Assert.True(Directory.Exists(journal.BackupDirectory));
    }

    [Fact]
    public async Task Recover_MaliciousJournalPath_DoesNotDeleteArbitraryDirectory()
    {
        using var workspace = new TestWorkspace();
        var arbitrary = Path.Combine(workspace.Root, "do-not-delete");
        Directory.CreateDirectory(arbitrary);
        TestWorkspace.Write(Path.Combine(arbitrary, "keep.txt"), "keep");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: false,
            installMovedToBackup: false,
            stagingActivated: false) with
        {
            FailedDirectory = arbitrary
        };
        Directory.CreateDirectory(workspace.OperationsDirectory);
        File.WriteAllText(
            workspace.Paths.JournalPath,
            System.Text.Json.JsonSerializer.Serialize(journal));

        var inspection = workspace.CreateOrchestrator().InspectPendingRecovery();
        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(inspection.Exists);
        Assert.False(inspection.CanRecover);
        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RecoveryRequired, result.Code);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(arbitrary, "keep.txt")));
        Assert.NotNull(inspection.Diagnostic);
        Assert.Equal(
            ViewerSetupDiagnosticJournalState.Unreadable,
            inspection.Diagnostic!.JournalState);
        Assert.Equal(
            ViewerSetupDiagnosticStage.RecoveryGate,
            inspection.Diagnostic.FailedStage);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(
            ViewerSetupDiagnosticJournalState.Unreadable,
            result.Diagnostic!.JournalState);
        Assert.Equal(
            ViewerSetupDiagnosticStage.RecoveryGate,
            result.Diagnostic.FailedStage);
        Assert.Equal(
            ViewerSetupDiagnosticRollbackState.NotRun,
            result.Diagnostic.RollbackState);
    }

    [Fact]
    public async Task Recover_TransactionDeleteTransientFailure_RetriesAndSucceeds()
    {
        using var workspace = new TestWorkspace();
        var (journal, fileSystem) = PrepareRollbackCleanupFault(
            workspace,
            failures: 1);

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).RecoverAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(2, fileSystem.MatchingDeleteAttempts);
        Assert.False(Directory.Exists(journal.FailedDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_TransactionDeletePersistentFailure_IsBoundedAndPreservesJournal()
    {
        using var workspace = new TestWorkspace();
        var (journal, fileSystem) = PrepareRollbackCleanupFault(
            workspace,
            failures: 5);

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).RecoverAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.Equal(5, fileSystem.MatchingDeleteAttempts);
        Assert.True(Directory.Exists(journal.FailedDirectory));
        Assert.True(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_TransactionDeleteCompletesBeforeTransientException_AcceptsAbsence()
    {
        using var workspace = new TestWorkspace();
        var (journal, fileSystem) = PrepareRollbackCleanupFault(
            workspace,
            failures: 1);
        fileSystem.CompleteDeleteBeforeFailure = true;

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).RecoverAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(1, fileSystem.MatchingDeleteAttempts);
        Assert.False(Directory.Exists(journal.FailedDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_CancelledDuringTransactionDeleteRetry_StopsAndPreservesJournal()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var (journal, fileSystem) = PrepareRollbackCleanupFault(
            workspace,
            failures: 5);
        fileSystem.BeforeDeleteFailure = cancellation.Cancel;

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).RecoverAsync(cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.Cancelled, result.Code);
        Assert.Equal(1, fileSystem.MatchingDeleteAttempts);
        Assert.True(Directory.Exists(journal.FailedDirectory));
        Assert.True(File.Exists(workspace.Paths.JournalPath));
    }

    private static (
        ViewerDeploymentJournal Journal,
        FaultInjectingViewerSetupFileSystem FileSystem)
        PrepareRollbackCleanupFault(TestWorkspace workspace, int failures)
    {
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "rollback-restored"
        };
        Directory.CreateDirectory(journal.FailedDirectory);
        TestWorkspace.Write(
            Path.Combine(journal.FailedDirectory, "viewer-new.tmp"),
            "new");
        WriteJournal(workspace, journal);
        var fileSystem = new FaultInjectingViewerSetupFileSystem(
            workspace.FileSystem)
        {
            DeleteFailuresRemaining = failures,
            DeleteFailurePredicate = path => string.Equals(
                path,
                journal.FailedDirectory,
                StringComparison.OrdinalIgnoreCase)
        };
        return (journal, fileSystem);
    }

    private static ViewerDeploymentJournal CreateJournal(
        TestWorkspace workspace,
        bool previousInstallExisted,
        bool installMovedToBackup,
        bool stagingActivated)
    {
        var transactionId = new string('b', 32);
        var transaction = workspace.Paths.CreateTransactionPaths(transactionId);
        var desktop = new ShortcutJournalSnapshot(
            workspace.Paths.DesktopShortcutPath,
            false,
            Path.Combine(transaction.EvidenceDirectory, "desktop.lnk"),
            workspace.Paths.ViewerExecutablePath);
        var start = new ShortcutJournalSnapshot(
            workspace.Paths.StartMenuShortcutPath,
            false,
            Path.Combine(transaction.EvidenceDirectory, "start-menu.lnk"),
            workspace.Paths.ViewerExecutablePath);
        var startup = new ShortcutJournalSnapshot(
            workspace.Paths.StartupShortcutPath,
            false,
            Path.Combine(transaction.EvidenceDirectory, "startup.lnk"),
            workspace.Paths.ViewerExecutablePath);
        var packageManifestSha256 = fileSystemManifestHash(
            workspace.InstallDirectory,
            fallback: new string('c', 64));
        var previousManifestSha256 = previousInstallExisted
            ? fileSystemManifestHash(
                transaction.BackupDirectory,
                fileSystemManifestHash(
                    workspace.InstallDirectory,
                    new string('d', 64)))
            : null;
        return new ViewerDeploymentJournal(
            ViewerDeploymentJournalStore.LegacyFormatVersion,
            transactionId,
            "prepared",
            "0.11.4-poc",
            packageManifestSha256,
            previousManifestSha256,
            transaction.StagingDirectory,
            transaction.BackupDirectory,
            transaction.FailedDirectory,
            transaction.EvidenceDirectory,
            previousInstallExisted,
            installMovedToBackup,
            stagingActivated,
            desktop,
            start,
            startup,
            false,
            false,
            false,
            false);

        static string fileSystemManifestHash(string directory, string fallback)
        {
            var manifest = Path.Combine(directory, ViewerSetupConstants.ManifestFileName);
            return File.Exists(manifest)
                ? TestWorkspace.Hash(manifest)
                : fallback;
        }
    }

    private static ViewerDeploymentJournal CreateInvalidJournal(
        TestWorkspace workspace) =>
        CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: false) with
        {
            FormatVersion = ViewerDeploymentJournalStore.CurrentFormatVersion,
            Stage = "backup-move-intent",
            PreviousManifestSha256 = null,
            PreviousInstallKind = ViewerPreviousInstallKind.Invalid,
            PreviousEmptyInstallDirectory = false
        };

    private static void WriteJournal(
        TestWorkspace workspace,
        ViewerDeploymentJournal journal)
    {
        Directory.CreateDirectory(journal.EvidenceDirectory);
        new ViewerDeploymentJournalStore(workspace.FileSystem, workspace.Paths)
            .Write(journal);
    }
}
