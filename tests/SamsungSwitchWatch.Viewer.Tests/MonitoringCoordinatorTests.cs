using System.Collections.Concurrent;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Monitoring;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class MonitoringCoordinatorTests
{
    [Fact]
    public async Task PeriodicScheduling_UsesInjectedTimeProviderWithoutRealDelay()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var captureCount = 0;
        var coordinator = new MonitoringCoordinator(
            _ =>
            {
                Interlocked.Increment(ref captureCount);
                return Task.FromResult<IReadOnlyList<MonitoringWorkItem>>([]);
            },
            (_, _) => Task.CompletedTask,
            interval: TimeSpan.FromMinutes(1),
            timeProvider: timeProvider);

        await coordinator.StartAsync();
        await timeProvider.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref captureCount));

        timeProvider.Advance(TimeSpan.FromMinutes(1));

        await WaitUntilAsync(() => Volatile.Read(ref captureCount) == 2);
        Assert.Equal(timeProvider.GetUtcNow(), coordinator.GetStatus().LastCycleCompletedUtc);

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Start_UsesTwoWorkersAndSuppressesDuplicateDeviceWork()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var processed = new ConcurrentBag<string>();
        var client = new StubClient();
        var coordinator = new MonitoringCoordinator(
            _ => Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(
            [
                Work("a", client),
                Work("a", client),
                Work("b", client),
                Work("c", client)
            ]),
            async (work, cancellationToken) =>
            {
                processed.Add(work.DeviceId);
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                if (current == 2)
                {
                    entered.TrySetResult();
                }
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            interval: TimeSpan.FromHours(1),
            workerCount: 2,
            capacity: 8);

        await coordinator.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, coordinator.GetStatus().ActiveWorkers);
        Assert.InRange(maximumActive, 1, 2);

        release.TrySetResult();
        await WaitUntilAsync(() => coordinator.GetStatus().CompletedWorkItems == 3);
        await coordinator.StopAsync();

        Assert.Equal(3, processed.Count);
        Assert.Equal(1, processed.Count(item => item == "a"));
        Assert.False(coordinator.GetStatus().IsRunning);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task BoundedQueue_DropsOldestAndNeverGrowsPastCapacity()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubClient();
        var items = Enumerable.Range(0, 100)
            .Select(index => Work($"device-{index:D3}", client))
            .ToArray();
        var coordinator = new MonitoringCoordinator(
            _ => Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(items),
            (_, cancellationToken) => release.Task.WaitAsync(cancellationToken),
            interval: TimeSpan.FromHours(1),
            workerCount: 1,
            capacity: 4);

        await coordinator.StartAsync();
        await WaitUntilAsync(() => coordinator.GetStatus().DroppedWorkItems > 0);

        var saturated = coordinator.GetStatus();
        Assert.InRange(saturated.QueueDepth, 0, 4);
        Assert.True(saturated.DroppedWorkItems >= 90);

        release.TrySetResult();
        await coordinator.StopAsync();
        Assert.Equal(0, coordinator.GetStatus().QueueDepth);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task WorkerException_IsCountedAndDoesNotTerminateFollowingWork()
    {
        var client = new StubClient();
        var failures = new ConcurrentBag<Exception>();
        var processed = new ConcurrentBag<string>();
        var coordinator = new MonitoringCoordinator(
            _ => Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(
            [
                Work("fails", client),
                Work("succeeds", client)
            ]),
            (work, _) =>
            {
                if (work.DeviceId == "fails")
                {
                    throw new InvalidOperationException("synthetic");
                }
                processed.Add(work.DeviceId);
                return Task.CompletedTask;
            },
            unhandledFailure: failures.Add,
            interval: TimeSpan.FromHours(1),
            workerCount: 1,
            capacity: 4);

        await coordinator.StartAsync();
        await WaitUntilAsync(() => coordinator.GetStatus().FailedWorkItems == 1
                                   && coordinator.GetStatus().CompletedWorkItems == 1);
        await coordinator.StopAsync();

        Assert.Single(failures);
        Assert.Contains("succeeds", processed);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task RapidStartStopAndDoubleDispose_AreIdempotentAndLeaveNoWork()
    {
        var coordinator = new MonitoringCoordinator(
            _ => Task.FromResult<IReadOnlyList<MonitoringWorkItem>>([]),
            (_, _) => Task.CompletedTask,
            interval: TimeSpan.FromHours(1));

        for (var index = 0; index < 5; index++)
        {
            await coordinator.StartAsync();
            await coordinator.StartAsync();
            await coordinator.StopAsync();
            await coordinator.StopAsync();
        }

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        var status = coordinator.GetStatus();
        Assert.False(status.IsRunning);
        Assert.Equal(0, status.QueueDepth);
        Assert.Equal(0, status.ActiveWorkers);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await coordinator.RequestImmediateCollectionAsync());
    }

    [Fact]
    public async Task ConcurrentStopAndStart_SerializesCleanupBeforeNewRun()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStoppingWorker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = 0;
        var client = new StubClient();
        var coordinator = new MonitoringCoordinator(
            _ => Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(
                [Work("switch-a", client)]),
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref invocation) != 1)
                {
                    return;
                }
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    stopObserved.TrySetResult();
                    await releaseStoppingWorker.Task;
                    throw;
                }
            },
            interval: TimeSpan.FromHours(1),
            workerCount: 1,
            capacity: 4);

        await coordinator.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopTask = coordinator.StopAsync();
        await stopObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var restartTask = coordinator.StartAsync();
        await Task.Delay(25);
        Assert.False(restartTask.IsCompleted);

        releaseStoppingWorker.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        await restartTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.GetStatus().IsRunning);

        await coordinator.DisposeAsync();
        Assert.False(coordinator.GetStatus().IsRunning);
    }

    [Fact]
    public async Task Stop_CancelsRunningAndQueuedWorkWithoutLeakingCompletion()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubClient();
        var coordinator = new MonitoringCoordinator(
            _ => Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(
                Enumerable.Range(0, 20)
                    .Select(index => Work($"device-{index}", client))
                    .ToArray()),
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            interval: TimeSpan.FromHours(1),
            workerCount: 2,
            capacity: 20);

        await coordinator.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var status = coordinator.GetStatus();
        Assert.False(status.IsRunning);
        Assert.Equal(0, status.QueueDepth);
        Assert.Equal(0, status.ActiveWorkers);
        await coordinator.DisposeAsync();
    }

    private static MonitoringWorkItem Work(string id, IAgentClient client) =>
        new(
            new ManagedDeviceProfile
            {
                Id = id,
                DisplayName = id,
                Host = $"10.0.0.{Math.Abs(id.GetHashCode()) % 200 + 1}",
                MonitoringEnabled = true,
                ConnectionVerified = true
            },
            1,
            1,
            client);

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (observed >= value
                || Interlocked.CompareExchange(ref target, value, observed) == observed)
            {
                return;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The monitoring condition was not reached.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class StubClient : IAgentClient
    {
        public event EventHandler<AgentEventChangeDto>? EventChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<AgentConnectionState>? ConnectionStateChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EventChangePageDto> GetEventChangesAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(
            string deviceId,
            string commandId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
            string deviceId,
            string command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AcknowledgeAsync(
            string eventId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = initialUtc;

        public Task TimerCreated => _timerCreated.Task;

        private readonly TaskCompletionSource _timerCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return _utcNow;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(callback, state, dueTime, period);
            lock (_sync) _timers.Add(timer);
            _timerCreated.TrySetResult();
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(amount));
            }

            ManualTimer[] timers;
            lock (_sync)
            {
                _utcNow += amount;
                timers = [.. _timers];
            }
            foreach (var timer in timers)
            {
                timer.Fire(amount);
            }
        }

        private sealed class ManualTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private readonly object _sync = new();
            private TimeSpan _remaining = dueTime;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_sync)
                {
                    if (_disposed) return false;
                    _remaining = dueTime;
                    _period = period;
                    return true;
                }
            }

            public void Fire(TimeSpan elapsed)
            {
                var shouldFire = false;
                lock (_sync)
                {
                    if (_disposed || _remaining == Timeout.InfiniteTimeSpan)
                    {
                        return;
                    }
                    _remaining -= elapsed;
                    if (_remaining <= TimeSpan.Zero)
                    {
                        shouldFire = true;
                        _remaining = _period;
                    }
                }
                if (shouldFire) callback(state);
            }

            public void Dispose()
            {
                lock (_sync) _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
