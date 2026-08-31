namespace SamsungSwitchWatch.Viewer.Monitoring;

internal enum DeviceCircuitState
{
    Closed,
    Open,
    HalfOpen
}

internal sealed record DeviceCircuitSnapshot(
    DeviceCircuitState State,
    int ConsecutiveAvailabilityFailures,
    DateTimeOffset? RetryAfterUtc,
    bool ProbeInFlight);

internal sealed class DeviceCircuitBreaker
{
    internal const int DefaultFailureThreshold = 3;
    internal static readonly TimeSpan DefaultOpenDuration = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;

    public DeviceCircuitBreaker(
        TimeProvider? timeProvider = null,
        int failureThreshold = DefaultFailureThreshold,
        TimeSpan? openDuration = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? DefaultOpenDuration;
        if (_failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }
        if (_openDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(openDuration));
        }
    }

    public bool TryEnter(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            var entry = GetOrCreate(deviceId);
            var now = _timeProvider.GetUtcNow();
            if (entry.State == DeviceCircuitState.Open)
            {
                if (entry.RetryAfterUtc > now)
                {
                    return false;
                }
                entry.State = DeviceCircuitState.HalfOpen;
            }

            if (entry.State == DeviceCircuitState.HalfOpen)
            {
                if (entry.ProbeInFlight)
                {
                    return false;
                }
                entry.ProbeInFlight = true;
            }
            return true;
        }
    }

    public void RecordSuccess(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            var entry = GetOrCreate(deviceId);
            entry.State = DeviceCircuitState.Closed;
            entry.ConsecutiveFailures = 0;
            entry.RetryAfterUtc = null;
            entry.ProbeInFlight = false;
        }
    }

    public void RecordAvailabilityFailure(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            var entry = GetOrCreate(deviceId);
            entry.ProbeInFlight = false;
            entry.ConsecutiveFailures++;
            if (entry.State == DeviceCircuitState.HalfOpen
                || entry.ConsecutiveFailures >= _failureThreshold)
            {
                entry.State = DeviceCircuitState.Open;
                entry.RetryAfterUtc = _timeProvider.GetUtcNow().Add(_openDuration);
            }
        }
    }

    public void RecordNonAvailabilityFailure(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            var entry = GetOrCreate(deviceId);
            if (entry.State == DeviceCircuitState.HalfOpen)
            {
                entry.State = DeviceCircuitState.Closed;
            }
            entry.ConsecutiveFailures = 0;
            entry.RetryAfterUtc = null;
            entry.ProbeInFlight = false;
        }
    }

    public void CancelAttempt(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }
        lock (_sync)
        {
            if (_entries.TryGetValue(deviceId, out var entry))
            {
                entry.ProbeInFlight = false;
            }
        }
    }

    public void Remove(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }
        lock (_sync)
        {
            _entries.Remove(deviceId);
        }
    }

    public DeviceCircuitSnapshot GetSnapshot(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            var entry = GetOrCreate(deviceId);
            var state = entry.State;
            if (state == DeviceCircuitState.Open
                && entry.RetryAfterUtc <= _timeProvider.GetUtcNow())
            {
                state = DeviceCircuitState.HalfOpen;
            }
            return new DeviceCircuitSnapshot(
                state,
                entry.ConsecutiveFailures,
                entry.RetryAfterUtc,
                entry.ProbeInFlight);
        }
    }

    private Entry GetOrCreate(string deviceId)
    {
        if (_entries.TryGetValue(deviceId, out var entry))
        {
            return entry;
        }

        entry = new Entry();
        _entries[deviceId] = entry;
        return entry;
    }

    private sealed class Entry
    {
        public DeviceCircuitState State { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset? RetryAfterUtc { get; set; }
        public bool ProbeInFlight { get; set; }
    }
}
