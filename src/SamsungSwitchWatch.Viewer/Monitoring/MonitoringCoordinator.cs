using System.Threading.Channels;
using System.Diagnostics;
using SamsungSwitchWatch.Viewer.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Monitoring;

internal sealed class MonitoringCoordinator : IAsyncDisposable
{
    internal const int DefaultWorkerCount = 2;
    internal const int DefaultQueueCapacity = 256;

    private readonly Func<CancellationToken, Task<IReadOnlyList<MonitoringWorkItem>>> _captureWork;
    private readonly Func<MonitoringWorkItem, CancellationToken, Task> _processWork;
    private readonly Func<MonitoringCycleResult, CancellationToken, Task> _cycleCompleted;
    private readonly Action<Exception> _unhandledFailure;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly int _workerCount;
    private readonly int _capacity;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _queueSync = new();
    private readonly Dictionary<string, QueueEntry> _inFlightByDeviceId =
        new(StringComparer.Ordinal);

    private Channel<QueueEntry>? _channel;
    private CancellationTokenSource? _runCancellation;
    private Task? _schedulerTask;
    private Task[] _workerTasks = [];
    private bool _accepting;
    private int _running;
    private int _disposed;
    private int _queueDepth;
    private int _activeWorkers;
    private long _nextCycleId;
    private long _droppedWorkItems;
    private long _completedWorkItems;
    private long _failedWorkItems;
    private long _lastCycleStartedUtcTicks;
    private long _lastCycleCompletedUtcTicks;
    private long _lastSuccessfulCycleUtcTicks;

    public MonitoringCoordinator(
        Func<CancellationToken, Task<IReadOnlyList<MonitoringWorkItem>>> captureWork,
        Func<MonitoringWorkItem, CancellationToken, Task> processWork,
        Func<MonitoringCycleResult, CancellationToken, Task>? cycleCompleted = null,
        Action<Exception>? unhandledFailure = null,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null,
        int workerCount = DefaultWorkerCount,
        int capacity = DefaultQueueCapacity)
    {
        _captureWork = captureWork ?? throw new ArgumentNullException(nameof(captureWork));
        _processWork = processWork ?? throw new ArgumentNullException(nameof(processWork));
        _cycleCompleted = cycleCompleted ?? ((_, _) => Task.CompletedTask);
        _unhandledFailure = unhandledFailure ?? (_ => { });
        _interval = interval ?? TimeSpan.FromSeconds(60);
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
        if (workerCount is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        }
        if (capacity is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _workerCount = workerCount;
        _capacity = capacity;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _running) != 0)
            {
                return;
            }

            var channel = Channel.CreateBounded<QueueEntry>(
                new BoundedChannelOptions(_capacity)
                {
                    // DropOldest is implemented explicitly so the dropped
                    // device can be removed from duplicate tracking and its
                    // cycle completion can be settled deterministically.
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = _workerCount == 1,
                    AllowSynchronousContinuations = false
                });
            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

            lock (_queueSync)
            {
                _channel = channel;
                _runCancellation = runCancellation;
                _inFlightByDeviceId.Clear();
                Volatile.Write(ref _queueDepth, 0);
                ViewerRuntimeDiagnostics.SetMonitorQueueDepth(0);
                _accepting = true;
            }

            Volatile.Write(ref _running, 1);
            _workerTasks = Enumerable.Range(0, _workerCount)
                .Select(_ => WorkerLoopAsync(channel.Reader, runCancellation.Token))
                .ToArray();
            _schedulerTask = SchedulerLoopAsync(runCancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<MonitoringCycleResult> RequestImmediateCollectionAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _running) == 0)
        {
            throw new InvalidOperationException("Monitoring is not running.");
        }

        CancellationToken runCancellation;
        lock (_queueSync)
        {
            if (!_accepting || _runCancellation is null)
            {
                throw new InvalidOperationException("Monitoring is not accepting work.");
            }
            runCancellation = _runCancellation.Token;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runCancellation);
        return await RunCycleAsync(deviceId, linkedCancellation.Token).ConfigureAwait(false);
    }

    public MonitoringCoordinatorStatus GetStatus() =>
        new(
            Volatile.Read(ref _running) != 0,
            Math.Max(0, Volatile.Read(ref _queueDepth)),
            Math.Max(0, Volatile.Read(ref _activeWorkers)),
            ReadTimestamp(ref _lastCycleStartedUtcTicks),
            ReadTimestamp(ref _lastCycleCompletedUtcTicks),
            ReadTimestamp(ref _lastSuccessfulCycleUtcTicks),
            Interlocked.Read(ref _droppedWorkItems),
            Interlocked.Read(ref _completedWorkItems),
            Interlocked.Read(ref _failedWorkItems));

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _running) == 0)
            {
                return;
            }

            Task? scheduler;
            Task[] workers;
            CancellationTokenSource? runCancellation;
            Channel<QueueEntry>? channel;
            lock (_queueSync)
            {
                _accepting = false;
                channel = _channel;
                runCancellation = _runCancellation;
            }

            runCancellation?.Cancel();
            channel?.Writer.TryComplete();
            DrainPendingEntries(channel, cancelled: true);
            scheduler = _schedulerTask;
            workers = _workerTasks;
            Volatile.Write(ref _running, 0);

            var tasks = workers.AsEnumerable();
            if (scheduler is not null)
            {
                tasks = tasks.Append(scheduler);
            }
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stop owns this cancellation and all pending entries are
                // settled again below before the lifecycle gate is released.
                Trace.TraceInformation(
                    "Monitoring coordinator workers observed stop cancellation.");
            }
            catch (Exception exception)
            {
                ReportUnhandled(exception);
            }
            finally
            {
                DrainPendingEntries(channel, cancelled: true);
                lock (_queueSync)
                {
                    _channel = null;
                    _runCancellation = null;
                    _schedulerTask = null;
                    _workerTasks = [];
                    _inFlightByDeviceId.Clear();
                    Volatile.Write(ref _queueDepth, 0);
                    Volatile.Write(ref _activeWorkers, 0);
                    ViewerRuntimeDiagnostics.SetMonitorQueueDepth(0);
                    ViewerRuntimeDiagnostics.SetMonitorActiveWorkers(0);
                }
                runCancellation?.Dispose();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunCycleAsync(null, cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_interval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunCycleAsync(null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportUnhandled(exception);
        }
    }

    private async Task<MonitoringCycleResult> RunCycleAsync(
        string? deviceId,
        CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await EnqueueCycleAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task<MonitoringCycleResult> EnqueueCycleAsync(
        string? deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cycleId = Interlocked.Increment(ref _nextCycleId);
        var tracker = new CycleTracker(cycleId);
        WriteTimestamp(ref _lastCycleStartedUtcTicks, _timeProvider.GetUtcNow());

        IReadOnlyList<MonitoringWorkItem> captured;
        try
        {
            captured = await _captureWork(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tracker.Cancel();
            tracker.Seal();
            throw;
        }
        catch (Exception exception)
        {
            tracker.FailCapture();
            tracker.Seal();
            ReportUnhandled(exception);
            return await CompleteCycleAsync(tracker, CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var item in captured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deviceId is not null
                && !item.DeviceId.Equals(deviceId, StringComparison.Ordinal))
            {
                continue;
            }

            Enqueue(item, tracker);
        }

        tracker.Seal();
        return await CompleteCycleAsync(tracker, cancellationToken).ConfigureAwait(false);
    }

    private void Enqueue(MonitoringWorkItem item, CycleTracker tracker)
    {
        lock (_queueSync)
        {
            var channel = _channel;
            if (!_accepting || channel is null)
            {
                tracker.Cancel();
                return;
            }
            if (_inFlightByDeviceId.TryGetValue(item.DeviceId, out var existing))
            {
                tracker.FollowDuplicate();
                existing.Followers.Add(tracker);
                return;
            }

            while (Volatile.Read(ref _queueDepth) >= _capacity
                   && channel.Reader.TryRead(out var dropped))
            {
                Interlocked.Decrement(ref _queueDepth);
                ViewerRuntimeDiagnostics.SetMonitorQueueDepth(
                    Volatile.Read(ref _queueDepth));
                _inFlightByDeviceId.Remove(dropped.Work.DeviceId);
                Interlocked.Increment(ref _droppedWorkItems);
                ViewerRuntimeDiagnostics.RecordMonitorWorkDropped();
                dropped.Tracker.Complete(dropped: true, failed: false, cancelled: false);
                foreach (var follower in dropped.Followers)
                {
                    follower.CompleteFollower(dropped: true, failed: false, cancelled: false);
                }
            }

            var entry = new QueueEntry(item, tracker);
            if (!channel.Writer.TryWrite(entry))
            {
                Interlocked.Increment(ref _droppedWorkItems);
                ViewerRuntimeDiagnostics.RecordMonitorWorkDropped();
                tracker.MarkDropped();
                return;
            }

            _inFlightByDeviceId[item.DeviceId] = entry;
            tracker.Accept();
            Interlocked.Increment(ref _queueDepth);
            ViewerRuntimeDiagnostics.SetMonitorQueueDepth(
                Volatile.Read(ref _queueDepth));
        }
    }

    private async Task WorkerLoopAsync(
        ChannelReader<QueueEntry> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_queueSync)
                {
                    if (Volatile.Read(ref _queueDepth) > 0)
                    {
                        Interlocked.Decrement(ref _queueDepth);
                        ViewerRuntimeDiagnostics.SetMonitorQueueDepth(
                            Volatile.Read(ref _queueDepth));
                    }
                }

                var activeWorkers = Interlocked.Increment(ref _activeWorkers);
                ViewerRuntimeDiagnostics.SetMonitorActiveWorkers(activeWorkers);
                var startedTimestamp = Stopwatch.GetTimestamp();
                var failed = false;
                var cancelled = false;
                try
                {
                    await _processWork(entry.Work, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _completedWorkItems);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (Exception exception)
                {
                    failed = true;
                    Interlocked.Increment(ref _failedWorkItems);
                    ReportUnhandled(exception);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeWorkers);
                    ViewerRuntimeDiagnostics.SetMonitorActiveWorkers(
                        Volatile.Read(ref _activeWorkers));
                    ViewerRuntimeDiagnostics.RecordMonitorCollection(
                        Stopwatch.GetElapsedTime(startedTimestamp),
                        failed);
                    lock (_queueSync)
                    {
                        _inFlightByDeviceId.Remove(entry.Work.DeviceId);
                        foreach (var follower in entry.Followers)
                        {
                            follower.CompleteFollower(
                                dropped: false,
                                failed,
                                cancelled);
                        }
                    }
                    entry.Tracker.Complete(
                        dropped: false,
                        failed,
                        cancelled);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<MonitoringCycleResult> CompleteCycleAsync(
        CycleTracker tracker,
        CancellationToken cancellationToken)
    {
        var result = await tracker.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        WriteTimestamp(ref _lastCycleCompletedUtcTicks, now);
        if (result.Succeeded)
        {
            WriteTimestamp(ref _lastSuccessfulCycleUtcTicks, now);
        }

        try
        {
            await _cycleCompleted(result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportUnhandled(exception);
        }
        return result;
    }

    private void DrainPendingEntries(Channel<QueueEntry>? channel, bool cancelled)
    {
        if (channel is null)
        {
            return;
        }

        lock (_queueSync)
        {
            while (channel.Reader.TryRead(out var entry))
            {
                if (Volatile.Read(ref _queueDepth) > 0)
                {
                    Interlocked.Decrement(ref _queueDepth);
                    ViewerRuntimeDiagnostics.SetMonitorQueueDepth(
                        Volatile.Read(ref _queueDepth));
                }
                _inFlightByDeviceId.Remove(entry.Work.DeviceId);
                entry.Tracker.Complete(
                    dropped: false,
                    failed: false,
                    cancelled);
                foreach (var follower in entry.Followers)
                {
                    follower.CompleteFollower(
                        dropped: false,
                        failed: false,
                        cancelled);
                }
            }
        }
    }

    private void ReportUnhandled(Exception exception)
    {
        try
        {
            _unhandledFailure(exception);
        }
        catch (Exception observerException)
        {
            // The bounded coordinator cannot let an optional observer terminate
            // scheduling or shutdown. The original failure remains classified.
            Trace.TraceWarning(
                "Monitoring failure observer raised {0}.",
                observerException.GetType().Name);
        }
    }

    private static void WriteTimestamp(ref long target, DateTimeOffset value) =>
        Interlocked.Exchange(ref target, value.UtcTicks);

    private static DateTimeOffset? ReadTimestamp(ref long target)
    {
        var ticks = Interlocked.Read(ref target);
        return ticks == 0
            ? null
            : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private sealed class QueueEntry(
        MonitoringWorkItem work,
        CycleTracker tracker)
    {
        public MonitoringWorkItem Work { get; } = work;
        public CycleTracker Tracker { get; } = tracker;
        public List<CycleTracker> Followers { get; } = [];
    }

    private sealed class CycleTracker(long cycleId)
    {
        private readonly TaskCompletionSource<MonitoringCycleResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _accepted;
        private int _completed;
        private int _failed;
        private int _dropped;
        private int _duplicate;
        private int _cancelled;
        private int _sealed;
        private int _outstanding;

        public Task<MonitoringCycleResult> Completion => _completion.Task;

        public void Accept()
        {
            Interlocked.Increment(ref _accepted);
            Interlocked.Increment(ref _outstanding);
        }

        public void MarkDropped() => Interlocked.Increment(ref _dropped);

        public void FollowDuplicate()
        {
            Interlocked.Increment(ref _duplicate);
            Interlocked.Increment(ref _outstanding);
        }

        public void FailCapture() => Interlocked.Increment(ref _failed);

        public void Cancel() => Volatile.Write(ref _cancelled, 1);

        public void Seal()
        {
            Volatile.Write(ref _sealed, 1);
            TrySettle();
        }

        public void Complete(bool dropped, bool failed, bool cancelled)
        {
            if (dropped)
            {
                Interlocked.Increment(ref _dropped);
            }
            else if (failed)
            {
                Interlocked.Increment(ref _failed);
            }
            else if (!cancelled)
            {
                Interlocked.Increment(ref _completed);
            }
            if (cancelled)
            {
                Volatile.Write(ref _cancelled, 1);
            }

            Interlocked.Decrement(ref _outstanding);
            TrySettle();
        }

        public void CompleteFollower(bool dropped, bool failed, bool cancelled)
        {
            if (dropped)
            {
                Interlocked.Increment(ref _dropped);
            }
            else if (failed)
            {
                Interlocked.Increment(ref _failed);
            }
            if (cancelled)
            {
                Volatile.Write(ref _cancelled, 1);
            }

            Interlocked.Decrement(ref _outstanding);
            TrySettle();
        }

        private void TrySettle()
        {
            if (Volatile.Read(ref _sealed) == 0
                || Volatile.Read(ref _outstanding) != 0)
            {
                return;
            }

            _completion.TrySetResult(new MonitoringCycleResult(
                cycleId,
                Volatile.Read(ref _accepted),
                Volatile.Read(ref _completed),
                Volatile.Read(ref _failed),
                Volatile.Read(ref _dropped),
                Volatile.Read(ref _duplicate),
                Volatile.Read(ref _cancelled) != 0));
        }
    }
}
