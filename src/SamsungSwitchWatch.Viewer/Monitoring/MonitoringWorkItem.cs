using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Monitoring;

internal sealed record DeviceLifecycleToken(string DeviceId, long Revision);

internal sealed record MonitoringWorkItem(
    ManagedDeviceProfile Profile,
    long Revision,
    long ClientEpoch,
    IAgentClient Client)
{
    public string DeviceId => Profile.Id;

    public DeviceLifecycleToken Token => new(Profile.Id, Revision);
}

internal sealed record MonitoringCycleResult(
    long CycleId,
    int Accepted,
    int Completed,
    int Failed,
    int Dropped,
    int Duplicate,
    bool Cancelled)
{
    public bool Succeeded =>
        !Cancelled && Failed == 0 && Dropped == 0 && Completed == Accepted;
}

internal sealed record MonitoringCoordinatorStatus(
    bool IsRunning,
    int QueueDepth,
    int ActiveWorkers,
    DateTimeOffset? LastCycleStartedUtc,
    DateTimeOffset? LastCycleCompletedUtc,
    DateTimeOffset? LastSuccessfulCycleUtc,
    long DroppedWorkItems,
    long CompletedWorkItems,
    long FailedWorkItems);
