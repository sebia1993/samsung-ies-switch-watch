using System.Text.Json;
using SamsungSwitchWatch.Viewer.Setup.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Setup.Deployment;

public sealed class ViewerDeploymentOrchestrator(
    IViewerPackageValidator packageValidator,
    IViewerSetupFileSystem fileSystem,
    IViewerProcessManager processManager,
    IViewerShutdownCoordinator shutdownCoordinator,
    IViewerShortcutManager shortcutManager,
    IViewerDeploymentLock deploymentLock,
    ViewerSetupPaths paths)
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly TimeSpan ViewerShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LaunchLivenessWindow = TimeSpan.FromSeconds(2);
    private const int DirectoryMutationMaxAttempts = 5;
    private const int MaximumQuarantineMarkerBytes = 64 * 1024;
    private static readonly TimeSpan DirectoryMutationRetryDelay =
        TimeSpan.FromMilliseconds(250);

    public async Task<ViewerSetupResult> DeployAsync(
        CancellationToken cancellationToken = default)
    {
        var steps = new ViewerSetupStepRecorder();
        var diagnostic = new DeploymentDiagnosticState(
            ViewerSetupDiagnosticOperation.Install);
        var gateEntered = false;
        IDisposable? lease = null;
        try
        {
            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Lock;
            await ProcessGate.WaitAsync(cancellationToken);
            gateEntered = true;
            lease = deploymentLock.Acquire();
            return AttachDiagnostic(
                await DeployCoreAsync(steps, diagnostic, cancellationToken),
                diagnostic);
        }
        catch (OperationCanceledException)
        {
            steps.Failed(
                ViewerSetupErrorCodes.Cancelled,
                "Viewer 설치",
                "Viewer 설치가 취소되었습니다.");
            diagnostic.PrimaryCode ??= ViewerSetupErrorCodes.Cancelled;
            return AttachDiagnostic(Failure(
                ViewerSetupErrorCodes.Cancelled,
                "Viewer 설치가 취소되었습니다.",
                steps), diagnostic);
        }
        catch (ViewerSetupException exception)
        {
            steps.Failed(exception.Code, "Viewer 설치", exception.Message);
            diagnostic.PrimaryCode ??= exception.Code;
            return AttachDiagnostic(
                Failure(exception.Code, exception.Message, steps),
                diagnostic);
        }
        catch
        {
            steps.Failed(
                ViewerSetupErrorCodes.Unexpected,
                "Viewer 설치",
                "예상하지 못한 Windows 오류로 Viewer를 설치하지 못했습니다.");
            diagnostic.PrimaryCode ??= ViewerSetupErrorCodes.Unexpected;
            return AttachDiagnostic(Failure(
                ViewerSetupErrorCodes.Unexpected,
                "예상하지 못한 Windows 오류로 Viewer를 설치하지 못했습니다.",
                steps), diagnostic);
        }
        finally
        {
            lease?.Dispose();
            if (gateEntered)
            {
                ProcessGate.Release();
            }
        }
    }

    public ViewerRecoveryInspection InspectPendingRecovery()
    {
        var store = new ViewerDeploymentJournalStore(fileSystem, paths);
        if (!store.Exists)
        {
            return ViewerRecoveryInspection.None;
        }

        try
        {
            var journal = store.Read();
            return new ViewerRecoveryInspection(
                true,
                true,
                ViewerSetupErrorCodes.RecoveryRequired,
                journal.NormalLaunchObserved ||
                string.Equals(journal.Stage, "committed", StringComparison.Ordinal)
                    ? "이전 설치의 정리 작업이 남아 있습니다. 이전 상태 복구를 실행하세요."
                    : "완료되지 않은 Viewer 설치가 있습니다. 이전 상태 복구를 먼저 실행하세요.")
            {
                Diagnostic = RecoveryInspectionDiagnostic(
                    journal,
                    ViewerSetupDiagnosticJournalState.Recoverable,
                    ViewerSetupDiagnosticStage.RecoveryGate)
            };
        }
        catch (ViewerSetupException exception)
        {
            return new ViewerRecoveryInspection(
                true,
                false,
                ViewerSetupErrorCodes.RecoveryRequired,
                exception.Message)
            {
                Diagnostic = RecoveryInspectionDiagnostic(
                    null,
                    ViewerSetupDiagnosticJournalState.Unreadable,
                    ViewerSetupDiagnosticStage.RecoveryGate)
            };
        }
        catch
        {
            return new ViewerRecoveryInspection(
                true,
                false,
                ViewerSetupErrorCodes.RecoveryRequired,
                "이전 Viewer 설치 작업을 안전하게 확인할 수 없습니다.")
            {
                Diagnostic = RecoveryInspectionDiagnostic(
                    null,
                    ViewerSetupDiagnosticJournalState.Unreadable,
                    ViewerSetupDiagnosticStage.RecoveryGate)
            };
        }
    }

    public async Task<ViewerSetupResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var steps = new ViewerSetupStepRecorder();
        var diagnostic = new DeploymentDiagnosticState(
            ViewerSetupDiagnosticOperation.Recovery)
        {
            ActiveStage = ViewerSetupDiagnosticStage.RecoveryGate,
            JournalState = ViewerSetupDiagnosticJournalState.None
        };
        var gateEntered = false;
        IDisposable? lease = null;
        try
        {
            await ProcessGate.WaitAsync(cancellationToken);
            gateEntered = true;
            lease = deploymentLock.Acquire();

            var store = new ViewerDeploymentJournalStore(fileSystem, paths);
            if (!store.Exists)
            {
                steps.Succeeded(
                    "RECOVERY_NOT_REQUIRED",
                    "이전 상태 복구",
                    "복구가 필요한 이전 설치 작업이 없습니다.");
                diagnostic.JournalState = ViewerSetupDiagnosticJournalState.None;
                return AttachDiagnostic(ViewerSetupResult.Success(
                    "복구가 필요한 이전 설치 작업이 없습니다.",
                    steps), diagnostic);
            }

            ViewerDeploymentJournal journal;
            try
            {
                journal = store.Read();
            }
            catch
            {
                diagnostic.JournalState = ViewerSetupDiagnosticJournalState.Unreadable;
                throw;
            }
            diagnostic.JournalState = ViewerSetupDiagnosticJournalState.Recoverable;
            diagnostic.ProductVersion = journal.PackageVersion;
            diagnostic.PreviousInstallKind = EffectivePreviousInstallKind(journal);
            diagnostic.QuarantineState = QuarantineStateFor(journal);
            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Recovery;
            await RecoverJournalAsync(store, journal, steps, cancellationToken);
            steps.Succeeded(
                "RECOVERY_COMPLETED",
                "이전 상태 복구",
                "이전 설치 상태 복구가 완료되었습니다.");
            diagnostic.RollbackState = ViewerSetupDiagnosticRollbackState.Succeeded;
            diagnostic.JournalState = ViewerSetupDiagnosticJournalState.None;
            if (diagnostic.QuarantineState ==
                ViewerSetupDiagnosticQuarantineState.IsolatedPending)
            {
                diagnostic.QuarantineState =
                    ViewerSetupDiagnosticQuarantineState.Restored;
            }
            return AttachDiagnostic(ViewerSetupResult.Success(
                "이전 설치 상태 복구가 완료되었습니다.",
                steps), diagnostic);
        }
        catch (OperationCanceledException)
        {
            steps.Failed(
                ViewerSetupErrorCodes.Cancelled,
                "이전 상태 복구",
                "Viewer 복구가 취소되었습니다.");
            diagnostic.PrimaryCode ??= ViewerSetupErrorCodes.Cancelled;
            return AttachDiagnostic(Failure(
                ViewerSetupErrorCodes.Cancelled,
                "Viewer 복구가 취소되었습니다.",
                steps), diagnostic);
        }
        catch (ViewerSetupException exception)
        {
            steps.Failed(exception.Code, "이전 상태 복구", exception.Message);
            diagnostic.PrimaryCode ??= exception.Code;
            if (diagnostic.ActiveStage == ViewerSetupDiagnosticStage.Recovery)
            {
                diagnostic.RollbackState = ViewerSetupDiagnosticRollbackState.Failed;
                if (diagnostic.QuarantineState ==
                    ViewerSetupDiagnosticQuarantineState.IsolatedPending)
                {
                    diagnostic.QuarantineState =
                        ViewerSetupDiagnosticQuarantineState.RestoreFailed;
                }
            }
            return AttachDiagnostic(
                Failure(exception.Code, exception.Message, steps),
                diagnostic);
        }
        catch
        {
            steps.Failed(
                ViewerSetupErrorCodes.RollbackFailed,
                "이전 상태 복구",
                "이전 Viewer 설치 상태를 완전히 복구하지 못했습니다.");
            diagnostic.PrimaryCode ??= ViewerSetupErrorCodes.RollbackFailed;
            if (diagnostic.ActiveStage == ViewerSetupDiagnosticStage.Recovery)
            {
                diagnostic.RollbackState = ViewerSetupDiagnosticRollbackState.Failed;
                if (diagnostic.QuarantineState ==
                    ViewerSetupDiagnosticQuarantineState.IsolatedPending)
                {
                    diagnostic.QuarantineState =
                        ViewerSetupDiagnosticQuarantineState.RestoreFailed;
                }
            }
            return AttachDiagnostic(Failure(
                ViewerSetupErrorCodes.RollbackFailed,
                "이전 Viewer 설치 상태를 완전히 복구하지 못했습니다.",
                steps), diagnostic);
        }
        finally
        {
            lease?.Dispose();
            if (gateEntered)
            {
                ProcessGate.Release();
            }
        }
    }

    private async Task<ViewerSetupResult> DeployCoreAsync(
        ViewerSetupStepRecorder steps,
        DeploymentDiagnosticState diagnostic,
        CancellationToken cancellationToken)
    {
        var store = new ViewerDeploymentJournalStore(fileSystem, paths);
        var ownsJournal = false;
        ViewerDeploymentJournal? journal = null;
        try
        {
            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.RecoveryGate;
            if (store.Exists)
            {
                diagnostic.JournalState = ViewerSetupDiagnosticJournalState.Recoverable;
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RecoveryRequired,
                    "완료되지 않은 Viewer 설치가 있습니다. 이전 상태 복구를 먼저 실행하세요.");
            }

            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Path;
            ValidateBasePaths();
            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Package;
            var package = packageValidator.Validate(paths.PackageDirectory);
            diagnostic.ProductVersion = package.Version;
            diagnostic.PackageState = ViewerSetupDiagnosticStageState.Succeeded;
            steps.Succeeded(
                "PACKAGE_VALID",
                "패키지 확인",
                $"Viewer {package.Version} 파일 무결성이 정상입니다.");

            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Backup;
            var existingInstall = ClassifyExistingInstallation();
            diagnostic.PreviousInstallKind = existingInstall.Kind;
            diagnostic.ExistingInstallState = ViewerSetupDiagnosticStageState.Succeeded;
            diagnostic.QuarantineState = existingInstall.Kind == ViewerPreviousInstallKind.Invalid
                ? ViewerSetupDiagnosticQuarantineState.NotRun
                : ViewerSetupDiagnosticQuarantineState.NotRequired;

            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Shutdown;
            var shutdown = await shutdownCoordinator.EnsureStoppedAsync(
                ViewerShutdownTimeout,
                cancellationToken);
            if (!shutdown.Succeeded)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.ViewerRunning,
                    ShutdownMessage(shutdown.Status));
            }

            steps.Succeeded(
                "VIEWER_STOPPED",
                "실행 상태 확인",
                shutdown.Status == ViewerShutdownStatus.Stopped
                    ? "실행 중이던 Viewer가 안전하게 종료되었습니다."
                    : "실행 중인 Viewer가 없습니다.");
            diagnostic.ShutdownState = ViewerSetupDiagnosticStageState.Succeeded;

            var installParent = Path.GetDirectoryName(paths.InstallDirectory) ??
                                throw new ViewerSetupException(
                                    ViewerSetupErrorCodes.PathInvalid,
                                    "Viewer 설치 경로를 확인할 수 없습니다.");
            fileSystem.EnsureDirectoryWritable(installParent);
            fileSystem.EnsureDirectoryWritable(paths.OperationsDirectory);

            var transactionId = Guid.NewGuid().ToString("N");
            var transaction = paths.CreateTransactionPaths(transactionId);
            ViewerSetupPathGuard.ValidateTransactionPaths(
                paths,
                transactionId,
                transaction);
            EnsureTransactionTargetsAbsent(transaction);

            var desktopSnapshot = NewSnapshot(
                paths.DesktopShortcutPath,
                Path.Combine(transaction.EvidenceDirectory, "desktop.lnk"));
            var startMenuSnapshot = NewSnapshot(
                paths.StartMenuShortcutPath,
                Path.Combine(transaction.EvidenceDirectory, "start-menu.lnk"));
            var startupSnapshot = NewSnapshot(
                paths.StartupShortcutPath,
                Path.Combine(transaction.EvidenceDirectory, "startup.lnk"));

            journal = new ViewerDeploymentJournal(
                ViewerDeploymentJournalStore.CurrentFormatVersion,
                transactionId,
                "prepared",
                package.Version,
                package.ManifestSha256,
                existingInstall.Package?.ManifestSha256,
                transaction.StagingDirectory,
                transaction.BackupDirectory,
                transaction.FailedDirectory,
                transaction.EvidenceDirectory,
                existingInstall.Kind != ViewerPreviousInstallKind.None ||
                    existingInstall.EmptyDirectoryExisted,
                false,
                false,
                desktopSnapshot,
                startMenuSnapshot,
                startupSnapshot,
                false,
                false,
                false,
                false,
                existingInstall.Kind,
                existingInstall.EmptyDirectoryExisted);
            store.Write(journal);
            ownsJournal = true;
            diagnostic.JournalState = ViewerSetupDiagnosticJournalState.Recoverable;

            fileSystem.CreateDirectory(transaction.EvidenceDirectory);
            desktopSnapshot = shortcutManager.Capture(
                desktopSnapshot.ShortcutPath,
                desktopSnapshot.BackupFilePath,
                desktopSnapshot.ExpectedTargetPath);
            startMenuSnapshot = shortcutManager.Capture(
                startMenuSnapshot.ShortcutPath,
                startMenuSnapshot.BackupFilePath,
                startMenuSnapshot.ExpectedTargetPath);
            startupSnapshot = shortcutManager.Capture(
                startupSnapshot.ShortcutPath,
                startupSnapshot.BackupFilePath,
                startupSnapshot.ExpectedTargetPath);
            journal = journal with
            {
                Stage = "shortcut-snapshots-captured",
                DesktopShortcut = desktopSnapshot,
                StartMenuShortcut = startMenuSnapshot,
                StartupShortcut = startupSnapshot
            };
            store.Write(journal);

            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Staging;
            StagePackage(package, transaction.StagingDirectory);
            diagnostic.StagingState = ViewerSetupDiagnosticStageState.Succeeded;
            journal = journal with { Stage = "package-staged" };
            store.Write(journal);
            steps.Succeeded(
                "PACKAGE_STAGED",
                "파일 준비",
                "검증된 Viewer 파일을 별도 작업 폴더에 준비했습니다.");

            if (journal.PreviousEmptyInstallDirectory)
            {
                RemoveRecordedEmptyInstallDirectory();
            }

            journal = journal with { Stage = "activation-started" };
            store.Write(journal);
            if (journal.PreviousInstallKind is
                ViewerPreviousInstallKind.Verified or
                ViewerPreviousInstallKind.Invalid)
            {
                diagnostic.ActiveStage = journal.PreviousInstallKind == ViewerPreviousInstallKind.Invalid
                    ? ViewerSetupDiagnosticStage.Quarantine
                    : ViewerSetupDiagnosticStage.Backup;
                journal = journal with
                {
                    Stage = "backup-move-intent",
                    InstallMovedToBackup = true
                };
                store.Write(journal);
                await MoveTransactionDirectoryAsync(
                    paths.InstallDirectory,
                    transaction.BackupDirectory,
                    journal.PreviousInstallKind == ViewerPreviousInstallKind.Invalid
                        ? ViewerSetupErrorCodes.QuarantineFailed
                        : ViewerSetupErrorCodes.InstallWriteFailed,
                    journal.PreviousInstallKind == ViewerPreviousInstallKind.Invalid
                        ? "기존 Viewer 폴더를 새 설치 전 임시 격리하지 못했습니다."
                        : "Viewer 기존 파일을 안전하게 백업하지 못했습니다.",
                    cancellationToken);
                if (journal.PreviousInstallKind == ViewerPreviousInstallKind.Verified)
                {
                    ValidateExpectedInstallation(
                        transaction.BackupDirectory,
                        journal.PreviousManifestSha256!,
                        allowLegacy: true,
                        ViewerSetupErrorCodes.RollbackFailed);
                }
                else
                {
                    ValidateUntrustedBackupTopology(journal);
                    diagnostic.QuarantineState =
                        ViewerSetupDiagnosticQuarantineState.IsolatedPending;
                    steps.Succeeded(
                        "EXISTING_INSTALL_QUARANTINED",
                        "기존 설치 보관",
                        "검증되지 않은 기존 Viewer 폴더를 새 설치가 확인될 때까지 보관했습니다.");
                }
            }

            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Activation;
            journal = journal with
            {
                Stage = "activation-move-intent",
                StagingActivated = true
            };
            store.Write(journal);
            if (journal.PreviousInstallKind == ViewerPreviousInstallKind.None &&
                (fileSystem.DirectoryExists(paths.InstallDirectory) ||
                 fileSystem.FileExists(paths.InstallDirectory)))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.PathInvalid,
                    "Viewer 설치 폴더가 설치 중 외부 작업으로 변경되었습니다.");
            }
            await MoveTransactionDirectoryAsync(
                transaction.StagingDirectory,
                paths.InstallDirectory,
                ViewerSetupErrorCodes.InstallWriteFailed,
                "Viewer 파일을 안전하게 교체하지 못했습니다.",
                cancellationToken);
            ValidateExpectedInstallation(
                paths.InstallDirectory,
                journal.PackageManifestSha256,
                allowLegacy: false,
                ViewerSetupErrorCodes.InstallWriteFailed);
            journal = journal with
            {
                Stage = "files-activated"
            };
            store.Write(journal);
            diagnostic.ActivationState = ViewerSetupDiagnosticStageState.Succeeded;

            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Smoke;
            var smoke = await processManager.RunSmokeCheckAsync(
                paths.ViewerExecutablePath,
                SmokeTimeout,
                cancellationToken);
            if (!smoke.Succeeded)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.SmokeFailed,
                    "설치된 Viewer의 화면 리소스 사전 점검에 실패했습니다.");
            }

            journal = journal with { Stage = "smoke-passed" };
            store.Write(journal);
            diagnostic.SmokeState = ViewerSetupDiagnosticStageState.Succeeded;
            steps.Succeeded(
                "SMOKE_PASSED",
                "Viewer 사전 점검",
                "설치된 Viewer의 화면 리소스 점검을 통과했습니다.");

            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Shortcut;
            journal = ConfigureShortcuts(
                store,
                journal,
                steps,
                out var shortcutRecoveryFailed);
            if (shortcutRecoveryFailed)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.ShortcutFailed,
                    "바로가기 복구를 완료하지 못해 Viewer 설치를 중단했습니다.");
            }
            diagnostic.ShortcutState = ViewerSetupDiagnosticStageState.Succeeded;

            diagnostic.ActiveStage = ViewerSetupDiagnosticStage.Launch;
            journal = journal with { Stage = "normal-launch-started" };
            store.Write(journal);
            var launch = await processManager.LaunchAndVerifyAsync(
                paths.ViewerExecutablePath,
                LaunchLivenessWindow,
                cancellationToken);
            if (!launch.Succeeded)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.LaunchFailed,
                    "설치된 Viewer가 정상 실행 상태를 유지하지 못했습니다.");
            }

            journal = journal with
            {
                Stage = "committed",
                NormalLaunchObserved = true
            };
            store.Write(journal);
            steps.Succeeded(
                "VIEWER_LAUNCHED",
                "Viewer 실행",
                "설치된 Viewer가 정상적으로 실행되었습니다.");

            try
            {
                diagnostic.ActiveStage = ViewerSetupDiagnosticStage.CommitCleanup;
                await CleanupCommittedTransactionAsync(
                    store,
                    journal,
                    cancellationToken);
                diagnostic.JournalState = ViewerSetupDiagnosticJournalState.None;
                if (diagnostic.PreviousInstallKind == ViewerPreviousInstallKind.Invalid)
                {
                    diagnostic.QuarantineState =
                        ViewerSetupDiagnosticQuarantineState.Retained;
                }
            }
            catch
            {
                if (diagnostic.PreviousInstallKind == ViewerPreviousInstallKind.Invalid)
                {
                    diagnostic.QuarantineState =
                        ViewerSetupDiagnosticQuarantineState.FinalizationPending;
                }
                steps.Warning(
                    "COMMIT_CLEANUP_PENDING",
                    "설치 정리",
                    "Viewer는 실행 중이지만 이전 설치의 정리 작업이 남았습니다. 다음 실행에서 이전 상태 복구를 선택하세요.");
            }

            return ViewerSetupResult.Success(
                "Viewer 설치 또는 업데이트가 완료되었습니다.",
                steps);
        }
        catch (Exception primaryException)
        {
            diagnostic.PrimaryCode ??= primaryException switch
            {
                ViewerSetupException setupException => setupException.Code,
                OperationCanceledException => ViewerSetupErrorCodes.Cancelled,
                _ => ViewerSetupErrorCodes.Unexpected
            };
            if (ownsJournal && journal is not null && !journal.NormalLaunchObserved)
            {
                try
                {
                    await RecoverJournalAsync(
                        store,
                        store.Exists ? store.Read() : journal,
                        steps,
                        CancellationToken.None);
                    diagnostic.RollbackState =
                        ViewerSetupDiagnosticRollbackState.Succeeded;
                    diagnostic.JournalState = ViewerSetupDiagnosticJournalState.None;
                    if (diagnostic.QuarantineState ==
                        ViewerSetupDiagnosticQuarantineState.IsolatedPending)
                    {
                        diagnostic.QuarantineState =
                            ViewerSetupDiagnosticQuarantineState.Restored;
                    }
                }
                catch (Exception rollbackException)
                {
                    diagnostic.RollbackState =
                        ViewerSetupDiagnosticRollbackState.Failed;
                    diagnostic.JournalState =
                        ViewerSetupDiagnosticJournalState.Recoverable;
                    if (diagnostic.QuarantineState ==
                        ViewerSetupDiagnosticQuarantineState.IsolatedPending)
                    {
                        diagnostic.QuarantineState =
                            ViewerSetupDiagnosticQuarantineState.RestoreFailed;
                    }
                    throw new ViewerSetupException(
                        ViewerSetupErrorCodes.RollbackFailed,
                        "이전 Viewer 설치 상태를 완전히 복구하지 못했습니다.",
                        rollbackException);
                }
            }

            throw;
        }
    }

    private async Task RecoverJournalAsync(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        ViewerSetupStepRecorder steps,
        CancellationToken cancellationToken)
    {
        ViewerSetupPathGuard.ValidateJournal(paths, journal);
        if (journal.NormalLaunchObserved ||
            string.Equals(journal.Stage, "committed", StringComparison.Ordinal))
        {
            await CleanupCommittedTransactionAsync(
                store,
                journal,
                cancellationToken);
            return;
        }

        if (string.Equals(
                journal.Stage,
                "rollback-restored",
                StringComparison.Ordinal))
        {
            ValidateRestoredInstallation(journal);
            await CleanupTransactionArtifactsAsync(
                store,
                journal,
                cancellationToken);
            return;
        }

        if (journal.StagingActivated || journal.InstallMovedToBackup)
        {
            var shutdown = await shutdownCoordinator.EnsureStoppedAsync(
                ViewerShutdownTimeout,
                cancellationToken);
            if (!shutdown.Succeeded)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.ViewerRunning,
                    ShutdownMessage(shutdown.Status));
            }
        }

        var backupExists = fileSystem.DirectoryExists(journal.BackupDirectory);
        var installExists = fileSystem.DirectoryExists(paths.InstallDirectory);
        if (fileSystem.FileExists(journal.BackupDirectory) ||
            fileSystem.FileExists(paths.InstallDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.RollbackFailed,
                "Viewer 설치 또는 백업 경로가 폴더가 아니어서 복구를 중단했습니다.");
        }

        if (journal.FormatVersion == ViewerDeploymentJournalStore.CurrentFormatVersion &&
            journal.PreviousInstallKind == ViewerPreviousInstallKind.None &&
            journal.PreviousEmptyInstallDirectory)
        {
            await RestoreEmptyExistingInstallAsync(
                journal,
                installExists,
                cancellationToken);
            RestoreShortcutIfMutated(
                journal.DesktopShortcutMutated,
                journal.DesktopShortcut);
            RestoreShortcutIfMutated(
                journal.StartMenuShortcutMutated,
                journal.StartMenuShortcut);
            RestoreShortcutIfMutated(
                journal.StartupShortcutMutated,
                journal.StartupShortcut);
            journal = journal with
            {
                Stage = "rollback-restored",
                DesktopShortcutMutated = false,
                StartMenuShortcutMutated = false,
                StartupShortcutMutated = false
            };
            store.Write(journal);
            await CleanupTransactionArtifactsAsync(store, journal, cancellationToken);
            steps.Succeeded(
                "ROLLBACK_COMPLETED",
                "자동 복구",
                "설치 전 비어 있던 Viewer 폴더를 복구했습니다.");
            return;
        }

        if (journal.FormatVersion == ViewerDeploymentJournalStore.CurrentFormatVersion &&
            journal.PreviousInstallKind == ViewerPreviousInstallKind.Invalid)
        {
            await RestoreInvalidExistingInstallAsync(
                journal,
                backupExists,
                installExists,
                cancellationToken);
            RestoreShortcutIfMutated(
                journal.DesktopShortcutMutated,
                journal.DesktopShortcut);
            RestoreShortcutIfMutated(
                journal.StartMenuShortcutMutated,
                journal.StartMenuShortcut);
            RestoreShortcutIfMutated(
                journal.StartupShortcutMutated,
                journal.StartupShortcut);
            journal = journal with
            {
                Stage = "rollback-restored",
                DesktopShortcutMutated = false,
                StartMenuShortcutMutated = false,
                StartupShortcutMutated = false
            };
            store.Write(journal);
            await CleanupTransactionArtifactsAsync(
                store,
                journal,
                cancellationToken);
            steps.Succeeded(
                "ROLLBACK_COMPLETED",
                "자동 복구",
                "설치 전 Viewer 폴더와 바로가기를 복구했습니다.");
            return;
        }

        if (backupExists)
        {
            ValidateExpectedInstallation(
                journal.BackupDirectory,
                journal.PreviousManifestSha256!,
                allowLegacy: true,
                ViewerSetupErrorCodes.RollbackFailed);
            if (installExists)
            {
                if (fileSystem.DirectoryExists(journal.FailedDirectory) ||
                    fileSystem.FileExists(journal.FailedDirectory))
                {
                    throw new ViewerSetupException(
                        ViewerSetupErrorCodes.RollbackFailed,
                        "Viewer 복구 폴더와 현재 설치가 동시에 남아 있어 복구를 중단했습니다.");
                }

                await MoveTransactionDirectoryAsync(
                    paths.InstallDirectory,
                    journal.FailedDirectory,
                    ViewerSetupErrorCodes.RollbackFailed,
                    "새 Viewer 파일을 복구 영역으로 이동하지 못했습니다.",
                    cancellationToken);
            }

            await MoveTransactionDirectoryAsync(
                journal.BackupDirectory,
                paths.InstallDirectory,
                ViewerSetupErrorCodes.RollbackFailed,
                "이전 Viewer 파일을 안전하게 복구하지 못했습니다.",
                cancellationToken);
            ValidateExpectedInstallation(
                paths.InstallDirectory,
                journal.PreviousManifestSha256!,
                allowLegacy: true,
                ViewerSetupErrorCodes.RollbackFailed);
        }
        else if (journal.PreviousInstallExisted)
        {
            var stagedFilesRemain =
                fileSystem.DirectoryExists(journal.StagingDirectory);
            var isolatedNewInstallRemains =
                fileSystem.DirectoryExists(journal.FailedDirectory);
            if (!installExists ||
                journal.StagingActivated &&
                !stagedFilesRemain &&
                !isolatedNewInstallRemains)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "이전 Viewer 파일 백업 상태를 확인할 수 없어 복구를 중단했습니다.");
            }
            // The backup-move intent was persisted before the move. When the
            // backup is absent and activation never began, the original install
            // is still in place and must not be touched.
            ValidateExpectedInstallation(
                paths.InstallDirectory,
                journal.PreviousManifestSha256!,
                allowLegacy: true,
                ViewerSetupErrorCodes.RollbackFailed);
        }
        else if (journal.StagingActivated && installExists)
        {
            if (fileSystem.DirectoryExists(journal.FailedDirectory) ||
                fileSystem.FileExists(journal.FailedDirectory))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "Viewer 복구 폴더와 현재 설치가 동시에 남아 있어 복구를 중단했습니다.");
            }

            await MoveTransactionDirectoryAsync(
                paths.InstallDirectory,
                journal.FailedDirectory,
                ViewerSetupErrorCodes.RollbackFailed,
                "새 Viewer 파일을 복구 영역으로 이동하지 못했습니다.",
                cancellationToken);
        }

        RestoreShortcutIfMutated(
            journal.DesktopShortcutMutated,
            journal.DesktopShortcut);
        RestoreShortcutIfMutated(
            journal.StartMenuShortcutMutated,
            journal.StartMenuShortcut);
        RestoreShortcutIfMutated(
            journal.StartupShortcutMutated,
            journal.StartupShortcut);

        journal = journal with
        {
            Stage = "rollback-restored",
            DesktopShortcutMutated = false,
            StartMenuShortcutMutated = false,
            StartupShortcutMutated = false
        };
        store.Write(journal);

        await DeleteTransactionDirectoryAsync(
            journal.StagingDirectory,
            cancellationToken);
        await DeleteTransactionDirectoryAsync(
            journal.FailedDirectory,
            cancellationToken);
        if (fileSystem.DirectoryExists(journal.BackupDirectory))
        {
            // A backup remains only when no move was necessary. It is still a
            // validated product transaction path, never the extraction folder.
            await DeleteTransactionDirectoryAsync(
                journal.BackupDirectory,
                cancellationToken);
        }

        await DeleteTransactionDirectoryAsync(
            journal.EvidenceDirectory,
            cancellationToken);
        store.Delete();
        steps.Succeeded(
            "ROLLBACK_COMPLETED",
            "자동 복구",
            "설치 전 Viewer 파일과 바로가기를 복구했습니다.");
    }

    private void StagePackage(ViewerPackage package, string stagingDirectory)
    {
        fileSystem.CreateDirectory(stagingDirectory);
        foreach (var packageFile in package.InstallFiles)
        {
            var destination = Path.Combine(stagingDirectory, packageFile.Name);
            fileSystem.CopyFile(packageFile.SourcePath, destination, overwrite: false);
            if (fileSystem.GetFileLength(destination) != packageFile.Size ||
                !string.Equals(
                    fileSystem.ComputeSha256(destination),
                    packageFile.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.InstallWriteFailed,
                    "Viewer 파일을 안전하게 준비하지 못했습니다.");
            }
        }

        fileSystem.CopyFile(
            package.ManifestPath,
            Path.Combine(stagingDirectory, ViewerSetupConstants.ManifestFileName),
            overwrite: false);
        var stagedManifest = Path.Combine(
            stagingDirectory,
            ViewerSetupConstants.ManifestFileName);
        if (!string.Equals(
                fileSystem.ComputeSha256(stagedManifest),
                package.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.InstallWriteFailed,
                "Viewer 빌드 정보를 안전하게 준비하지 못했습니다.");
        }
    }

    private ViewerDeploymentJournal ConfigureShortcuts(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        ViewerSetupStepRecorder steps,
        out bool recoveryFailed)
    {
        recoveryFailed = false;
        journal = ConfigureShortcut(
            store,
            journal,
            journal.DesktopShortcut,
            "desktop",
            current => current with { DesktopShortcutMutated = true },
            current => current with { DesktopShortcutMutated = false },
            () => shortcutManager.Create(
                paths.DesktopShortcutPath,
                paths.ViewerExecutablePath,
                paths.InstallDirectory),
            steps,
            ref recoveryFailed);
        journal = ConfigureShortcut(
            store,
            journal,
            journal.StartMenuShortcut,
            "start-menu",
            current => current with { StartMenuShortcutMutated = true },
            current => current with { StartMenuShortcutMutated = false },
            () => shortcutManager.Create(
                paths.StartMenuShortcutPath,
                paths.ViewerExecutablePath,
                paths.InstallDirectory),
            steps,
            ref recoveryFailed);
        journal = ConfigureShortcut(
            store,
            journal,
            journal.StartupShortcut,
            "startup",
            current => current with { StartupShortcutMutated = true },
            current => current with { StartupShortcutMutated = false },
            () => shortcutManager.RemoveOwned(
                paths.StartupShortcutPath,
                paths.ViewerExecutablePath),
            steps,
            ref recoveryFailed);

        journal = journal with { Stage = "shortcuts-configured" };
        store.Write(journal);
        return journal;
    }

    private ViewerDeploymentJournal ConfigureShortcut(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        ShortcutJournalSnapshot snapshot,
        string diagnosticName,
        Func<ViewerDeploymentJournal, ViewerDeploymentJournal> markIntent,
        Func<ViewerDeploymentJournal, ViewerDeploymentJournal> clearIntent,
        Func<ViewerShortcutMutationResult> mutate,
        ViewerSetupStepRecorder steps,
        ref bool recoveryFailed)
    {
        journal = markIntent(journal);
        store.Write(journal);
        try
        {
            var result = mutate();
            if (!result.Mutated)
            {
                journal = clearIntent(journal);
                store.Write(journal);
            }

            if (result.Preserved)
            {
                steps.Warning(
                    "SHORTCUT_PRESERVED",
                    "바로가기",
                    "동일한 이름의 사용자 바로가기는 변경하지 않았습니다.");
            }
            else
            {
                steps.Succeeded(
                    $"SHORTCUT_{diagnosticName.ToUpperInvariant()}_OK",
                    "바로가기",
                    diagnosticName == "startup"
                        ? "제품 소유 자동 시작 바로가기를 정리했습니다."
                        : "제품 소유 바로가기를 준비했습니다.");
            }
        }
        catch
        {
            var restoreFailed = false;
            try
            {
                shortcutManager.Restore(snapshot);
                journal = clearIntent(journal);
                store.Write(journal);
            }
            catch
            {
                // Preserve the intent and evidence for a later explicit recovery.
                restoreFailed = true;
                recoveryFailed = true;
            }

            steps.Warning(
                ViewerSetupErrorCodes.ShortcutFailed,
                "바로가기",
                restoreFailed
                    ? "바로가기 복구를 완료하지 못해 Viewer 설치를 중단합니다."
                    : "바로가기를 변경하지 못했지만 Viewer 설치는 계속합니다.");
        }

        return journal;
    }

    private async Task CleanupCommittedTransactionAsync(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        CancellationToken cancellationToken)
    {
        ValidateExpectedInstallation(
            paths.InstallDirectory,
            journal.PackageManifestSha256,
            allowLegacy: false,
            ViewerSetupErrorCodes.RollbackFailed);
        if (journal.FormatVersion == ViewerDeploymentJournalStore.CurrentFormatVersion &&
            journal.PreviousInstallKind == ViewerPreviousInstallKind.Invalid)
        {
            journal = await FinalizeInvalidQuarantineAsync(
                store,
                journal,
                cancellationToken);
        }
        await CleanupTransactionArtifactsAsync(
            store,
            journal,
            cancellationToken);
    }

    private async Task RestoreInvalidExistingInstallAsync(
        ViewerDeploymentJournal journal,
        bool backupExists,
        bool installExists,
        CancellationToken cancellationToken)
    {
        if (backupExists)
        {
            ValidateUntrustedBackupTopology(journal);
            if (installExists)
            {
                if (!journal.StagingActivated)
                {
                    throw new ViewerSetupException(
                        ViewerSetupErrorCodes.RollbackFailed,
                        "기존 Viewer 격리 이후 설치 경로가 예기치 않게 다시 생성되었습니다.");
                }

                ValidateExpectedInstallation(
                    paths.InstallDirectory,
                    journal.PackageManifestSha256,
                    allowLegacy: false,
                    ViewerSetupErrorCodes.RollbackFailed);
                await MoveTransactionDirectoryAsync(
                    paths.InstallDirectory,
                    journal.FailedDirectory,
                    ViewerSetupErrorCodes.RollbackFailed,
                    "실패한 새 Viewer를 복구 영역으로 이동하지 못했습니다.",
                    cancellationToken);
            }

            await MoveTransactionDirectoryAsync(
                journal.BackupDirectory,
                paths.InstallDirectory,
                ViewerSetupErrorCodes.RollbackFailed,
                "격리했던 기존 Viewer 폴더를 원래 위치로 복구하지 못했습니다.",
                cancellationToken);
            ValidateRestoredUntrustedInstall(journal);
            return;
        }

        var stagingExists = fileSystem.DirectoryExists(journal.StagingDirectory);
        if (!journal.StagingActivated && installExists ||
            journal.StagingActivated && stagingExists && installExists)
        {
            ValidateRestoredUntrustedInstall(journal);
            return;
        }

        throw new ViewerSetupException(
            ViewerSetupErrorCodes.RollbackFailed,
            "격리했던 기존 Viewer 폴더의 위치를 확인할 수 없습니다.");
    }

    private async Task RestoreEmptyExistingInstallAsync(
        ViewerDeploymentJournal journal,
        bool installExists,
        CancellationToken cancellationToken)
    {
        if (installExists)
        {
            if (journal.StagingActivated)
            {
                ValidateExpectedInstallation(
                    paths.InstallDirectory,
                    journal.PackageManifestSha256,
                    allowLegacy: false,
                    ViewerSetupErrorCodes.RollbackFailed);
                await MoveTransactionDirectoryAsync(
                    paths.InstallDirectory,
                    journal.FailedDirectory,
                    ViewerSetupErrorCodes.RollbackFailed,
                    "실패한 새 Viewer를 복구 영역으로 이동하지 못했습니다.",
                    cancellationToken);
            }
            else if (fileSystem.IsReparsePoint(paths.InstallDirectory) ||
                     fileSystem.DirectoryHasEntries(paths.InstallDirectory))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "비어 있던 Viewer 설치 폴더의 상태가 변경되었습니다.");
            }
        }

        if (!fileSystem.DirectoryExists(paths.InstallDirectory))
        {
            fileSystem.CreateDirectory(paths.InstallDirectory);
        }

        if (fileSystem.FileExists(paths.InstallDirectory) ||
            fileSystem.IsReparsePoint(paths.InstallDirectory) ||
            fileSystem.DirectoryHasEntries(paths.InstallDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.RollbackFailed,
                "비어 있던 Viewer 설치 폴더를 복구하지 못했습니다.");
        }
    }

    private void ValidateUntrustedBackupTopology(ViewerDeploymentJournal journal)
    {
        if (!fileSystem.DirectoryExists(journal.BackupDirectory) ||
            fileSystem.FileExists(journal.BackupDirectory) ||
            fileSystem.IsReparsePoint(journal.BackupDirectory) ||
            fileSystem.DirectoryTreeContainsReparsePoint(journal.BackupDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.RollbackFailed,
                "격리한 기존 Viewer 폴더의 경로 상태를 안전하게 확인할 수 없습니다.");
        }
    }

    private void ValidateRestoredUntrustedInstall(ViewerDeploymentJournal journal)
    {
        if (!fileSystem.DirectoryExists(paths.InstallDirectory) ||
            fileSystem.FileExists(paths.InstallDirectory) ||
            fileSystem.IsReparsePoint(paths.InstallDirectory) ||
            fileSystem.DirectoryTreeContainsReparsePoint(paths.InstallDirectory) ||
            fileSystem.DirectoryExists(journal.BackupDirectory) ||
            fileSystem.FileExists(journal.BackupDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.RollbackFailed,
                "기존 Viewer 폴더 원복 상태를 확인할 수 없습니다.");
        }
    }

    private async Task CleanupTransactionArtifactsAsync(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        CancellationToken cancellationToken)
    {
        await DeleteTransactionDirectoryAsync(
            journal.StagingDirectory,
            cancellationToken);
        await DeleteTransactionDirectoryAsync(
            journal.BackupDirectory,
            cancellationToken);
        await DeleteTransactionDirectoryAsync(
            journal.FailedDirectory,
            cancellationToken);
        await DeleteTransactionDirectoryAsync(
            journal.EvidenceDirectory,
            cancellationToken);
        store.Delete();
    }

    private async Task<ViewerDeploymentJournal> FinalizeInvalidQuarantineAsync(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        CancellationToken cancellationToken)
    {
        if (!journal.QuarantineLatestToPreviousCompleted)
        {
            if (!journal.QuarantineLatestToPreviousIntent)
            {
                journal = journal with
                {
                    Stage = "quarantine-latest-to-previous-intent",
                    QuarantineLatestToPreviousIntent = true
                };
                store.Write(journal);
            }

            await MoveLatestQuarantineToPreviousAsync(cancellationToken);
            journal = journal with
            {
                Stage = "quarantine-latest-to-previous-completed",
                QuarantineLatestToPreviousCompleted = true
            };
            store.Write(journal);
        }

        if (!journal.QuarantineBackupToLatestCompleted)
        {
            if (!journal.QuarantineBackupToLatestIntent)
            {
                journal = journal with
                {
                    Stage = "quarantine-backup-to-latest-intent",
                    QuarantineBackupToLatestIntent = true
                };
                store.Write(journal);
            }

            await MoveBackupQuarantineToLatestAsync(journal, cancellationToken);
            journal = journal with
            {
                Stage = "quarantine-backup-to-latest-completed",
                QuarantineBackupToLatestCompleted = true
            };
            store.Write(journal);
        }

        if (!journal.QuarantinePreviousDeleteCompleted)
        {
            if (!journal.QuarantinePreviousDeleteIntent)
            {
                journal = journal with
                {
                    Stage = "quarantine-previous-delete-intent",
                    QuarantinePreviousDeleteIntent = true
                };
                store.Write(journal);
            }

            await DeleteOwnedQuarantineAsync(
                paths.QuarantinePreviousDirectory,
                paths.QuarantinePreviousMarkerPath,
                cancellationToken);
            journal = journal with
            {
                Stage = "quarantine-previous-delete-completed",
                QuarantinePreviousDeleteCompleted = true
            };
            store.Write(journal);
        }

        ValidateOwnedQuarantinePair(
            paths.QuarantineLatestDirectory,
            paths.QuarantineLatestMarkerPath,
            journal.TransactionId);
        return journal;
    }

    private async Task MoveLatestQuarantineToPreviousAsync(
        CancellationToken cancellationToken)
    {
        ViewerSetupPathGuard.ValidateQuarantinePaths(paths);
        var latestExists = fileSystem.DirectoryExists(paths.QuarantineLatestDirectory);
        var latestFile = fileSystem.FileExists(paths.QuarantineLatestDirectory);
        var latestMarker = fileSystem.FileExists(paths.QuarantineLatestMarkerPath);
        var previousExists = fileSystem.DirectoryExists(paths.QuarantinePreviousDirectory);
        var previousFile = fileSystem.FileExists(paths.QuarantinePreviousDirectory);
        var previousMarker = fileSystem.FileExists(paths.QuarantinePreviousMarkerPath);
        if (latestFile || previousFile)
        {
            throw QuarantineFailure("Viewer 격리 보관 경로가 폴더가 아닙니다.");
        }

        if (previousExists)
        {
            if (latestExists)
            {
                throw QuarantineFailure("Viewer 격리 보관 폴더가 동시에 존재합니다.");
            }

            if (!previousMarker && latestMarker)
            {
                MoveOwnedMarker(
                    paths.QuarantineLatestMarkerPath,
                    paths.QuarantinePreviousMarkerPath);
                previousMarker = true;
                latestMarker = false;
            }
            else if (previousMarker && latestMarker)
            {
                var previousOwner = ReadQuarantineMarker(paths.QuarantinePreviousMarkerPath);
                var latestOwner = ReadQuarantineMarker(paths.QuarantineLatestMarkerPath);
                if (!string.Equals(
                        previousOwner.TransactionId,
                        latestOwner.TransactionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw QuarantineFailure("Viewer 격리 소유 정보가 서로 일치하지 않습니다.");
                }

                fileSystem.DeleteFile(paths.QuarantineLatestMarkerPath);
                latestMarker = false;
            }

            if (!previousMarker || latestMarker)
            {
                throw QuarantineFailure("Viewer 격리 이동 상태를 확인할 수 없습니다.");
            }

            ValidateOwnedQuarantinePair(
                paths.QuarantinePreviousDirectory,
                paths.QuarantinePreviousMarkerPath);
            return;
        }

        if (!latestExists)
        {
            if (latestMarker || previousMarker)
            {
                throw QuarantineFailure("Viewer 격리 소유 정보만 남아 있습니다.");
            }

            return;
        }

        if (previousMarker)
        {
            throw QuarantineFailure("이전 Viewer 격리 소유 정보가 이미 존재합니다.");
        }

        ValidateOwnedQuarantinePair(
            paths.QuarantineLatestDirectory,
            paths.QuarantineLatestMarkerPath);
        await MoveTransactionDirectoryAsync(
            paths.QuarantineLatestDirectory,
            paths.QuarantinePreviousDirectory,
            ViewerSetupErrorCodes.QuarantineFailed,
            "기존 Viewer 격리본을 회전하지 못했습니다.",
            cancellationToken);
        MoveOwnedMarker(
            paths.QuarantineLatestMarkerPath,
            paths.QuarantinePreviousMarkerPath);
        ValidateOwnedQuarantinePair(
            paths.QuarantinePreviousDirectory,
            paths.QuarantinePreviousMarkerPath);
    }

    private async Task MoveBackupQuarantineToLatestAsync(
        ViewerDeploymentJournal journal,
        CancellationToken cancellationToken)
    {
        var backupExists = fileSystem.DirectoryExists(journal.BackupDirectory);
        var latestExists = fileSystem.DirectoryExists(paths.QuarantineLatestDirectory);
        if (fileSystem.FileExists(journal.BackupDirectory) ||
            fileSystem.FileExists(paths.QuarantineLatestDirectory))
        {
            throw QuarantineFailure("Viewer 격리 대상 경로가 폴더가 아닙니다.");
        }

        if (backupExists && latestExists)
        {
            throw QuarantineFailure("새 Viewer 격리본과 임시 보관 폴더가 동시에 존재합니다.");
        }

        if (backupExists)
        {
            ValidateUntrustedBackupTopology(journal);
            if (fileSystem.FileExists(paths.QuarantineLatestMarkerPath))
            {
                throw QuarantineFailure("새 Viewer 격리 소유 정보가 이미 존재합니다.");
            }

            await MoveTransactionDirectoryAsync(
                journal.BackupDirectory,
                paths.QuarantineLatestDirectory,
                ViewerSetupErrorCodes.QuarantineFailed,
                "기존 Viewer 폴더를 최종 격리 위치로 이동하지 못했습니다.",
                cancellationToken);
        }
        else if (!latestExists)
        {
            throw QuarantineFailure("최종 보관할 Viewer 격리본을 찾을 수 없습니다.");
        }

        if (!fileSystem.FileExists(paths.QuarantineLatestMarkerPath))
        {
            WriteQuarantineMarker(
                paths.QuarantineLatestMarkerPath,
                journal.TransactionId);
        }

        ValidateOwnedQuarantinePair(
            paths.QuarantineLatestDirectory,
            paths.QuarantineLatestMarkerPath,
            journal.TransactionId);
    }

    private async Task DeleteOwnedQuarantineAsync(
        string directory,
        string markerPath,
        CancellationToken cancellationToken)
    {
        var directoryExists = fileSystem.DirectoryExists(directory);
        var markerExists = fileSystem.FileExists(markerPath);
        if (!directoryExists)
        {
            if (fileSystem.FileExists(directory))
            {
                throw QuarantineFailure("Viewer 격리 정리 경로가 폴더가 아닙니다.");
            }

            if (markerExists)
            {
                _ = ReadQuarantineMarker(markerPath);
                fileSystem.DeleteFile(markerPath);
                if (fileSystem.FileExists(markerPath))
                {
                    throw QuarantineFailure("Viewer 격리 소유 정보를 정리하지 못했습니다.");
                }
            }

            return;
        }

        ValidateOwnedQuarantinePair(directory, markerPath);
        for (var attempt = 1; attempt <= DirectoryMutationMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                fileSystem.DeleteDirectoryTreeNoFollow(directory);
            }
            catch (Exception exception) when (IsTransientDirectoryMutation(exception))
            {
                if (fileSystem.DirectoryExists(directory) &&
                    attempt < DirectoryMutationMaxAttempts)
                {
                    await Task.Delay(DirectoryMutationRetryDelay, cancellationToken);
                    continue;
                }

                if (fileSystem.DirectoryExists(directory) || fileSystem.FileExists(directory))
                {
                    throw QuarantineFailure(
                        "이전 Viewer 격리본을 안전하게 정리하지 못했습니다.",
                        exception);
                }
            }

            break;
        }

        if (fileSystem.DirectoryExists(directory) || fileSystem.FileExists(directory))
        {
            throw QuarantineFailure("이전 Viewer 격리본을 안전하게 정리하지 못했습니다.");
        }

        _ = ReadQuarantineMarker(markerPath);
        fileSystem.DeleteFile(markerPath);
        if (fileSystem.FileExists(markerPath))
        {
            throw QuarantineFailure("이전 Viewer 격리 소유 정보를 정리하지 못했습니다.");
        }
    }

    private void ValidateOwnedQuarantinePair(
        string directory,
        string markerPath,
        string? expectedTransactionId = null)
    {
        if (!fileSystem.DirectoryExists(directory) ||
            fileSystem.FileExists(directory) ||
            fileSystem.IsReparsePoint(directory) ||
            fileSystem.DirectoryTreeContainsReparsePoint(directory) ||
            !fileSystem.FileExists(markerPath))
        {
            throw QuarantineFailure("Viewer 격리본의 소유 상태를 안전하게 확인할 수 없습니다.");
        }

        var marker = ReadQuarantineMarker(markerPath);
        if (expectedTransactionId is not null &&
            !string.Equals(
                marker.TransactionId,
                expectedTransactionId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw QuarantineFailure("Viewer 격리본의 작업 식별자가 일치하지 않습니다.");
        }
    }

    private void MoveOwnedMarker(string source, string destination)
    {
        var marker = ReadQuarantineMarker(source);
        if (fileSystem.FileExists(destination))
        {
            throw QuarantineFailure("Viewer 격리 소유 정보가 이미 존재합니다.");
        }

        WriteQuarantineMarker(destination, marker.TransactionId, marker.CapturedUtc);
        fileSystem.DeleteFile(source);
        if (fileSystem.FileExists(source))
        {
            throw QuarantineFailure("Viewer 격리 소유 정보를 이동하지 못했습니다.");
        }
    }

    private void WriteQuarantineMarker(
        string markerPath,
        string transactionId,
        DateTimeOffset? capturedUtc = null)
    {
        if (!ViewerSetupPathGuard.IsTransactionId(transactionId))
        {
            throw QuarantineFailure("Viewer 격리 작업 식별자가 올바르지 않습니다.");
        }

        fileSystem.WriteAllTextAtomic(
            markerPath,
            JsonSerializer.Serialize(
                new ViewerQuarantineMarker(
                    1,
                    ViewerSetupConstants.ProductName,
                    transactionId.ToLowerInvariant(),
                    capturedUtc ?? DateTimeOffset.UtcNow)));
    }

    private ViewerQuarantineMarker ReadQuarantineMarker(string markerPath)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<ViewerQuarantineMarker>(
                fileSystem.ReadAllTextBounded(
                    markerPath,
                    MaximumQuarantineMarkerBytes));
            if (marker is null ||
                marker.FormatVersion != 1 ||
                !string.Equals(
                    marker.Product,
                    ViewerSetupConstants.ProductName,
                    StringComparison.Ordinal) ||
                !ViewerSetupPathGuard.IsTransactionId(marker.TransactionId) ||
                marker.CapturedUtc.Offset != TimeSpan.Zero)
            {
                throw new JsonException();
            }

            return marker;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or
                UnauthorizedAccessException or
                System.Text.DecoderFallbackException)
        {
            throw QuarantineFailure(
                "Viewer 격리 소유 정보를 확인할 수 없습니다.",
                exception);
        }
    }

    private static ViewerSetupException QuarantineFailure(
        string message,
        Exception? inner = null) =>
        new(ViewerSetupErrorCodes.QuarantineFailed, message, inner);

    private sealed record ViewerQuarantineMarker(
        int FormatVersion,
        string Product,
        string TransactionId,
        DateTimeOffset CapturedUtc);

    private async Task DeleteTransactionDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= DirectoryMutationMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryExists = fileSystem.DirectoryExists(directory);
            var fileExists = fileSystem.FileExists(directory);
            if (!directoryExists && !fileExists)
            {
                return;
            }

            if (!directoryExists || fileExists)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "Viewer 설치 작업 폴더 상태가 모호하여 정리를 중단했습니다.");
            }

            try
            {
                fileSystem.DeleteDirectory(directory, recursive: true);
            }
            catch (Exception exception) when (IsTransientDirectoryMutation(exception))
            {
                directoryExists = fileSystem.DirectoryExists(directory);
                fileExists = fileSystem.FileExists(directory);
                if (!directoryExists && !fileExists)
                {
                    return;
                }

                if (!directoryExists || fileExists ||
                    attempt == DirectoryMutationMaxAttempts)
                {
                    throw new ViewerSetupException(
                        ViewerSetupErrorCodes.RollbackFailed,
                        "Viewer 설치 작업 폴더를 정리하지 못했습니다.",
                        exception);
                }
            }

            if (!fileSystem.DirectoryExists(directory) &&
                !fileSystem.FileExists(directory))
            {
                return;
            }

            if (attempt == DirectoryMutationMaxAttempts)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "Viewer 설치 작업 폴더를 정리하지 못했습니다.");
            }

            await Task.Delay(DirectoryMutationRetryDelay, cancellationToken);
        }
    }

    private async Task MoveTransactionDirectoryAsync(
        string source,
        string destination,
        string failureCode,
        string safeMessage,
        CancellationToken cancellationToken)
    {
        var moveAttempted = false;
        for (var attempt = 1; attempt <= DirectoryMutationMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceDirectoryExists = fileSystem.DirectoryExists(source);
            var sourceFileExists = fileSystem.FileExists(source);
            var destinationDirectoryExists = fileSystem.DirectoryExists(destination);
            var destinationFileExists = fileSystem.FileExists(destination);

            if (!sourceDirectoryExists && !sourceFileExists &&
                destinationDirectoryExists && !destinationFileExists)
            {
                if (moveAttempted)
                {
                    return;
                }

                throw new ViewerSetupException(failureCode, safeMessage);
            }

            if (!sourceDirectoryExists || sourceFileExists ||
                destinationDirectoryExists || destinationFileExists)
            {
                throw new ViewerSetupException(failureCode, safeMessage);
            }

            try
            {
                moveAttempted = true;
                fileSystem.MoveDirectory(source, destination);
            }
            catch (Exception exception) when (IsTransientDirectoryMutation(exception))
            {
                sourceDirectoryExists = fileSystem.DirectoryExists(source);
                sourceFileExists = fileSystem.FileExists(source);
                destinationDirectoryExists = fileSystem.DirectoryExists(destination);
                destinationFileExists = fileSystem.FileExists(destination);
                if (!sourceDirectoryExists && !sourceFileExists &&
                    destinationDirectoryExists && !destinationFileExists)
                {
                    return;
                }

                if (!sourceDirectoryExists || sourceFileExists ||
                    destinationDirectoryExists || destinationFileExists ||
                    attempt == DirectoryMutationMaxAttempts)
                {
                    throw new ViewerSetupException(
                        failureCode,
                        safeMessage,
                        exception);
                }

                await Task.Delay(DirectoryMutationRetryDelay, cancellationToken);
                continue;
            }

            if (!fileSystem.DirectoryExists(source) &&
                !fileSystem.FileExists(source) &&
                fileSystem.DirectoryExists(destination) &&
                !fileSystem.FileExists(destination))
            {
                return;
            }

            throw new ViewerSetupException(failureCode, safeMessage);
        }

        throw new ViewerSetupException(failureCode, safeMessage);
    }

    private static bool IsTransientDirectoryMutation(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException;

    private void RestoreShortcutIfMutated(
        bool mutated,
        ShortcutJournalSnapshot snapshot)
    {
        if (mutated)
        {
            shortcutManager.Restore(snapshot);
        }
    }

    private ShortcutJournalSnapshot NewSnapshot(
        string shortcutPath,
        string backupFilePath) =>
        new(
            shortcutPath,
            fileSystem.FileExists(shortcutPath),
            backupFilePath,
            paths.ViewerExecutablePath);

    private void EnsureTransactionTargetsAbsent(ViewerTransactionPaths transaction)
    {
        if (fileSystem.DirectoryExists(transaction.StagingDirectory) ||
            fileSystem.DirectoryExists(transaction.BackupDirectory) ||
            fileSystem.DirectoryExists(transaction.FailedDirectory) ||
            fileSystem.DirectoryExists(transaction.EvidenceDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.InstallWriteFailed,
                "Viewer 설치 작업 폴더가 이미 사용 중입니다.");
        }
    }

    private void ValidateBasePaths()
    {
        var package = Normalize(paths.PackageDirectory);
        var install = Normalize(paths.InstallDirectory);
        var data = Normalize(paths.DataDirectory);
        var operations = Normalize(paths.OperationsDirectory);
        var quarantineLatest = Normalize(paths.QuarantineLatestDirectory);
        var quarantinePrevious = Normalize(paths.QuarantinePreviousDirectory);
        var installParent = Normalize(
            Path.GetDirectoryName(paths.InstallDirectory) ?? string.Empty);
        var packageManagedSibling = IsManagedTransactionSource(
            package,
            installParent,
            Path.GetFileName(install));
        if (string.Equals(package, install, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(install, package) ||
            string.Equals(package, operations, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(operations, package) ||
            string.Equals(package, quarantineLatest, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(quarantineLatest, package) ||
            string.Equals(package, quarantinePrevious, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(quarantinePrevious, package) ||
            packageManagedSibling ||
            string.Equals(data, install, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(install, data))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "Viewer 패키지, 설치 폴더 또는 데이터 폴더 경로가 안전하지 않습니다.");
        }
    }

    private ExistingInstallClassification ClassifyExistingInstallation()
    {
        if (fileSystem.FileExists(paths.InstallDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "기존 Viewer 설치 경로가 폴더가 아닙니다.");
        }

        if (!fileSystem.DirectoryExists(paths.InstallDirectory))
        {
            return new ExistingInstallClassification(
                ViewerPreviousInstallKind.None,
                null,
                false);
        }

        if (fileSystem.IsReparsePoint(paths.InstallDirectory) ||
            fileSystem.DirectoryTreeContainsReparsePoint(paths.InstallDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "기존 Viewer 설치 폴더의 연결 경로를 안전하게 확인할 수 없습니다.");
        }

        if (!fileSystem.DirectoryHasEntries(paths.InstallDirectory))
        {
            return new ExistingInstallClassification(
                ViewerPreviousInstallKind.None,
                null,
                true);
        }

        try
        {
            return new ExistingInstallClassification(
                ViewerPreviousInstallKind.Verified,
                packageValidator.ValidateExisting(paths.InstallDirectory),
                false);
        }
        catch (ViewerSetupException exception) when (
            exception.Code is ViewerSetupErrorCodes.PackageNotFound or
                ViewerSetupErrorCodes.ManifestInvalid or
                ViewerSetupErrorCodes.PackageHashMismatch or
                ViewerSetupErrorCodes.PackageInvalid)
        {
            return new ExistingInstallClassification(
                ViewerPreviousInstallKind.Invalid,
                null,
                false);
        }
    }

    private void RemoveRecordedEmptyInstallDirectory()
    {
        if (!fileSystem.DirectoryExists(paths.InstallDirectory) ||
            fileSystem.FileExists(paths.InstallDirectory) ||
            fileSystem.IsReparsePoint(paths.InstallDirectory) ||
            fileSystem.DirectoryHasEntries(paths.InstallDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "비어 있던 Viewer 설치 폴더가 설치 중 변경되었습니다.");
        }

        fileSystem.DeleteDirectory(paths.InstallDirectory, recursive: false);
        if (fileSystem.DirectoryExists(paths.InstallDirectory) ||
            fileSystem.FileExists(paths.InstallDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.InstallWriteFailed,
                "비어 있는 Viewer 설치 폴더를 준비하지 못했습니다.");
        }
    }

    private sealed record ExistingInstallClassification(
        ViewerPreviousInstallKind Kind,
        ViewerPackage? Package,
        bool EmptyDirectoryExisted);

    private void ValidateRestoredInstallation(ViewerDeploymentJournal journal)
    {
        if (journal.FormatVersion == ViewerDeploymentJournalStore.CurrentFormatVersion &&
            journal.PreviousInstallKind == ViewerPreviousInstallKind.None &&
            journal.PreviousEmptyInstallDirectory)
        {
            if (!fileSystem.DirectoryExists(paths.InstallDirectory) ||
                fileSystem.FileExists(paths.InstallDirectory) ||
                fileSystem.IsReparsePoint(paths.InstallDirectory) ||
                fileSystem.DirectoryHasEntries(paths.InstallDirectory))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "비어 있던 Viewer 설치 폴더 복구 상태를 확인할 수 없습니다.");
            }

            return;
        }

        if (!journal.PreviousInstallExisted)
        {
            if (fileSystem.DirectoryExists(paths.InstallDirectory))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "첫 설치 이전 상태가 완전히 복구되지 않았습니다.");
            }

            return;
        }

        if (journal.FormatVersion == ViewerDeploymentJournalStore.CurrentFormatVersion &&
            journal.PreviousInstallKind == ViewerPreviousInstallKind.Invalid)
        {
            if (!fileSystem.DirectoryExists(paths.InstallDirectory) ||
                fileSystem.FileExists(paths.InstallDirectory) ||
                fileSystem.IsReparsePoint(paths.InstallDirectory) ||
                fileSystem.DirectoryExists(journal.BackupDirectory) ||
                fileSystem.FileExists(journal.BackupDirectory))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "기존 Viewer 폴더 원복 상태를 확인할 수 없습니다.");
            }

            return;
        }

        ValidateExpectedInstallation(
            paths.InstallDirectory,
            journal.PreviousManifestSha256!,
            allowLegacy: true,
            ViewerSetupErrorCodes.RollbackFailed);
    }

    private ViewerPackage ValidateExpectedInstallation(
        string directory,
        string expectedManifestSha256,
        bool allowLegacy,
        string failureCode)
    {
        try
        {
            var package = allowLegacy
                ? packageValidator.ValidateExisting(directory)
                : packageValidator.Validate(directory);
            if (!string.Equals(
                    package.ManifestSha256,
                    expectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The Viewer manifest changed during deployment.");
            }

            return package;
        }
        catch (Exception exception) when (
            exception is ViewerSetupException or InvalidDataException)
        {
            throw new ViewerSetupException(
                failureCode,
                "Viewer 설치 파일 무결성을 다시 확인하지 못해 작업을 중단했습니다.",
                exception);
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string parent, string candidate) =>
        candidate.StartsWith(
            parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedTransactionSource(
        string package,
        string installParent,
        string installName)
    {
        var current = package;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                return false;
            }

            if (string.Equals(
                    Normalize(parent),
                    installParent,
                    StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(current);
                return name.StartsWith(
                        installName + ".__staging_",
                        StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(
                        installName + ".__backup_",
                        StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(
                        installName + ".__failed_",
                        StringComparison.OrdinalIgnoreCase);
            }

            current = parent;
        }

        return false;
    }

    private static string ShutdownMessage(ViewerShutdownStatus status) =>
        status switch
        {
            ViewerShutdownStatus.Rejected =>
                "실행 중인 Viewer가 종료 요청을 거부했습니다. Viewer를 직접 닫고 다시 시도하세요.",
            ViewerShutdownStatus.ProtocolUnsupported =>
                "실행 중인 이전 Viewer는 자동 종료를 지원하지 않습니다. Viewer를 직접 닫고 다시 시도하세요.",
            ViewerShutdownStatus.TimedOut =>
                "Viewer 종료를 확인하지 못했습니다. Viewer를 직접 닫고 다시 시도하세요.",
            _ =>
                "실행 중인 Viewer와 안전하게 통신할 수 없습니다. Viewer를 직접 닫고 다시 시도하세요."
        };

    private static ViewerSetupResult Failure(
        string internalCode,
        string message,
        IReadOnlyList<ViewerSetupStep> steps) =>
        ViewerSetupResult.Failure(PublicCode(internalCode), message, steps);

    private static ViewerSetupResult AttachDiagnostic(
        ViewerSetupResult result,
        DeploymentDiagnosticState state)
    {
        var finalCode = result.Code;
        state.MarkFailureForActiveStage(result.Succeeded);
        return result with
        {
            Diagnostic = ViewerSetupDiagnosticFactory.Create(
                state.ProductVersion,
                state.Operation,
                finalCode,
                state.PrimaryCode ?? finalCode,
                result.Succeeded
                    ? ViewerSetupDiagnosticStage.None
                    : state.ActiveStage,
                state.RollbackState,
                state.JournalState,
                state.QuarantineState,
                state.PreviousInstallKind switch
                {
                    ViewerPreviousInstallKind.None =>
                        ViewerSetupDiagnosticPreviousInstallState.None,
                    ViewerPreviousInstallKind.Verified =>
                        ViewerSetupDiagnosticPreviousInstallState.Verified,
                    ViewerPreviousInstallKind.Invalid =>
                        ViewerSetupDiagnosticPreviousInstallState.Invalid,
                    _ => ViewerSetupDiagnosticPreviousInstallState.Unknown
                },
                state.Stages)
        };
    }

    private static ViewerSetupDiagnosticSnapshot RecoveryInspectionDiagnostic(
        ViewerDeploymentJournal? journal,
        ViewerSetupDiagnosticJournalState journalState,
        ViewerSetupDiagnosticStage failedStage) =>
        ViewerSetupDiagnosticFactory.Create(
            journal?.PackageVersion ?? "UNKNOWN",
            ViewerSetupDiagnosticOperation.RecoveryInspection,
            ViewerSetupErrorCodes.RecoveryRequired,
            failedStage: failedStage,
            journalState: journalState,
            quarantineState: journal is null
                ? ViewerSetupDiagnosticQuarantineState.Unknown
                : QuarantineStateFor(journal),
            previousInstallState: journal is null
                ? ViewerSetupDiagnosticPreviousInstallState.Unknown
                : EffectivePreviousInstallKind(journal) switch
                {
                    ViewerPreviousInstallKind.None =>
                        ViewerSetupDiagnosticPreviousInstallState.None,
                    ViewerPreviousInstallKind.Verified =>
                        ViewerSetupDiagnosticPreviousInstallState.Verified,
                    ViewerPreviousInstallKind.Invalid =>
                        ViewerSetupDiagnosticPreviousInstallState.Invalid,
                    _ => ViewerSetupDiagnosticPreviousInstallState.Unknown
                });

    private static ViewerPreviousInstallKind EffectivePreviousInstallKind(
        ViewerDeploymentJournal journal) =>
        journal.FormatVersion == ViewerDeploymentJournalStore.LegacyFormatVersion
            ? journal.PreviousInstallExisted
                ? ViewerPreviousInstallKind.Verified
                : ViewerPreviousInstallKind.None
            : journal.PreviousInstallKind;

    private static ViewerSetupDiagnosticQuarantineState QuarantineStateFor(
        ViewerDeploymentJournal journal)
    {
        if (EffectivePreviousInstallKind(journal) != ViewerPreviousInstallKind.Invalid)
        {
            return ViewerSetupDiagnosticQuarantineState.NotRequired;
        }

        if (journal.QuarantineBackupToLatestCompleted)
        {
            return ViewerSetupDiagnosticQuarantineState.Retained;
        }

        if (journal.NormalLaunchObserved ||
            string.Equals(journal.Stage, "committed", StringComparison.Ordinal) ||
            journal.QuarantineLatestToPreviousIntent ||
            journal.QuarantineBackupToLatestIntent)
        {
            return ViewerSetupDiagnosticQuarantineState.FinalizationPending;
        }

        return journal.InstallMovedToBackup
            ? ViewerSetupDiagnosticQuarantineState.IsolatedPending
            : ViewerSetupDiagnosticQuarantineState.NotRun;
    }

    private sealed class DeploymentDiagnosticState(
        ViewerSetupDiagnosticOperation operation)
    {
        public ViewerSetupDiagnosticOperation Operation { get; } = operation;
        public string ProductVersion { get; set; } = "UNKNOWN";
        public string? PrimaryCode { get; set; }
        public ViewerSetupDiagnosticStage ActiveStage { get; set; } =
            ViewerSetupDiagnosticStage.Lock;
        public ViewerSetupDiagnosticRollbackState RollbackState { get; set; } =
            ViewerSetupDiagnosticRollbackState.NotRun;
        public ViewerSetupDiagnosticJournalState JournalState { get; set; } =
            ViewerSetupDiagnosticJournalState.None;
        public ViewerSetupDiagnosticQuarantineState QuarantineState { get; set; } =
            ViewerSetupDiagnosticQuarantineState.NotRun;
        public ViewerPreviousInstallKind PreviousInstallKind { get; set; } =
            ViewerPreviousInstallKind.Unspecified;
        public ViewerSetupDiagnosticStageState PackageState { get; set; }
        public ViewerSetupDiagnosticStageState ExistingInstallState { get; set; }
        public ViewerSetupDiagnosticStageState ShutdownState { get; set; }
        public ViewerSetupDiagnosticStageState StagingState { get; set; }
        public ViewerSetupDiagnosticStageState ActivationState { get; set; }
        public ViewerSetupDiagnosticStageState SmokeState { get; set; }
        public ViewerSetupDiagnosticStageState ShortcutState { get; set; }
        public ViewerSetupDiagnosticStageState LaunchState { get; set; }

        public ViewerSetupDiagnosticStageStates Stages => new(
            PackageState,
            ExistingInstallState,
            ShutdownState,
            StagingState,
            ActivationState,
            SmokeState,
            ShortcutState,
            LaunchState,
            RollbackState == ViewerSetupDiagnosticRollbackState.Succeeded
                ? ViewerSetupDiagnosticStageState.Succeeded
                : RollbackState == ViewerSetupDiagnosticRollbackState.Failed
                    ? ViewerSetupDiagnosticStageState.Failed
                    : ViewerSetupDiagnosticStageState.NotRun);

        public void MarkFailureForActiveStage(bool succeeded)
        {
            if (succeeded)
            {
                return;
            }

            switch (ActiveStage)
            {
                case ViewerSetupDiagnosticStage.Package:
                    PackageState = ViewerSetupDiagnosticStageState.Failed;
                    break;
                case ViewerSetupDiagnosticStage.Shutdown:
                    ShutdownState = ViewerSetupDiagnosticStageState.Failed;
                    break;
                case ViewerSetupDiagnosticStage.Staging:
                    StagingState = ViewerSetupDiagnosticStageState.Failed;
                    break;
                case ViewerSetupDiagnosticStage.Backup:
                case ViewerSetupDiagnosticStage.Quarantine:
                    ExistingInstallState = ViewerSetupDiagnosticStageState.Failed;
                    break;
                case ViewerSetupDiagnosticStage.Activation:
                    ActivationState = ViewerSetupDiagnosticStageState.Failed;
                    break;
                case ViewerSetupDiagnosticStage.Smoke:
                    SmokeState = ViewerSetupDiagnosticStageState.Failed;
                    break;
                case ViewerSetupDiagnosticStage.Shortcut:
                    ShortcutState = ViewerSetupDiagnosticStageState.Failed;
                    break;
                case ViewerSetupDiagnosticStage.Launch:
                    LaunchState = ViewerSetupDiagnosticStageState.Failed;
                    break;
            }
        }
    }

    internal static string PublicCode(string internalCode) =>
        internalCode switch
        {
            ViewerSetupErrorCodes.PackageNotFound or
            ViewerSetupErrorCodes.ManifestInvalid or
            ViewerSetupErrorCodes.PackageHashMismatch or
            ViewerSetupErrorCodes.PackageInvalid =>
                ViewerSetupErrorCodes.PackageInvalid,
            ViewerSetupErrorCodes.ViewerRunning =>
                ViewerSetupErrorCodes.ViewerRunning,
            ViewerSetupErrorCodes.SmokeFailed =>
                ViewerSetupErrorCodes.SmokeFailed,
            ViewerSetupErrorCodes.RollbackFailed =>
                ViewerSetupErrorCodes.RollbackFailed,
            ViewerSetupErrorCodes.RecoveryRequired =>
                ViewerSetupErrorCodes.RecoveryRequired,
            ViewerSetupErrorCodes.Cancelled =>
                ViewerSetupErrorCodes.Cancelled,
            ViewerSetupErrorCodes.AlreadyRunning =>
                ViewerSetupErrorCodes.AlreadyRunning,
            ViewerSetupErrorCodes.LaunchFailed =>
                ViewerSetupErrorCodes.LaunchFailed,
            ViewerSetupErrorCodes.ShortcutFailed =>
                ViewerSetupErrorCodes.ShortcutFailed,
            ViewerSetupErrorCodes.QuarantineFailed =>
                ViewerSetupErrorCodes.QuarantineFailed,
            ViewerSetupErrorCodes.PathInvalid =>
                ViewerSetupErrorCodes.PathInvalid,
            ViewerSetupErrorCodes.PathNotWritable =>
                ViewerSetupErrorCodes.PathNotWritable,
            _ => ViewerSetupErrorCodes.InstallWriteFailed
        };
}
