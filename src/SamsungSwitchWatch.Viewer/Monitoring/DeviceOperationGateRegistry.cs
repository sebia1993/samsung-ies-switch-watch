namespace SamsungSwitchWatch.Viewer.Monitoring;

internal sealed class DeviceOperationGateRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, GateEntry> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    internal int Count
    {
        get
        {
            lock (_sync) return _gates.Count;
        }
    }

    public async ValueTask<DeviceOperationLease> AcquireAsync(
        string deviceKey,
        CancellationToken cancellationToken)
    {
        var (normalized, entry) = AddReference(deviceKey);
        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DeviceOperationLease(this, normalized, entry);
        }
        catch
        {
            ReleaseReference(normalized, entry, acquired: false);
            throw;
        }
    }

    public async ValueTask<DeviceOperationLease?> TryAcquireAsync(
        string deviceKey,
        CancellationToken cancellationToken)
    {
        var (normalized, entry) = AddReference(deviceKey);
        try
        {
            if (!await entry.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                ReleaseReference(normalized, entry, acquired: false);
                return null;
            }
            return new DeviceOperationLease(this, normalized, entry);
        }
        catch
        {
            ReleaseReference(normalized, entry, acquired: false);
            throw;
        }
    }

    private (string Normalized, GateEntry Entry) AddReference(string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        var normalized = deviceKey.Trim();
        lock (_sync)
        {
            if (!_gates.TryGetValue(normalized, out var entry))
            {
                entry = new GateEntry();
                _gates[normalized] = entry;
            }
            entry.References++;
            return (normalized, entry);
        }
    }

    private void Release(string normalized, GateEntry entry) =>
        ReleaseReference(normalized, entry, acquired: true);

    private void ReleaseReference(
        string normalized,
        GateEntry entry,
        bool acquired)
    {
        if (acquired)
        {
            entry.Gate.Release();
        }

        var dispose = false;
        lock (_sync)
        {
            entry.References--;
            if (entry.References == 0
                && _gates.TryGetValue(normalized, out var current)
                && ReferenceEquals(current, entry))
            {
                _gates.Remove(normalized);
                dispose = true;
            }
        }
        if (dispose)
        {
            entry.Gate.Dispose();
        }
    }

    internal sealed class GateEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }

    internal sealed class DeviceOperationLease(
        DeviceOperationGateRegistry owner,
        string normalized,
        GateEntry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(normalized, entry);
            }
        }
    }
}
