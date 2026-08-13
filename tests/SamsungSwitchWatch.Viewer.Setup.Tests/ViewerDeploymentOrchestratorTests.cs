using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class ViewerDeploymentOrchestratorTests
{
    [Fact]
    public async Task Deploy_InstallsValidatedFiles_PreservesData_AndLeavesPackage()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(
            Path.Combine(workspace.DataDirectory, "viewer-settings.json"),
            "preserve-me");
        TestWorkspace.Write(workspace.Paths.StartupShortcutPath, "owned:legacy-viewer");
        var packageViewer = Path.Combine(
            workspace.PackageDirectory,
            ViewerSetupConstants.ViewerExecutableName);
        var packageHash = TestWorkspace.Hash(packageViewer);

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(ViewerSetupErrorCodes.Ok, result.Code);
        Assert.Equal(
            "preserve-me",
            File.ReadAllText(Path.Combine(
                workspace.DataDirectory,
                "viewer-settings.json")));
        Assert.True(File.Exists(Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.SetupExecutableName)));
        Assert.True(File.Exists(workspace.Paths.DesktopShortcutPath));
        Assert.True(File.Exists(workspace.Paths.StartMenuShortcutPath));
        Assert.False(File.Exists(workspace.Paths.StartupShortcutPath));
        Assert.True(File.Exists(packageViewer));
        Assert.Equal(packageHash, TestWorkspace.Hash(packageViewer));
        Assert.Equal(1, workspace.Process.SmokeCalls);
        Assert.Equal(1, workspace.Process.LaunchCalls);
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Theory]
    [InlineData(ViewerShutdownStatus.Rejected)]
    [InlineData(ViewerShutdownStatus.ProtocolUnsupported)]
    [InlineData(ViewerShutdownStatus.Unavailable)]
    [InlineData(ViewerShutdownStatus.TimedOut)]
    public async Task Deploy_WhenViewerCannotStop_FailsBeforeMutation(
        ViewerShutdownStatus status)
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.Shutdown.Status = status;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.ViewerRunning, result.Code);
        Assert.False(Directory.Exists(workspace.InstallDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
        Assert.Equal(0, workspace.Process.SmokeCalls);
    }

    [Fact]
    public async Task Deploy_WhenSmokeFails_RestoresPreviousInstall()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        workspace.Process.SmokeSucceeds = false;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.SmokeFailed, result.Code);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Deploy_UpgradesExactLegacyViewerInstall()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.CreateLegacyInstalledProduct(viewerContents: "viewer-legacy");

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(
            "viewer-new",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.True(File.Exists(Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.SetupExecutableName)));
    }

    [Fact]
    public async Task Deploy_WhenNormalLaunchFails_RestoresFilesAndShortcuts()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        TestWorkspace.Write(workspace.Paths.DesktopShortcutPath, "owned:old-desktop");
        TestWorkspace.Write(workspace.Paths.StartMenuShortcutPath, "owned:old-start");
        TestWorkspace.Write(workspace.Paths.StartupShortcutPath, "owned:old-startup");
        workspace.Process.LaunchSucceeds = false;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.LaunchFailed, result.Code);
        Assert.Equal("owned:old-desktop", File.ReadAllText(workspace.Paths.DesktopShortcutPath));
        Assert.Equal("owned:old-start", File.ReadAllText(workspace.Paths.StartMenuShortcutPath));
        Assert.Equal("owned:old-startup", File.ReadAllText(workspace.Paths.StartupShortcutPath));
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Deploy_ShortcutFailureIsWarning_NotCoreRollback()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.Shortcuts.FailDesktopCreate = true;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Contains(result.Steps, step =>
            step.Code == ViewerSetupErrorCodes.ShortcutFailed &&
            step.State == ViewerSetupStepState.Warning);
        Assert.True(File.Exists(Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Deploy_ShortcutMutationAndRestoreFailure_PreservesRecoveryEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.Shortcuts.FailDesktopCreate = true;
        workspace.Shortcuts.FailRestore = true;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        var journal = new ViewerDeploymentJournalStore(
            workspace.FileSystem,
            workspace.Paths).Read();
        Assert.True(journal.DesktopShortcutMutated);
        Assert.True(Directory.Exists(journal.EvidenceDirectory));
    }

    [Fact]
    public async Task Deploy_PreservesUnownedShortcuts()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(workspace.Paths.DesktopShortcutPath, "unowned:other.exe");
        TestWorkspace.Write(workspace.Paths.StartMenuShortcutPath, "unowned:other.exe");
        TestWorkspace.Write(workspace.Paths.StartupShortcutPath, "unowned:other.exe");

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("unowned:other.exe", File.ReadAllText(workspace.Paths.DesktopShortcutPath));
        Assert.Equal("unowned:other.exe", File.ReadAllText(workspace.Paths.StartMenuShortcutPath));
        Assert.Equal("unowned:other.exe", File.ReadAllText(workspace.Paths.StartupShortcutPath));
        Assert.Contains(result.Steps, step => step.Code == "SHORTCUT_PRESERVED");
    }

    [Fact]
    public async Task Deploy_QuarantinesUnknownNonEmptyCanonicalInstall()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        var foreign = Path.Combine(workspace.InstallDirectory, "foreign.txt");
        TestWorkspace.Write(foreign, "do-not-touch");

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.False(File.Exists(foreign));
        Assert.Equal(
            "do-not-touch",
            File.ReadAllText(Path.Combine(
                workspace.Paths.QuarantineLatestDirectory,
                "foreign.txt")));
        Assert.True(File.Exists(workspace.Paths.QuarantineLatestMarkerPath));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(
            ViewerSetupDiagnosticPreviousInstallState.Invalid,
            result.Diagnostic!.PreviousInstallState);
        Assert.Equal(
            ViewerSetupDiagnosticQuarantineState.Retained,
            result.Diagnostic.QuarantineState);
    }

    [Fact]
    public async Task Deploy_ExistingInstallClassificationFailure_PreservesPackageDiagnostic()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(workspace.InstallDirectory, "not-a-directory");

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.PathInvalid, result.Code);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(
            ViewerSetupDiagnosticStageState.Succeeded,
            result.Diagnostic!.Stages.Package);
        Assert.Equal(
            ViewerSetupDiagnosticStageState.Failed,
            result.Diagnostic.Stages.ExistingInstall);
        Assert.Equal(
            ViewerSetupDiagnosticStageState.NotRun,
            result.Diagnostic.Stages.Activation);
    }

    [Fact]
    public async Task Deploy_InvalidInstallSmokeFailure_RestoresExactOriginal()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        var foreign = Path.Combine(workspace.InstallDirectory, "foreign.txt");
        TestWorkspace.Write(foreign, "do-not-touch");
        workspace.Process.SmokeSucceeds = false;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.SmokeFailed, result.Code);
        Assert.Equal("do-not-touch", File.ReadAllText(foreign));
        Assert.Single(Directory.EnumerateFiles(workspace.InstallDirectory));
        Assert.False(Directory.Exists(workspace.Paths.QuarantineLatestDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(ViewerSetupDiagnosticCode.SmokeFailed, result.Diagnostic!.PrimaryCode);
        Assert.Equal(
            ViewerSetupDiagnosticRollbackState.Succeeded,
            result.Diagnostic.RollbackState);
        Assert.Equal(
            ViewerSetupDiagnosticQuarantineState.Restored,
            result.Diagnostic.QuarantineState);
    }

    [Fact]
    public async Task Deploy_EmptyExistingDirectory_InstallsSuccessfully()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        Directory.CreateDirectory(workspace.InstallDirectory);

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.True(File.Exists(workspace.Paths.ViewerExecutablePath));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Deploy_EmptyExistingDirectorySmokeFailure_RestoresEmptyDirectory()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        Directory.CreateDirectory(workspace.InstallDirectory);
        workspace.Process.SmokeSucceeds = false;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(workspace.InstallDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.InstallDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Deploy_SecondInvalidInstall_RetainsOnlyNewestOwnedQuarantine()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(
            Path.Combine(workspace.InstallDirectory, "old-invalid.txt"),
            "old-invalid");
        var first = await workspace.CreateOrchestrator().DeployAsync();
        Assert.True(first.Succeeded, $"{first.Code}: {first.Message}");

        Directory.Delete(workspace.InstallDirectory, recursive: true);
        TestWorkspace.Write(
            Path.Combine(workspace.InstallDirectory, "new-invalid.txt"),
            "new-invalid");
        var second = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(second.Succeeded, $"{second.Code}: {second.Message}");
        Assert.False(File.Exists(Path.Combine(
            workspace.Paths.QuarantineLatestDirectory,
            "old-invalid.txt")));
        Assert.Equal("new-invalid", File.ReadAllText(Path.Combine(
            workspace.Paths.QuarantineLatestDirectory,
            "new-invalid.txt")));
        Assert.False(Directory.Exists(workspace.Paths.QuarantinePreviousDirectory));
        Assert.False(File.Exists(workspace.Paths.QuarantinePreviousMarkerPath));
    }

    [Fact]
    public async Task Deploy_QuarantineRotationCleanupFailure_LeavesRecoverableCommittedJournal()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(
            Path.Combine(workspace.InstallDirectory, "old-invalid.txt"),
            "old-invalid");
        var first = await workspace.CreateOrchestrator().DeployAsync();
        Assert.True(first.Succeeded, $"{first.Code}: {first.Message}");

        Directory.Delete(workspace.InstallDirectory, recursive: true);
        TestWorkspace.Write(
            Path.Combine(workspace.InstallDirectory, "new-invalid.txt"),
            "new-invalid");
        var fileSystem = new FaultInjectingViewerSetupFileSystem(workspace.FileSystem)
        {
            DeleteFailuresRemaining = 5,
            DeleteFailurePredicate = path => string.Equals(
                path,
                workspace.Paths.QuarantinePreviousDirectory,
                StringComparison.OrdinalIgnoreCase)
        };

        var second = await workspace.CreateOrchestrator(fileSystem: fileSystem).DeployAsync();

        Assert.True(second.Succeeded, $"{second.Code}: {second.Message}");
        Assert.Contains(second.Steps, step => step.Code == "COMMIT_CLEANUP_PENDING");
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        var journal = new ViewerDeploymentJournalStore(
            workspace.FileSystem,
            workspace.Paths).Read();
        Assert.True(journal.QuarantineLatestToPreviousCompleted);
        Assert.True(journal.QuarantineBackupToLatestCompleted);
        Assert.True(journal.QuarantinePreviousDeleteIntent);
        Assert.False(journal.QuarantinePreviousDeleteCompleted);

        var recovered = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(recovered.Succeeded, $"{recovered.Code}: {recovered.Message}");
        Assert.False(File.Exists(workspace.Paths.JournalPath));
        Assert.False(Directory.Exists(workspace.Paths.QuarantinePreviousDirectory));
        Assert.Equal("new-invalid", File.ReadAllText(Path.Combine(
            workspace.Paths.QuarantineLatestDirectory,
            "new-invalid.txt")));
        Assert.Equal(
            ViewerSetupDiagnosticQuarantineState.Retained,
            recovered.Diagnostic!.QuarantineState);
    }

    [Theory]
    [InlineData("latest-to-previous", false)]
    [InlineData("latest-to-previous", true)]
    [InlineData("backup-to-latest", false)]
    [InlineData("backup-to-latest", true)]
    [InlineData("previous-delete", false)]
    [InlineData("previous-delete", true)]
    public async Task Recover_QuarantineRotationIntent_IsIdempotentAcrossCrashTopology(
        string phase,
        bool mutationCompletedBeforeCrash)
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(
            Path.Combine(workspace.InstallDirectory, "old-invalid.txt"),
            "old-invalid");
        var first = await workspace.CreateOrchestrator().DeployAsync();
        Assert.True(first.Succeeded, $"{first.Code}: {first.Message}");

        Directory.Delete(workspace.InstallDirectory, recursive: true);
        TestWorkspace.Write(
            Path.Combine(workspace.InstallDirectory, "new-invalid.txt"),
            "new-invalid");
        var fileSystem = new FaultInjectingViewerSetupFileSystem(workspace.FileSystem);
        if (phase == "previous-delete")
        {
            fileSystem.DeleteFailuresRemaining = 5;
            fileSystem.DeleteFailurePredicate = path => string.Equals(
                path,
                workspace.Paths.QuarantinePreviousDirectory,
                StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            fileSystem.MoveFailuresRemaining = 5;
            fileSystem.MoveFailurePredicate = phase == "latest-to-previous"
                ? (source, destination) =>
                    string.Equals(
                        source,
                        workspace.Paths.QuarantineLatestDirectory,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        destination,
                        workspace.Paths.QuarantinePreviousDirectory,
                        StringComparison.OrdinalIgnoreCase)
                : (source, destination) =>
                    source.StartsWith(
                        workspace.InstallDirectory + ".__backup_",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        destination,
                        workspace.Paths.QuarantineLatestDirectory,
                        StringComparison.OrdinalIgnoreCase);
        }

        var deployed = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).DeployAsync();

        Assert.True(deployed.Succeeded, $"{deployed.Code}: {deployed.Message}");
        Assert.Contains(deployed.Steps, step => step.Code == "COMMIT_CLEANUP_PENDING");
        var journal = new ViewerDeploymentJournalStore(
            workspace.FileSystem,
            workspace.Paths).Read();
        Assert.True(File.Exists(workspace.Paths.JournalPath));

        switch (phase)
        {
            case "latest-to-previous":
                Assert.True(journal.QuarantineLatestToPreviousIntent);
                Assert.False(journal.QuarantineLatestToPreviousCompleted);
                if (mutationCompletedBeforeCrash)
                {
                    Directory.Move(
                        workspace.Paths.QuarantineLatestDirectory,
                        workspace.Paths.QuarantinePreviousDirectory);
                    File.Move(
                        workspace.Paths.QuarantineLatestMarkerPath,
                        workspace.Paths.QuarantinePreviousMarkerPath);
                }
                break;
            case "backup-to-latest":
                Assert.True(journal.QuarantineLatestToPreviousCompleted);
                Assert.True(journal.QuarantineBackupToLatestIntent);
                Assert.False(journal.QuarantineBackupToLatestCompleted);
                if (mutationCompletedBeforeCrash)
                {
                    Directory.Move(
                        journal.BackupDirectory,
                        workspace.Paths.QuarantineLatestDirectory);
                }
                break;
            case "previous-delete":
                Assert.True(journal.QuarantineBackupToLatestCompleted);
                Assert.True(journal.QuarantinePreviousDeleteIntent);
                Assert.False(journal.QuarantinePreviousDeleteCompleted);
                if (mutationCompletedBeforeCrash)
                {
                    Directory.Delete(
                        workspace.Paths.QuarantinePreviousDirectory,
                        recursive: true);
                    File.Delete(workspace.Paths.QuarantinePreviousMarkerPath);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase));
        }

        var recovered = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(recovered.Succeeded, $"{recovered.Code}: {recovered.Message}");
        Assert.False(File.Exists(workspace.Paths.JournalPath));
        Assert.False(Directory.Exists(workspace.Paths.QuarantinePreviousDirectory));
        Assert.False(File.Exists(workspace.Paths.QuarantinePreviousMarkerPath));
        Assert.Equal("new-invalid", File.ReadAllText(Path.Combine(
            workspace.Paths.QuarantineLatestDirectory,
            "new-invalid.txt")));
        Assert.True(File.Exists(workspace.Paths.QuarantineLatestMarkerPath));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("staging")]
    [InlineData("backup")]
    [InlineData("operations")]
    public async Task Deploy_RejectsManagedPackageSource(string sourceKind)
    {
        using var workspace = new TestWorkspace();
        var transactionId = new string('a', 32);
        var transaction = workspace.Paths.CreateTransactionPaths(transactionId);
        var packageDirectory = sourceKind switch
        {
            "install" => workspace.InstallDirectory,
            "staging" => Path.Combine(transaction.StagingDirectory, "extracted"),
            "backup" => transaction.BackupDirectory,
            "operations" => Path.Combine(workspace.OperationsDirectory, "download"),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind))
        };
        workspace.CreatePackage(packageDirectory);
        var paths = workspace.CreatePaths(packageDirectory);

        var result = await workspace.CreateOrchestrator(paths).DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.PathInvalid, result.Code);
        Assert.True(File.Exists(Path.Combine(
            packageDirectory,
            ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Deploy_ActivationMoveTransientFailure_RetriesAndSucceeds()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        var fileSystem = CreateActivationMoveFault(workspace, failures: 1);

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(2, fileSystem.MatchingMoveAttempts);
        Assert.True(File.Exists(workspace.Paths.ViewerExecutablePath));
    }

    [Fact]
    public async Task Deploy_ActivationMovePersistentFailure_IsBoundedAndRollsBack()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        var fileSystem = CreateActivationMoveFault(workspace, failures: 5);

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.InstallWriteFailed, result.Code);
        Assert.Equal(5, fileSystem.MatchingMoveAttempts);
        Assert.False(Directory.Exists(workspace.InstallDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Deploy_ActivationMoveCompletesBeforeTransientException_AcceptsExactTopology()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        var fileSystem = CreateActivationMoveFault(workspace, failures: 1);
        fileSystem.CompleteMoveBeforeFailure = true;

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(1, fileSystem.MatchingMoveAttempts);
        Assert.True(File.Exists(workspace.Paths.ViewerExecutablePath));
    }

    [Fact]
    public async Task Deploy_CancelledDuringActivationMoveRetry_StopsRetryAndRollsBack()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        using var cancellation = new CancellationTokenSource();
        var fileSystem = CreateActivationMoveFault(workspace, failures: 5);
        fileSystem.BeforeMoveFailure = cancellation.Cancel;

        var result = await workspace.CreateOrchestrator(
            fileSystem: fileSystem).DeployAsync(cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.Cancelled, result.Code);
        Assert.Equal(1, fileSystem.MatchingMoveAttempts);
        Assert.False(Directory.Exists(workspace.InstallDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    private static FaultInjectingViewerSetupFileSystem CreateActivationMoveFault(
        TestWorkspace workspace,
        int failures) =>
        new(workspace.FileSystem)
        {
            MoveFailuresRemaining = failures,
            MoveFailurePredicate = (source, destination) =>
                source.StartsWith(
                    workspace.InstallDirectory + ".__staging_",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    destination,
                    workspace.InstallDirectory,
                    StringComparison.OrdinalIgnoreCase)
        };
}
