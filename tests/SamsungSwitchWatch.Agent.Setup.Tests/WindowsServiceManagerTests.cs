using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class WindowsServiceManagerTests
{
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;

    [Fact]
    public void ServiceSnapshot_LegacyOptionalMarkersPreserveKnownValues()
    {
        var snapshot = new ServiceSnapshot(
            true,
            false,
            "\"agent.exe\" --service",
            2,
            @"NT SERVICE\SamsungSwitchWatchAgent",
            SetupConstants.ServiceDisplayName,
            "legacy description",
            1,
            WindowsServiceManager.CreateAutomaticRecoveryPolicy(),
            [1, 2, 3],
            0);

        Assert.True(snapshot.HasKnownDescription);
        Assert.True(snapshot.HasKnownRecovery);
        Assert.True(snapshot.HasKnownSecurityDescriptor);
    }

    [Fact]
    public void ServiceSnapshot_ExplicitUnknownOptionalMarkersStayUnknown()
    {
        var snapshot = ServiceSnapshot.Missing with
        {
            Exists = true,
            DescriptionCaptured = false,
            RecoveryCaptured = false,
            SecurityDescriptorCaptured = false
        };

        Assert.False(snapshot.HasKnownDescription);
        Assert.False(snapshot.HasKnownRecovery);
        Assert.False(snapshot.HasKnownSecurityDescriptor);
    }

    [Fact]
    public void ServiceTimeoutException_PreservesTimeoutAsClassifiableCause()
    {
        var exception = WindowsServiceManager.CreateServiceTimeoutException(
            "safe service timeout");

        Assert.Equal(SetupErrorCodes.ServiceFailed, exception.Code);
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact]
    public void ServiceSnapshotStep_ReportsOnlySanitizedOptionalMetadataWarnings()
    {
        var steps = new SetupStepRecorder();
        var snapshot = ServiceSnapshot.Missing with
        {
            Exists = true,
            DescriptionCaptured = false,
            RecoveryCaptured = false,
            SecurityDescriptorCaptured = false
        };

        SetupDiagnosticsService.AddServiceSnapshotStep(steps, snapshot);

        Assert.Contains(
            steps,
            step => step.Code == SetupErrorCodes.ServiceDescriptionWarning &&
                    step.State == SetupStepState.Warning);
        Assert.Contains(
            steps,
            step => step.Code == SetupErrorCodes.ServiceRecoveryPolicyWarning &&
                    step.State == SetupStepState.Warning);
        Assert.Contains(
            steps,
            step => step.Code == "SERVICE_SECURITY_PRESERVED" &&
                    step.State == SetupStepState.Warning);
        Assert.DoesNotContain(
            steps,
            step => step.Message.Contains("agent.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CaptureAccess_UsesReadOnlyScmAndServiceRights()
    {
        Assert.Equal(0x00000001u, WindowsServiceManager.ScManagerConnectAccess);
        Assert.Equal(0x00000005u, WindowsServiceManager.ServiceCaptureAccess);
        Assert.Equal(
            0x00020000u,
            WindowsServiceManager.ServiceSecurityCaptureAccess);
        const uint mutationRights =
            0x00000002 | // SERVICE_CHANGE_CONFIG
            0x00000010 | // SERVICE_START
            0x00000020 | // SERVICE_STOP
            0x00010000 | // DELETE
            0x00040000 | // WRITE_DAC
            0x00080000;  // WRITE_OWNER
        Assert.Equal(0u, WindowsServiceManager.ServiceCaptureAccess & mutationRights);
        Assert.Equal(
            0u,
            WindowsServiceManager.ServiceSecurityCaptureAccess & mutationRights);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(1060, false)]
    [InlineData(1072, false)]
    public void SecurityDescriptorReadError_OnlyAccessDeniedIsOptional(
        int error,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsServiceManager.IsOptionalSecurityDescriptorReadError(error));
    }

    [Theory]
    [InlineData(1072, true)]
    [InlineData(1060, false)]
    [InlineData(5, false)]
    public void ServiceDeletionPendingError_OnlyMarkedForDeleteKeepsWaiting(
        int error,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsServiceManager.IsServiceDeletionPendingError(error));
    }

    [Theory]
    [InlineData(1060, true)]
    [InlineData(1072, false)]
    [InlineData(5, false)]
    public void ServiceDeletionCompleteError_OnlyMissingServiceCompletesWait(
        int error,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsServiceManager.IsServiceDeletionCompleteError(error));
    }

    [Fact]
    public void OperationAccess_UsesOnlyRequiredServiceRights()
    {
        Assert.Equal(0x00000002u, WindowsServiceManager.ScManagerCreateServiceAccess);
        Assert.Equal(0x00000002u, WindowsServiceManager.ServiceChangeConfigAccess);
        Assert.Equal(0x00000001u, WindowsServiceManager.ServiceQueryConfigAccess);
        Assert.Equal(0x00000004u, WindowsServiceManager.ServiceQueryStatusAccess);
        Assert.Equal(0x00000010u, WindowsServiceManager.ServiceStartAccess);
        Assert.Equal(0x00000020u, WindowsServiceManager.ServiceStopAccess);
        Assert.Equal(0x00010000u, WindowsServiceManager.ServiceDeleteAccess);
        Assert.Equal(0x00040000u, WindowsServiceManager.ServiceWriteDacAccess);
        Assert.Equal(
            0u,
            (WindowsServiceManager.ServiceChangeConfigAccess |
             WindowsServiceManager.ServiceQueryConfigAccess |
             WindowsServiceManager.ServiceQueryStatusAccess |
             WindowsServiceManager.ServiceStartAccess |
             WindowsServiceManager.ServiceStopAccess |
             WindowsServiceManager.ServiceDeleteAccess) &
            WindowsServiceManager.ServiceSecurityCaptureAccess);
    }

    [Fact]
    public void CreateDisabledRecoveryPolicy_RemovesActionsAndNonCrashRestart()
    {
        var policy = WindowsServiceManager.CreateDisabledRecoveryPolicy();

        Assert.Equal(0u, policy.ResetPeriod);
        Assert.False(policy.ApplyOnNonCrashFailures);
        Assert.Empty(policy.Actions);
        Assert.Empty(policy.RebootMessage);
        Assert.Empty(policy.Command);
    }

    [Fact]
    public void EmptyRecoveryPolicy_AllocatesNonNullNativeActionBuffer()
    {
        var size = WindowsServiceManager.GetRecoveryActionsAllocationSize(0);

        Assert.True(size > 0);
    }

    [Fact]
    public void CreateAutomaticRecoveryPolicy_PreservesBoundedRestartSchedule()
    {
        var policy = WindowsServiceManager.CreateAutomaticRecoveryPolicy();

        Assert.Equal(86400u, policy.ResetPeriod);
        Assert.True(policy.ApplyOnNonCrashFailures);
        Assert.Equal(
            new[] { 1, 1, 1 },
            policy.Actions.Select(action => action.Type));
        Assert.Equal(
            new uint[] { 5000, 15000, 60000 },
            policy.Actions.Select(action => action.Delay));
    }

    [Theory]
    [InlineData(ServiceStopped, true, true)]
    [InlineData(ServiceStopped, false, false)]
    [InlineData(ServiceRunning, true, false)]
    [InlineData(ServiceStopPending, true, false)]
    public void IsStopComplete_RequiresStoppedScmStateAndEveryObservedProcessExit(
        uint currentState,
        bool processExited,
        bool expected)
    {
        var completed = WindowsServiceManager.IsStopComplete(
            currentState,
            [true, processExited]);

        Assert.Equal(expected, completed);
    }

    [Fact]
    public void ShouldRequestStop_SendsOncePerRunningProcessAndTracksRestartPid()
    {
        var requested = new HashSet<int>();

        Assert.True(WindowsServiceManager.ShouldRequestStop(
            ServiceRunning,
            100,
            requested));
        Assert.False(WindowsServiceManager.ShouldRequestStop(
            ServiceRunning,
            100,
            requested));
        Assert.True(WindowsServiceManager.ShouldRequestStop(
            ServiceRunning,
            101,
            requested));
        Assert.False(WindowsServiceManager.ShouldRequestStop(
            ServiceStopped,
            0,
            requested));
        Assert.False(WindowsServiceManager.ShouldRequestStop(
            ServiceStartPending,
            102,
            requested));
        Assert.False(WindowsServiceManager.ShouldRequestStop(
            ServiceStopPending,
            102,
            requested));
    }

    [Fact]
    public void DecideStartAction_HandlesKnownScmStatesExplicitly()
    {
        Assert.Equal(
            ServiceStartAction.Complete,
            WindowsServiceManager.DecideStartAction(ServiceRunning));
        Assert.Equal(
            ServiceStartAction.Start,
            WindowsServiceManager.DecideStartAction(ServiceStopped));
        Assert.Equal(
            ServiceStartAction.WaitForRunning,
            WindowsServiceManager.DecideStartAction(ServiceStartPending));
        Assert.Equal(
            ServiceStartAction.WaitForStopped,
            WindowsServiceManager.DecideStartAction(ServiceStopPending));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void DecideStartAction_RejectsUnsupportedScmStates(uint currentState)
    {
        Assert.Equal(
            ServiceStartAction.Reject,
            WindowsServiceManager.DecideStartAction(currentState));
    }

    [Fact]
    public void RunStartStateMachine_DoesNotMissAutomaticRestartAfterStopPending()
    {
        var states = new Queue<uint>(
        [
            ServiceStopPending,
            ServiceStartPending,
            ServiceRunning
        ]);
        var elapsed = TimeSpan.Zero;
        var startRequests = 0;

        WindowsServiceManager.RunStartStateMachine(
            () => states.Dequeue(),
            () =>
            {
                startRequests++;
                return true;
            },
            TimeSpan.FromSeconds(1),
            () => elapsed,
            delay => elapsed += delay);

        Assert.Equal(0, startRequests);
        Assert.Empty(states);
        Assert.True(elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RunStartStateMachine_StartsStoppedServiceAndUsesSameBudget()
    {
        var states = new Queue<uint>(
        [
            ServiceStopped,
            ServiceStartPending,
            ServiceRunning
        ]);
        var elapsed = TimeSpan.Zero;
        var startRequests = 0;

        WindowsServiceManager.RunStartStateMachine(
            () => states.Dequeue(),
            () =>
            {
                startRequests++;
                return true;
            },
            TimeSpan.FromSeconds(1),
            () => elapsed,
            delay => elapsed += delay);

        Assert.Equal(1, startRequests);
        Assert.Empty(states);
        Assert.True(elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RunStartStateMachine_StopPendingTimesOutWithinSingleBudget()
    {
        var elapsed = TimeSpan.Zero;
        var stateReads = 0;

        var exception = Assert.Throws<SetupException>(() =>
            WindowsServiceManager.RunStartStateMachine(
                () =>
                {
                    stateReads++;
                    return ServiceStopPending;
                },
                () =>
                {
                    Assert.Fail("STOP_PENDING must not request a start.");
                    return true;
                },
                TimeSpan.FromMilliseconds(450),
                () => elapsed,
                delay => elapsed += delay));

        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(TimeSpan.FromMilliseconds(450), elapsed);
        Assert.InRange(stateReads, 2, 4);
    }

    [Fact]
    public void RunStartStateMachine_PersistentStoppedAfterAcceptedStartDoesNotRequestAgain()
    {
        var elapsed = TimeSpan.Zero;
        var startRequests = 0;

        var exception = Assert.Throws<SetupException>(() =>
            WindowsServiceManager.RunStartStateMachine(
                () => ServiceStopped,
                () =>
                {
                    startRequests++;
                    return true;
                },
                TimeSpan.FromMilliseconds(450),
                () => elapsed,
                delay => elapsed += delay));

        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, startRequests);
        Assert.Equal(TimeSpan.FromMilliseconds(450), elapsed);
    }

    [Fact]
    public void RunStartStateMachine_RecurrentStoppedAfterAcceptedStartDoesNotRequestAgain()
    {
        var states = new Queue<uint>(
        [
            ServiceStopped,
            ServiceStartPending,
            ServiceStopped,
            ServiceStopped
        ]);
        var elapsed = TimeSpan.Zero;
        var startRequests = 0;

        var exception = Assert.Throws<SetupException>(() =>
            WindowsServiceManager.RunStartStateMachine(
                () => states.Count > 0 ? states.Dequeue() : ServiceStopped,
                () =>
                {
                    startRequests++;
                    return true;
                },
                TimeSpan.FromMilliseconds(450),
                () => elapsed,
                delay => elapsed += delay));

        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, startRequests);
        Assert.Equal(TimeSpan.FromMilliseconds(450), elapsed);
    }

    [Fact]
    public void RunStartStateMachine_FailedConcurrentStartCanRequestAfterServiceStops()
    {
        var states = new Queue<uint>(
        [
            ServiceStopped,
            ServiceStopPending,
            ServiceStopped,
            ServiceStartPending,
            ServiceRunning
        ]);
        var elapsed = TimeSpan.Zero;
        var startCalls = 0;

        WindowsServiceManager.RunStartStateMachine(
            () => states.Dequeue(),
            () =>
            {
                startCalls++;
                return startCalls > 1;
            },
            TimeSpan.FromSeconds(1),
            () => elapsed,
            delay => elapsed += delay);

        Assert.Equal(2, startCalls);
        Assert.Empty(states);
    }

    [Theory]
    [InlineData(ServiceRunning, true)]
    [InlineData(ServiceStartPending, true)]
    [InlineData(ServiceStopPending, true)]
    [InlineData(ServiceStopped, false)]
    [InlineData(7u, false)]
    public void FailedStartOnlyContinuesForObservedConcurrentTransition(
        uint observedState,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsServiceManager.CanContinueAfterFailedStart(observedState));
    }

    [Theory]
    [InlineData(0, 1000, false)]
    [InlineData(999, 1000, false)]
    [InlineData(1000, 1000, true)]
    [InlineData(1001, 1000, true)]
    [InlineData(0, 0, true)]
    [InlineData(0, -1, true)]
    public void HasReachedServiceOperationTimeout_UsesElapsedMonotonicBudget(
        int elapsedMilliseconds,
        int timeoutMilliseconds,
        bool expected)
    {
        var timedOut = WindowsServiceManager.HasReachedServiceOperationTimeout(
            TimeSpan.FromMilliseconds(elapsedMilliseconds),
            TimeSpan.FromMilliseconds(timeoutMilliseconds));

        Assert.Equal(expected, timedOut);
    }

    [Fact]
    public void FakeServiceManager_DisableAndConfigureRecoveryFollowContract()
    {
        var service = new FakeServiceManager(new ServiceSnapshot(
            true,
            false,
            "\"agent.exe\" --service",
            2,
            @"NT SERVICE\SamsungSwitchWatchAgent",
            SetupConstants.ServiceDisplayName,
            string.Empty,
            1,
            WindowsServiceManager.CreateAutomaticRecoveryPolicy(),
            [],
            0));

        service.DisableRecovery(SetupConstants.ServiceName);

        Assert.False(service.State.Recovery.ApplyOnNonCrashFailures);
        Assert.Empty(service.State.Recovery.Actions);
        Assert.Contains("recovery-disabled", service.Operations);

        service.ConfigureRecovery(SetupConstants.ServiceName);

        Assert.True(service.State.Recovery.ApplyOnNonCrashFailures);
        Assert.Equal(
            new uint[] { 5000, 15000, 60000 },
            service.State.Recovery.Actions.Select(action => action.Delay));
        Assert.Contains("recovery", service.Operations);
    }
}
