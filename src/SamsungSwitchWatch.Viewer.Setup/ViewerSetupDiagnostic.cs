using SamsungSwitchWatch.Viewer.Setup.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Setup;

internal static class ViewerSetupDiagnosticFactory
{
    public static ViewerSetupDiagnosticSnapshot Create(
        string productVersion,
        ViewerSetupDiagnosticOperation operation,
        string? finalCode,
        string? primaryCode = null,
        ViewerSetupDiagnosticStage failedStage = ViewerSetupDiagnosticStage.Unknown,
        ViewerSetupDiagnosticRollbackState rollbackState = ViewerSetupDiagnosticRollbackState.NotRun,
        ViewerSetupDiagnosticJournalState journalState = ViewerSetupDiagnosticJournalState.None,
        ViewerSetupDiagnosticQuarantineState quarantineState = ViewerSetupDiagnosticQuarantineState.NotRun,
        ViewerSetupDiagnosticPreviousInstallState previousInstallState = ViewerSetupDiagnosticPreviousInstallState.Unknown,
        ViewerSetupDiagnosticStageStates? stages = null) =>
        new(
            SafeVersion(productVersion),
            operation,
            Code(finalCode),
            Code(primaryCode ?? finalCode),
            failedStage,
            rollbackState,
            journalState,
            quarantineState,
            previousInstallState,
            stages ?? ViewerSetupDiagnosticStageStates.Empty);

    public static string CreateSupportCode(ViewerSetupDiagnosticSnapshot snapshot) =>
        Sws1ViewerSetupSupportCode.Encode(snapshot);

    public static ViewerSetupDiagnosticCode Code(string? value) => value switch
    {
        "OK" => ViewerSetupDiagnosticCode.Ok,
        "VIEWER_SETUP_PACKAGE_INVALID" => ViewerSetupDiagnosticCode.PackageInvalid,
        "VIEWER_SETUP_INSTALL_WRITE_FAILED" => ViewerSetupDiagnosticCode.InstallWriteFailed,
        "VIEWER_SETUP_PACKAGE_NOT_FOUND" => ViewerSetupDiagnosticCode.PackageNotFound,
        "VIEWER_SETUP_MANIFEST_INVALID" => ViewerSetupDiagnosticCode.ManifestInvalid,
        "VIEWER_SETUP_PACKAGE_HASH_MISMATCH" => ViewerSetupDiagnosticCode.PackageHashMismatch,
        "VIEWER_SETUP_PATH_INVALID" => ViewerSetupDiagnosticCode.PathInvalid,
        "VIEWER_SETUP_PATH_NOT_WRITABLE" => ViewerSetupDiagnosticCode.PathNotWritable,
        "VIEWER_SETUP_ALREADY_RUNNING" => ViewerSetupDiagnosticCode.AlreadyRunning,
        "VIEWER_SETUP_RECOVERY_REQUIRED" => ViewerSetupDiagnosticCode.RecoveryRequired,
        "VIEWER_SETUP_VIEWER_RUNNING" => ViewerSetupDiagnosticCode.ViewerRunning,
        "VIEWER_SETUP_SMOKE_FAILED" => ViewerSetupDiagnosticCode.SmokeFailed,
        "VIEWER_SETUP_LAUNCH_FAILED" => ViewerSetupDiagnosticCode.LaunchFailed,
        "VIEWER_SETUP_SHORTCUT_FAILED" => ViewerSetupDiagnosticCode.ShortcutFailed,
        "VIEWER_SETUP_ROLLBACK_FAILED" => ViewerSetupDiagnosticCode.RollbackFailed,
        "VIEWER_SETUP_CANCELLED" => ViewerSetupDiagnosticCode.Cancelled,
        "VIEWER_SETUP_UNEXPECTED" => ViewerSetupDiagnosticCode.Unexpected,
        "VIEWER_SETUP_QUARANTINE_FAILED" => ViewerSetupDiagnosticCode.QuarantineFailed,
        _ => ViewerSetupDiagnosticCode.Unknown
    };

    private static string SafeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "UNKNOWN";
        var trimmed = value.Trim();
        return trimmed.Length <= 32 && trimmed.All(character =>
            char.IsAsciiDigit(character) || character is '.' or '-' or '+' || char.IsAsciiLetter(character))
                ? trimmed
                : "UNKNOWN";
    }
}

internal static class ViewerSetupSupportCodePolicy
{
    public static bool ShouldShow(string resultCode, bool succeeded) =>
        !succeeded && resultCode is not
            "VIEWER_SETUP_CANCELLED" and
            not "VIEWER_SETUP_ALREADY_RUNNING";

    public static bool ShouldShow(ViewerSetupDiagnosticSnapshot? diagnostic) =>
        diagnostic is not null && diagnostic.Operation == ViewerSetupDiagnosticOperation.RecoveryInspection &&
        diagnostic.JournalState == ViewerSetupDiagnosticJournalState.Unreadable;

    public static string? TryCreate(ViewerSetupDiagnosticSnapshot? diagnostic)
    {
        if (diagnostic is null) return null;
        try
        {
            return ViewerSetupDiagnosticFactory.CreateSupportCode(diagnostic);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
