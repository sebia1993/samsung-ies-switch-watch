using SamsungSwitchWatch.Viewer.Setup.Diagnostics;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class Sws1ViewerSetupSupportCodeTests
{
    [Fact]
    public void Encode_RoundTripsSafeFixedLengthPayload()
    {
        var snapshot = CreateSnapshot();

        var code = Sws1ViewerSetupSupportCode.Encode(snapshot);

        Assert.Equal(24, code.Length);
        Assert.Matches("^SWS1-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code);
        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(code, out var decoded));
        Assert.Equal(Sws1ViewerSetupPayload.CurrentProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(0, decoded.ProductMajor);
        Assert.Equal(11, decoded.ProductMinor);
        Assert.Equal(10, decoded.ProductPatch);
        Assert.Equal(snapshot.Operation, decoded.Operation);
        Assert.Equal(snapshot.FinalCode, decoded.FinalCode);
        Assert.Equal(snapshot.PrimaryCode, decoded.PrimaryCode);
        Assert.Equal(snapshot.FailedStage, decoded.FailedStage);
        Assert.Equal(snapshot.RollbackState, decoded.RollbackState);
        Assert.Equal(snapshot.JournalState, decoded.JournalState);
        Assert.Equal(snapshot.QuarantineState, decoded.QuarantineState);
        Assert.Equal(snapshot.PreviousInstallState, decoded.PreviousInstallState);
        Assert.Equal(snapshot.Stages, decoded.Stages);
    }

    [Fact]
    public void Encode_GoldenCodeRemainsStable()
    {
        Assert.Equal(
            "SWS1-82S9-FCK7-ENB1-G06M",
            Sws1ViewerSetupSupportCode.Encode(CreateSnapshot()));
    }

    [Fact]
    public void Encode_IsDeterministicAndIndependentOfUntrustedText()
    {
        var first = ViewerSetupDiagnosticFactory.Create(
            "0.11.10-poc",
            ViewerSetupDiagnosticOperation.Install,
            "VIEWER_SETUP_ROLLBACK_FAILED",
            "VIEWER_SETUP_SMOKE_FAILED",
            ViewerSetupDiagnosticStage.Smoke,
            ViewerSetupDiagnosticRollbackState.Failed,
            ViewerSetupDiagnosticJournalState.Recoverable,
            ViewerSetupDiagnosticQuarantineState.RestoreFailed,
            ViewerSetupDiagnosticPreviousInstallState.Invalid,
            CreateSnapshot().Stages);
        var second = first with { ProductVersion = string.Concat("0.11.10", "-poc") };

        var repeatedCodes = Enumerable.Range(0, 128)
            .Select(_ => ViewerSetupDiagnosticFactory.CreateSupportCode(first))
            .ToArray();
        var firstCode = repeatedCodes[0];
        var secondCode = ViewerSetupDiagnosticFactory.CreateSupportCode(second);

        Assert.All(repeatedCodes, code => Assert.Equal(firstCode, code));
        Assert.Equal(firstCode, secondCode);
        Assert.DoesNotContain("USER", firstCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH", firstCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDecode_AcceptsCanonicalFormattingVariationsAndRejectsWrongPrefix()
    {
        var canonical = Sws1ViewerSetupSupportCode.Encode(CreateSnapshot());
        var compactLower = canonical.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(compactLower, out var decoded));
        Assert.Equal(ViewerSetupDiagnosticCode.RollbackFailed, decoded.FinalCode);
        Assert.False(Sws1ViewerSetupSupportCode.TryDecode(
            canonical.Replace("SWS1", "SWD1", StringComparison.Ordinal),
            out _));
    }

    [Fact]
    public void TryDecode_RejectsSingleCharacterCorruption()
    {
        var canonical = Sws1ViewerSetupSupportCode.Encode(CreateSnapshot());
        var characters = canonical.ToCharArray();
        var index = characters.Length - 1;
        characters[index] = characters[index] == '0' ? '2' : '0';

        Assert.False(Sws1ViewerSetupSupportCode.TryDecode(new string(characters), out _));
    }

    [Fact]
    public void Encode_RejectsPartiallyUnknownVersionSentinel()
    {
        var payload = Sws1ViewerSetupPayload.From(CreateSnapshot()) with
        {
            ProductMajor = Sws1ViewerSetupPayload.UnknownMajor,
            ProductMinor = 11,
            ProductPatch = 10
        };

        Assert.Throws<ArgumentException>(() => Sws1ViewerSetupSupportCode.Encode(payload));
    }

    [Fact]
    public void UnknownOrUnsafeProductVersionUsesReservedVersionValue()
    {
        var snapshot = CreateSnapshot() with { ProductVersion = "0.11.10 C:\\Users\\operator" };

        var code = ViewerSetupDiagnosticFactory.CreateSupportCode(snapshot);

        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(code, out var decoded));
        Assert.Equal(Sws1ViewerSetupPayload.UnknownMajor, decoded.ProductMajor);
        Assert.Equal(Sws1ViewerSetupPayload.UnknownMinorOrPatch, decoded.ProductMinor);
        Assert.Equal(Sws1ViewerSetupPayload.UnknownMinorOrPatch, decoded.ProductPatch);
    }

    [Theory]
    [InlineData("0.11")]
    [InlineData("0.11.10.2")]
    [InlineData("v0.11.10.2-poc")]
    [InlineData("0.+11.10")]
    public void MalformedProductVersionUsesReservedVersionValue(string version)
    {
        var code = ViewerSetupDiagnosticFactory.CreateSupportCode(
            CreateSnapshot() with { ProductVersion = version });

        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(code, out var decoded));
        Assert.Equal(Sws1ViewerSetupPayload.UnknownMajor, decoded.ProductMajor);
        Assert.Equal(Sws1ViewerSetupPayload.UnknownMinorOrPatch, decoded.ProductMinor);
        Assert.Equal(Sws1ViewerSetupPayload.UnknownMinorOrPatch, decoded.ProductPatch);
    }

    [Theory]
    [InlineData("v0.11.10-poc")]
    [InlineData("V0.11.10+commit")]
    public void OptionalVersionPrefixUsesNumericVersion(string version)
    {
        var code = ViewerSetupDiagnosticFactory.CreateSupportCode(
            CreateSnapshot() with { ProductVersion = version });

        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(code, out var decoded));
        Assert.Equal(0, decoded.ProductMajor);
        Assert.Equal(11, decoded.ProductMinor);
        Assert.Equal(10, decoded.ProductPatch);
    }

    [Fact]
    public void DiagnosticCodePositions_AreAppendOnlyProtocolValues()
    {
        Assert.Equal(0, (byte)ViewerSetupDiagnosticCode.Unknown);
        Assert.Equal(1, (byte)ViewerSetupDiagnosticCode.Ok);
        Assert.Equal(2, (byte)ViewerSetupDiagnosticCode.PackageInvalid);
        Assert.Equal(3, (byte)ViewerSetupDiagnosticCode.InstallWriteFailed);
        Assert.Equal(4, (byte)ViewerSetupDiagnosticCode.PackageNotFound);
        Assert.Equal(5, (byte)ViewerSetupDiagnosticCode.ManifestInvalid);
        Assert.Equal(6, (byte)ViewerSetupDiagnosticCode.PackageHashMismatch);
        Assert.Equal(7, (byte)ViewerSetupDiagnosticCode.PathInvalid);
        Assert.Equal(8, (byte)ViewerSetupDiagnosticCode.PathNotWritable);
        Assert.Equal(9, (byte)ViewerSetupDiagnosticCode.AlreadyRunning);
        Assert.Equal(10, (byte)ViewerSetupDiagnosticCode.RecoveryRequired);
        Assert.Equal(11, (byte)ViewerSetupDiagnosticCode.ViewerRunning);
        Assert.Equal(12, (byte)ViewerSetupDiagnosticCode.SmokeFailed);
        Assert.Equal(13, (byte)ViewerSetupDiagnosticCode.LaunchFailed);
        Assert.Equal(14, (byte)ViewerSetupDiagnosticCode.ShortcutFailed);
        Assert.Equal(15, (byte)ViewerSetupDiagnosticCode.RollbackFailed);
        Assert.Equal(16, (byte)ViewerSetupDiagnosticCode.Cancelled);
        Assert.Equal(17, (byte)ViewerSetupDiagnosticCode.Unexpected);
        Assert.Equal(18, (byte)ViewerSetupDiagnosticCode.QuarantineFailed);
    }

    [Fact]
    public void StateEnumPositions_AreStableProtocolValues()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<ViewerSetupDiagnosticOperation>().Select(value => (int)value).ToArray());
        Assert.Equal(Enumerable.Range(0, 16).ToArray(), Enum.GetValues<ViewerSetupDiagnosticStage>().Select(value => (int)value).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<ViewerSetupDiagnosticRollbackState>().Select(value => (int)value).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<ViewerSetupDiagnosticJournalState>().Select(value => (int)value).ToArray());
        Assert.Equal(Enumerable.Range(0, 8).ToArray(), Enum.GetValues<ViewerSetupDiagnosticQuarantineState>().Select(value => (int)value).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<ViewerSetupDiagnosticPreviousInstallState>().Select(value => (int)value).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<ViewerSetupDiagnosticStageState>().Select(value => (int)value).ToArray());
    }

    [Theory]
    [InlineData("OK", (byte)ViewerSetupDiagnosticCode.Ok)]
    [InlineData("VIEWER_SETUP_SMOKE_FAILED", (byte)ViewerSetupDiagnosticCode.SmokeFailed)]
    [InlineData("VIEWER_SETUP_ROLLBACK_FAILED", (byte)ViewerSetupDiagnosticCode.RollbackFailed)]
    [InlineData("VIEWER_SETUP_QUARANTINE_FAILED", (byte)ViewerSetupDiagnosticCode.QuarantineFailed)]
    [InlineData("C:\\Users\\operator\\secret", (byte)ViewerSetupDiagnosticCode.Unknown)]
    public void Factory_AllowsOnlyStableErrorCodes(string value, byte expected)
    {
        Assert.Equal(expected, (byte)ViewerSetupDiagnosticFactory.Code(value));
    }

    [Theory]
    [InlineData("VIEWER_SETUP_CANCELLED", false)]
    [InlineData("VIEWER_SETUP_ALREADY_RUNNING", false)]
    [InlineData("VIEWER_SETUP_SMOKE_FAILED", true)]
    [InlineData("VIEWER_SETUP_ROLLBACK_FAILED", true)]
    public void Policy_ShowsOnlyActionableFailureCodes(string code, bool expected)
    {
        Assert.Equal(expected, ViewerSetupSupportCodePolicy.ShouldShow(code, succeeded: false));
        Assert.False(ViewerSetupSupportCodePolicy.ShouldShow(code, succeeded: true));
    }

    [Fact]
    public void Policy_ShowsOnlyUnrecoverableRecoveryInspection()
    {
        var unreadable = CreateSnapshot() with
        {
            Operation = ViewerSetupDiagnosticOperation.RecoveryInspection,
            JournalState = ViewerSetupDiagnosticJournalState.Unreadable
        };
        var recoverable = unreadable with { JournalState = ViewerSetupDiagnosticJournalState.Recoverable };

        Assert.True(ViewerSetupSupportCodePolicy.ShouldShow(unreadable));
        Assert.False(ViewerSetupSupportCodePolicy.ShouldShow(recoverable));
        Assert.False(ViewerSetupSupportCodePolicy.ShouldShow((ViewerSetupDiagnosticSnapshot?)null));
    }

    [Fact]
    public void UiPolicy_HidesNormalAndRecoverableStates()
    {
        Assert.False(ViewerSetupUiPolicy.ShouldShowSupportCode(
            ViewerSetupResult.Success("ok", [])));
        Assert.False(ViewerSetupUiPolicy.ShouldShowSupportCode(
            ViewerSetupResult.Failure("VIEWER_SETUP_CANCELLED", "cancelled", [])));
        Assert.False(ViewerSetupUiPolicy.ShouldShowSupportCode(
            ViewerSetupResult.Failure("VIEWER_SETUP_ALREADY_RUNNING", "running", [])));
        Assert.True(ViewerSetupUiPolicy.ShouldShowSupportCode(
            ViewerSetupResult.Failure("VIEWER_SETUP_SMOKE_FAILED", "failed", [])));

        Assert.False(ViewerSetupUiPolicy.ShouldShowSupportCode(ViewerRecoveryInspection.None));
        Assert.False(ViewerSetupUiPolicy.ShouldShowSupportCode(
            new ViewerRecoveryInspection(true, true, "VIEWER_SETUP_RECOVERY_REQUIRED", "recover")));
        Assert.True(ViewerSetupUiPolicy.ShouldShowSupportCode(
            new ViewerRecoveryInspection(true, false, "VIEWER_SETUP_RECOVERY_REQUIRED", "blocked")));
    }

    [Fact]
    public void CopyPolicy_CopiesExactCodeAndReturnsSafeFeedback()
    {
        string? copied = null;

        var feedback = MainWindow.TryCopySupportCode(
            "SWS1-82S9-FCK7-ANB1-G00T",
            value => copied = value);

        Assert.Equal("SWS1-82S9-FCK7-ANB1-G00T", copied);
        Assert.Equal("지원 코드를 복사했습니다.", feedback);
    }

    [Fact]
    public void CopyPolicy_ClipboardFailureIsActionableAndDoesNotLeakException()
    {
        var feedback = MainWindow.TryCopySupportCode(
            "SWS1-82S9-FCK7-ANB1-G00T",
            _ => throw new System.Runtime.InteropServices.ExternalException("sensitive clipboard detail"));

        Assert.Equal("복사하지 못했습니다. 코드를 선택한 뒤 Ctrl+C를 누르세요.", feedback);
        Assert.DoesNotContain("sensitive", feedback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyPolicy_ClipboardUnavailableIsActionableAndDoesNotEscape()
    {
        var feedback = MainWindow.TryCopySupportCode(
            "SWS1-82S9-FCK7-ENB1-G06M",
            _ => throw new InvalidOperationException("clipboard unavailable"));

        Assert.Equal("복사하지 못했습니다. 코드를 선택한 뒤 Ctrl+C를 누르세요.", feedback);
    }

    [Fact]
    public async Task DeploymentFailure_UsesActualStageAndRollbackMetadataInSws1()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(
            Path.Combine(workspace.InstallDirectory, "unverified.txt"),
            "preserve");
        workspace.Process.SmokeSucceeds = false;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Diagnostic);
        var code = ViewerSetupDiagnosticFactory.CreateSupportCode(result.Diagnostic!);
        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(code, out var decoded));
        Assert.Equal(ViewerSetupDiagnosticCode.SmokeFailed, decoded.FinalCode);
        Assert.Equal(ViewerSetupDiagnosticCode.SmokeFailed, decoded.PrimaryCode);
        Assert.Equal(ViewerSetupDiagnosticStage.Smoke, decoded.FailedStage);
        Assert.Equal(ViewerSetupDiagnosticRollbackState.Succeeded, decoded.RollbackState);
        Assert.Equal(ViewerSetupDiagnosticJournalState.None, decoded.JournalState);
        Assert.Equal(ViewerSetupDiagnosticQuarantineState.Restored, decoded.QuarantineState);
        Assert.Equal(ViewerSetupDiagnosticPreviousInstallState.Invalid, decoded.PreviousInstallState);
        Assert.Equal(ViewerSetupDiagnosticStageState.Failed, decoded.Stages.Smoke);
        Assert.Equal(ViewerSetupDiagnosticStageState.Succeeded, decoded.Stages.Recovery);
    }

    [Fact]
    public async Task PackageFailure_PreservesInternalPrimaryCodeWithoutUntrustedText()
    {
        using var workspace = new TestWorkspace();

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.PackageInvalid, result.Code);
        Assert.NotNull(result.Diagnostic);
        var code = ViewerSetupDiagnosticFactory.CreateSupportCode(result.Diagnostic!);
        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(code, out var decoded));
        Assert.Equal(ViewerSetupDiagnosticCode.PackageInvalid, decoded.FinalCode);
        Assert.Equal(ViewerSetupDiagnosticCode.PackageNotFound, decoded.PrimaryCode);
        Assert.Equal(ViewerSetupDiagnosticStage.Package, decoded.FailedStage);
        Assert.Equal(ViewerSetupDiagnosticStageState.Failed, decoded.Stages.Package);
    }

    [Fact]
    public async Task UnreadableRecoveryJournal_UsesActualInspectionAndRecoverySnapshots()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.OperationsDirectory);
        File.WriteAllText(workspace.Paths.JournalPath, "{\"formatVersion\":999}");
        var orchestrator = workspace.CreateOrchestrator();

        var inspection = orchestrator.InspectPendingRecovery();
        var recovery = await orchestrator.RecoverAsync();

        Assert.True(inspection.Exists);
        Assert.False(inspection.CanRecover);
        Assert.NotNull(inspection.Diagnostic);
        var inspectionCode = ViewerSetupDiagnosticFactory.CreateSupportCode(
            inspection.Diagnostic!);
        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(inspectionCode, out var inspectionDecoded));
        Assert.Equal(ViewerSetupDiagnosticOperation.RecoveryInspection, inspectionDecoded.Operation);
        Assert.Equal(ViewerSetupDiagnosticJournalState.Unreadable, inspectionDecoded.JournalState);
        Assert.Equal(ViewerSetupDiagnosticStage.RecoveryGate, inspectionDecoded.FailedStage);

        Assert.False(recovery.Succeeded);
        Assert.NotNull(recovery.Diagnostic);
        var recoveryCode = ViewerSetupDiagnosticFactory.CreateSupportCode(recovery.Diagnostic!);
        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(recoveryCode, out var recoveryDecoded));
        Assert.Equal(ViewerSetupDiagnosticOperation.Recovery, recoveryDecoded.Operation);
        Assert.Equal(ViewerSetupDiagnosticJournalState.Unreadable, recoveryDecoded.JournalState);
        Assert.Equal(ViewerSetupDiagnosticRollbackState.NotRun, recoveryDecoded.RollbackState);
        Assert.Equal(ViewerSetupDiagnosticStage.RecoveryGate, recoveryDecoded.FailedStage);
    }

    [Fact]
    public async Task RollbackFailure_DoesNotOverwritePrimaryFailureInSws1()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.Shortcuts.FailDesktopCreate = true;
        workspace.Shortcuts.FailRestore = true;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.NotNull(result.Diagnostic);
        var code = ViewerSetupDiagnosticFactory.CreateSupportCode(result.Diagnostic!);
        Assert.True(Sws1ViewerSetupSupportCode.TryDecode(code, out var decoded));
        Assert.Equal(ViewerSetupDiagnosticCode.RollbackFailed, decoded.FinalCode);
        Assert.Equal(ViewerSetupDiagnosticCode.ShortcutFailed, decoded.PrimaryCode);
        Assert.Equal(ViewerSetupDiagnosticStage.Shortcut, decoded.FailedStage);
        Assert.Equal(ViewerSetupDiagnosticRollbackState.Failed, decoded.RollbackState);
        Assert.Equal(ViewerSetupDiagnosticJournalState.Recoverable, decoded.JournalState);
        Assert.Equal(ViewerSetupDiagnosticStageState.Failed, decoded.Stages.Shortcut);
        Assert.Equal(ViewerSetupDiagnosticStageState.Failed, decoded.Stages.Recovery);
    }

    private static ViewerSetupDiagnosticSnapshot CreateSnapshot() => new(
        "0.11.10-poc",
        ViewerSetupDiagnosticOperation.Install,
        ViewerSetupDiagnosticCode.RollbackFailed,
        ViewerSetupDiagnosticCode.SmokeFailed,
        ViewerSetupDiagnosticStage.Smoke,
        ViewerSetupDiagnosticRollbackState.Failed,
        ViewerSetupDiagnosticJournalState.Recoverable,
        ViewerSetupDiagnosticQuarantineState.RestoreFailed,
        ViewerSetupDiagnosticPreviousInstallState.Invalid,
        new ViewerSetupDiagnosticStageStates(
            ViewerSetupDiagnosticStageState.Succeeded,
            ViewerSetupDiagnosticStageState.Succeeded,
            ViewerSetupDiagnosticStageState.Succeeded,
            ViewerSetupDiagnosticStageState.Succeeded,
            ViewerSetupDiagnosticStageState.Succeeded,
            ViewerSetupDiagnosticStageState.Failed,
            ViewerSetupDiagnosticStageState.NotRun,
            ViewerSetupDiagnosticStageState.NotRun,
            ViewerSetupDiagnosticStageState.Failed));
}
