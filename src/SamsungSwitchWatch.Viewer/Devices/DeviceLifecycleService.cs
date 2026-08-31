using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Monitoring;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Devices;

internal sealed record DeviceSaveLifecycleResult(
    ManagedDeviceProfile Profile,
    IReadOnlyList<SwitchEventDto> LifecycleEvents,
    string? WarningCode);

internal sealed record DeviceDeleteLifecycleResult(
    bool Removed,
    IReadOnlyList<SwitchEventDto> LifecycleEvents,
    string? WarningCode);

internal sealed record DeviceMonitoringLifecycleResult(
    ManagedDeviceProfile Profile,
    IReadOnlyList<SwitchEventDto> LifecycleEvents,
    string? WarningCode);

internal sealed class DeviceLifecycleService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _credentialBlocks = new(StringComparer.Ordinal);
    private long _nextRevision;

    public DeviceSaveLifecycleResult Save(
        ManagedDeviceStore deviceStore,
        ViewerMonitoringStore? monitoringStore,
        bool monitoringStoreOperational,
        ManagedDeviceDraft draft,
        Action<string>? revisionAdvanced = null)
    {
        ArgumentNullException.ThrowIfNull(deviceStore);
        ArgumentNullException.ThrowIfNull(draft);
        lock (_sync)
        {
            var outcome = deviceStore.SaveWithOutcome(draft);
            var profile = outcome.Profile;
            AdvanceUnsafe(profile.Id);
            revisionAdvanced?.Invoke(profile.Id);
            if (profile.ConnectionVerified)
            {
                _credentialBlocks.Remove(profile.Id);
            }

            IReadOnlyList<SwitchEventDto> lifecycleEvents = [];
            string? warningCode = null;
            if (monitoringStoreOperational
                && monitoringStore is not null
                && (outcome.ConnectionIdentityChanged || !profile.MonitoringEnabled))
            {
                warningCode = RunCleanup(
                    () => lifecycleEvents =
                        monitoringStore.ResetDeviceCollectionState(profile.Id),
                    warningCode);
            }
            else if (monitoringStoreOperational
                     && monitoringStore is not null
                     && profile.ConnectionVerified)
            {
                warningCode = RunCleanup(
                    () => monitoringStore.ClearCapabilities(profile.Id),
                    warningCode);
            }

            return new DeviceSaveLifecycleResult(
                profile,
                lifecycleEvents,
                warningCode);
        }
    }

    public DeviceDeleteLifecycleResult Delete(
        ManagedDeviceStore deviceStore,
        ViewerMonitoringStore? monitoringStore,
        bool monitoringStoreOperational,
        string deviceId,
        Action<string>? revisionAdvanced = null)
    {
        ArgumentNullException.ThrowIfNull(deviceStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            if (!deviceStore.Delete(deviceId))
            {
                return new DeviceDeleteLifecycleResult(false, [], null);
            }

            AdvanceUnsafe(deviceId);
            revisionAdvanced?.Invoke(deviceId);
            _credentialBlocks.Remove(deviceId);
            IReadOnlyList<SwitchEventDto> lifecycleEvents = [];
            var warningCode = monitoringStoreOperational && monitoringStore is not null
                ? RunCleanup(
                    () => lifecycleEvents =
                        monitoringStore.ResetDeviceCollectionState(deviceId),
                    null)
                : null;
            return new DeviceDeleteLifecycleResult(
                true,
                lifecycleEvents,
                warningCode);
        }
    }

    public DeviceMonitoringLifecycleResult SetMonitoring(
        ManagedDeviceStore deviceStore,
        ViewerMonitoringStore? monitoringStore,
        bool monitoringStoreOperational,
        string deviceId,
        bool enabled,
        Action<string>? revisionAdvanced = null)
    {
        ArgumentNullException.ThrowIfNull(deviceStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            var profile = deviceStore.SetMonitoring(deviceId, enabled);
            AdvanceUnsafe(deviceId);
            revisionAdvanced?.Invoke(deviceId);
            IReadOnlyList<SwitchEventDto> lifecycleEvents = [];
            var warningCode = monitoringStoreOperational && monitoringStore is not null
                ? RunCleanup(
                    () => lifecycleEvents =
                        monitoringStore.ResetDeviceCollectionState(deviceId),
                    null)
                : null;
            return new DeviceMonitoringLifecycleResult(
                profile,
                lifecycleEvents,
                warningCode);
        }
    }

    public T ReadConsistent<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_sync) return read();
    }

    public void UpdateConsistent(Action update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync) update();
    }

    public T UpdateConsistent<T>(Func<T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync) return update();
    }

    public long GetOrCreateRevision(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            if (_revisions.TryGetValue(deviceId, out var revision))
            {
                return revision;
            }
            return AdvanceUnsafe(deviceId);
        }
    }

    public long AdvanceRevision(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync) return AdvanceUnsafe(deviceId);
    }

    public bool IsCurrent(DeviceLifecycleToken token)
    {
        lock (_sync)
        {
            return _revisions.TryGetValue(token.DeviceId, out var revision)
                   && revision == token.Revision;
        }
    }

    public bool IsCurrent(string deviceId, long revision)
    {
        lock (_sync)
        {
            return _revisions.TryGetValue(deviceId, out var current)
                   && current == revision;
        }
    }

    public bool IsCredentialBlocked(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync) return _credentialBlocks.Contains(deviceId);
    }

    public void BlockForCredentialFailure(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync) _credentialBlocks.Add(deviceId);
    }

    public void ClearCredentialBlock(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        lock (_sync) _credentialBlocks.Remove(deviceId);
    }

    private long AdvanceUnsafe(string deviceId)
    {
        var revision = ++_nextRevision;
        _revisions[deviceId] = revision;
        return revision;
    }

    private static string? RunCleanup(Action action, string? currentWarning)
    {
        try
        {
            action();
            return currentWarning;
        }
        catch (Exception exception)
        {
            return currentWarning
                   ?? (exception is IOException or UnauthorizedAccessException
                       ? "VIEWER_MONITOR_STATE_WRITE_FAILED"
                       : "VIEWER_UNEXPECTED_ERROR");
        }
    }
}
