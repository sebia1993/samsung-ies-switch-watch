using SamsungSwitchWatch.Viewer.Models;
using System.Diagnostics.CodeAnalysis;
using SamsungSwitchWatch.Viewer.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Events;

internal sealed class EventFeedCoordinator
{
    private readonly object _sync = new();
    private readonly SortedDictionary<long, AgentEventChangeDto> _buffer = [];
    private readonly HashSet<long> _liveAlertSequences = [];
    private readonly int _capacity;
    private bool _overflowed;
    private long _signalVersion;
    private long _observedSignalVersion;
    private int _pumpScheduled;
    private int _pumpStartCount;

    public EventFeedCoordinator(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _capacity = capacity;
    }

    public int BufferedCount
    {
        get
        {
            lock (_sync) return _buffer.Count;
        }
    }

    public bool IsPumpScheduled => Volatile.Read(ref _pumpScheduled) != 0;
    public long ObservedSignalVersion => Interlocked.Read(ref _observedSignalVersion);
    public int PumpStartCount => Volatile.Read(ref _pumpStartCount);
    public long SignalVersion => Interlocked.Read(ref _signalVersion);

    public void Buffer(
        IEnumerable<AgentEventChangeDto> changes,
        long appliedCursor,
        bool live,
        bool allowLiveAlerts)
    {
        lock (_sync)
        {
            foreach (var change in changes)
            {
                if (change.ChangeSequence <= appliedCursor) continue;
                if (_overflowed) break;

                if (!_buffer.ContainsKey(change.ChangeSequence)
                    && _buffer.Count >= _capacity)
                {
                    _buffer.Clear();
                    _liveAlertSequences.Clear();
                    _overflowed = true;
                    ViewerRuntimeDiagnostics.RecordEventBufferOverflow();
                    break;
                }

                _buffer[change.ChangeSequence] = change;
                if (live && allowLiveAlerts)
                {
                    _liveAlertSequences.Add(change.ChangeSequence);
                }
            }
            ViewerRuntimeDiagnostics.SetEventBufferCount(_buffer.Count);
        }
    }

    public bool TryTakeNext(
        long appliedCursor,
        [NotNullWhen(true)] out AgentEventChangeDto? change,
        out bool liveAlert)
    {
        lock (_sync)
        {
            var next = appliedCursor + 1;
            if (!_buffer.Remove(next, out change))
            {
                liveAlert = false;
                return false;
            }
            liveAlert = _liveAlertSequences.Remove(next);
            ViewerRuntimeDiagnostics.SetEventBufferCount(_buffer.Count);
            return true;
        }
    }

    public bool ConsumeOverflow()
    {
        lock (_sync)
        {
            if (!_overflowed) return false;
            _overflowed = false;
            return true;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _buffer.Clear();
            _liveAlertSequences.Clear();
            _overflowed = false;
            ViewerRuntimeDiagnostics.SetEventBufferCount(0);
        }
    }

    public long Signal() => Interlocked.Increment(ref _signalVersion);

    public bool TrySchedulePump()
    {
        if (Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) != 0)
        {
            return false;
        }
        Interlocked.Increment(ref _pumpStartCount);
        return true;
    }

    public long ObserveSignal()
    {
        var observed = Interlocked.Read(ref _signalVersion);
        Interlocked.Exchange(ref _observedSignalVersion, observed);
        return observed;
    }

    public bool CompletePump(long observedSignalVersion, bool shuttingDown)
    {
        Interlocked.Exchange(ref _pumpScheduled, 0);
        return !shuttingDown
               && observedSignalVersion != Interlocked.Read(ref _signalVersion);
    }
}
