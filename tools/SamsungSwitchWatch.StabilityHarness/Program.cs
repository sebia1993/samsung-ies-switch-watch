using System.Diagnostics;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Monitoring;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.StabilityHarness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!HarnessOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            HarnessOptions.WriteUsage(Console.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            HarnessOptions.WriteUsage(Console.Out);
            return 0;
        }

        Console.WriteLine(
            $"Profile: {options.Profile} | Duration: {options.Duration} | Devices: {options.DeviceCount} | Seed: {options.Seed}");

        var runner = new StabilityRunner(options);
        var summary = await runner.RunAsync().ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(
            summary,
            new JsonSerializerOptions { WriteIndented = true }));

        return summary.Passed ? 0 : 1;
    }
}

internal sealed class StabilityRunner(HarnessOptions options)
{
    private const int QueueCapacity = MonitoringCoordinator.DefaultQueueCapacity;
    private const int WorkerCount = MonitoringCoordinator.DefaultWorkerCount;

    private readonly object _stateSync = new();
    private readonly object _randomSync = new();
    private readonly Dictionary<string, SyntheticDevice> _devices = [];
    private readonly Random _random = new(options.Seed);
    private readonly SyntheticAgentClient _client = new();
    private readonly DeviceOperationGateRegistry _operationGates = new();
    private long _nextRevision;
    private long _collections;
    private long _failures;
    private long _reconnects;
    private long _deviceDeletes;
    private long _deviceEdits;
    private long _settingsChanges;
    private long _monitorRestarts;
    private long _staleDiscards;
    private long _unhandledExceptions;
    private int _activeWorkers;
    private int _maximumActiveWorkers;
    private int _peakQueueDepth;
    private int _peakOperationGates;
    private bool _agentConnected = true;

    public async Task<StabilitySummary> RunAsync()
    {
        SeedDevices();
        var process = Process.GetCurrentProcess();
        var startedUtc = DateTimeOffset.UtcNow;
        var startedWorkingSet = process.WorkingSet64;
        var startedPrivateMemory = process.PrivateMemorySize64;
        var startedHeap = GC.GetTotalMemory(forceFullCollection: true);
        var startedGc = GcCounts();
        var peakWorkingSet = startedWorkingSet;
        var peakPrivateMemory = startedPrivateMemory;
        var peakThreads = process.Threads.Count;
        var peakHandles = SafeHandleCount(process);
        var monotonicHeapSamples = 0;
        var previousHeap = startedHeap;
        var maximumMonotonicHeapSamples = 0;

        void OnUnhandled(object? _, UnhandledExceptionEventArgs __) =>
            Interlocked.Increment(ref _unhandledExceptions);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

        await using var coordinator = new MonitoringCoordinator(
            CaptureWorkAsync,
            ProcessWorkAsync,
            interval: TimeSpan.FromMilliseconds(250),
            workerCount: WorkerCount,
            capacity: QueueCapacity,
            unhandledFailure: _ => Interlocked.Increment(ref _failures));
        using var durationCancellation = new CancellationTokenSource(options.Duration);
        try
        {
            await coordinator.StartAsync(durationCancellation.Token).ConfigureAwait(false);
            var mutationTask = MutateWorkloadAsync(
                coordinator,
                durationCancellation.Token);

            while (!durationCancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(200),
                            durationCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (durationCancellation.IsCancellationRequested)
                {
                    break;
                }

                var loopStatus = coordinator.GetStatus();
                UpdateMaximum(ref _peakQueueDepth, loopStatus.QueueDepth);
                UpdateMaximum(ref _peakOperationGates, _operationGates.Count);
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                peakPrivateMemory = Math.Max(peakPrivateMemory, process.PrivateMemorySize64);
                peakThreads = Math.Max(peakThreads, process.Threads.Count);
                peakHandles = Math.Max(peakHandles, SafeHandleCount(process));

                var heap = GC.GetTotalMemory(forceFullCollection: false);
                if (heap > previousHeap)
                {
                    monotonicHeapSamples++;
                    maximumMonotonicHeapSamples = Math.Max(
                        maximumMonotonicHeapSamples,
                        monotonicHeapSamples);
                }
                else
                {
                    monotonicHeapSamples = 0;
                }
                previousHeap = heap;
            }

            await coordinator.StopAsync().ConfigureAwait(false);
            try
            {
                await mutationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (durationCancellation.IsCancellationRequested)
            {
            }

            process.Refresh();
            var completedUtc = DateTimeOffset.UtcNow;
            var completedHeap = GC.GetTotalMemory(forceFullCollection: true);
            var completedGc = GcCounts();
            var finalStatus = coordinator.GetStatus();
            var managedHeapGrowth = completedHeap - startedHeap;
            var passed = finalStatus.QueueDepth == 0
                         && finalStatus.ActiveWorkers == 0
                         && _maximumActiveWorkers <= WorkerCount
                         && _peakQueueDepth <= QueueCapacity
                         && _operationGates.Count == 0
                         && Interlocked.Read(ref _unhandledExceptions) == 0
                         && (maximumMonotonicHeapSamples < 30
                             || managedHeapGrowth < 32L * 1024 * 1024);

            return new StabilitySummary(
                options.Profile,
                options.Seed,
                options.DeviceCount,
                completedUtc - startedUtc,
                Interlocked.Read(ref _collections),
                Interlocked.Read(ref _failures),
                Interlocked.Read(ref _reconnects),
                finalStatus.DroppedWorkItems,
                _peakQueueDepth,
                _maximumActiveWorkers,
                _peakOperationGates,
                _operationGates.Count,
                completedGc.Gen0 - startedGc.Gen0,
                completedGc.Gen1 - startedGc.Gen1,
                completedGc.Gen2 - startedGc.Gen2,
                startedWorkingSet,
                peakWorkingSet,
                process.WorkingSet64,
                startedPrivateMemory,
                peakPrivateMemory,
                process.PrivateMemorySize64,
                peakThreads,
                peakHandles,
                startedHeap,
                completedHeap,
                managedHeapGrowth,
                maximumMonotonicHeapSamples,
                Interlocked.Read(ref _deviceDeletes),
                Interlocked.Read(ref _deviceEdits),
                Interlocked.Read(ref _settingsChanges),
                Interlocked.Read(ref _monitorRestarts),
                Interlocked.Read(ref _staleDiscards),
                Interlocked.Read(ref _unhandledExceptions),
                passed);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        }
    }

    private Task<IReadOnlyList<MonitoringWorkItem>> CaptureWorkAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateSync)
        {
            return Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(
                _devices.Values.Select(device => new MonitoringWorkItem(
                        new ManagedDeviceProfile
                        {
                            Id = device.Id,
                            DisplayName = device.Id,
                            Model = "IES4224GP",
                            Host = device.Host,
                            Port = 23,
                            MonitoringEnabled = true,
                            ConnectionVerified = true
                        },
                        device.Revision,
                        1,
                        _client))
                    .ToArray());
        }
    }

    private async Task ProcessWorkAsync(
        MonitoringWorkItem work,
        CancellationToken cancellationToken)
    {
        using var operationLease = await _operationGates.AcquireAsync(
            work.Profile.Host,
            cancellationToken).ConfigureAwait(false);
        var active = Interlocked.Increment(ref _activeWorkers);
        UpdateMaximum(ref _maximumActiveWorkers, active);
        try
        {
            var roll = Next(0, 1_000);
            await Task.Delay(
                    roll < 80
                        ? TimeSpan.FromMilliseconds(60)
                        : TimeSpan.FromMilliseconds(Next(1, 8)),
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_stateSync)
            {
                if (!_devices.TryGetValue(work.DeviceId, out var current)
                    || current.Revision != work.Revision)
                {
                    Interlocked.Increment(ref _staleDiscards);
                    return;
                }
                if (!_agentConnected)
                {
                    Interlocked.Increment(ref _failures);
                    return;
                }
            }

            if (roll is >= 80 and < 100)
            {
                Interlocked.Increment(ref _failures);
                return;
            }

            Interlocked.Increment(ref _collections);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWorkers);
        }
    }

    private async Task MutateWorkloadAsync(
        MonitoringCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var iteration = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            iteration++;
            var id = $"switch-{Next(0, options.DeviceCount):D3}";
            switch (iteration % 6)
            {
                case 0:
                    lock (_stateSync)
                    {
                        _devices.Remove(id);
                        _devices[id] = NewDevice(id);
                    }
                    Interlocked.Increment(ref _deviceDeletes);
                    break;
                case 1:
                    lock (_stateSync)
                    {
                        if (_devices.TryGetValue(id, out var existing))
                        {
                            var revision = Interlocked.Increment(
                                ref _nextRevision);
                            _devices[id] = existing with
                            {
                                Host = HostFor(id, revision),
                                Revision = revision
                            };
                        }
                    }
                    Interlocked.Increment(ref _deviceEdits);
                    break;
                case 2:
                    Interlocked.Increment(ref _settingsChanges);
                    break;
                case 3:
                    lock (_stateSync) _agentConnected = false;
                    break;
                case 4:
                    lock (_stateSync) _agentConnected = true;
                    Interlocked.Increment(ref _reconnects);
                    break;
                case 5:
                    await coordinator.StopAsync().ConfigureAwait(false);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref _monitorRestarts);
                    }
                    break;
            }
        }
    }

    private void SeedDevices()
    {
        lock (_stateSync)
        {
            for (var index = 0; index < options.DeviceCount; index++)
            {
                var id = $"switch-{index:D3}";
                _devices[id] = NewDevice(id);
            }
        }
    }

    private SyntheticDevice NewDevice(string id)
    {
        var revision = Interlocked.Increment(ref _nextRevision);
        return new SyntheticDevice(
            id,
            HostFor(id, revision),
            revision);
    }

    private static string HostFor(string id, long revision)
    {
        var index = int.Parse(id.AsSpan("switch-".Length));
        return $"10.{index / 250 % 250 + 1}.{revision % 250}.{index % 250 + 1}";
    }

    private int Next(int minimum, int maximum)
    {
        lock (_randomSync) return _random.Next(minimum, maximum);
    }

    private static (int Gen0, int Gen1, int Gen2) GcCounts() =>
        (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    private static int SafeHandleCount(Process process)
    {
        try
        {
            return OperatingSystem.IsWindows() ? process.HandleCount : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

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
}

internal sealed record SyntheticDevice(string Id, string Host, long Revision);

internal sealed record StabilitySummary(
    string Profile,
    int Seed,
    int DeviceCount,
    TimeSpan Duration,
    long Collections,
    long Failures,
    long Reconnects,
    long DroppedWork,
    int PeakQueueDepth,
    int MaxActiveWorkers,
    int PeakOperationGates,
    int EndingOperationGates,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long StartingWorkingSet,
    long PeakWorkingSet,
    long EndingWorkingSet,
    long StartingPrivateMemory,
    long PeakPrivateMemory,
    long EndingPrivateMemory,
    int PeakThreadCount,
    int PeakHandleCount,
    long StartingManagedHeap,
    long EndingManagedHeap,
    long ManagedHeapGrowth,
    int MaximumMonotonicHeapSamples,
    long DeviceDeletes,
    long DeviceEdits,
    long SettingsChanges,
    long MonitorRestarts,
    long StaleResultsDiscarded,
    long UnhandledExceptions,
    bool Passed);

internal sealed record HarnessOptions(
    string Profile,
    TimeSpan Duration,
    int DeviceCount,
    int Seed,
    bool ShowHelp)
{
    private static readonly IReadOnlyDictionary<string, TimeSpan> Profiles =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            ["smoke"] = TimeSpan.FromMinutes(5),
            ["quick"] = TimeSpan.FromMinutes(15),
            ["standard"] = TimeSpan.FromHours(1),
            ["extended"] = TimeSpan.FromHours(8),
            ["manual"] = TimeSpan.FromHours(24)
        };
    private static readonly int[] SupportedDeviceCounts = [10, 50, 100, 250];

    public static bool TryParse(
        string[] args,
        out HarnessOptions options,
        out string error)
    {
        var profile = "quick";
        TimeSpan? duration = null;
        var devices = 50;
        var seed = 372811;
        var help = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                help = true;
                continue;
            }
            if (index + 1 >= args.Length)
            {
                options = new(profile, TimeSpan.Zero, devices, seed, help);
                error = $"값이 필요한 인수입니다: {argument}";
                return false;
            }

            var value = args[++index];
            switch (argument)
            {
                case "--profile":
                    profile = value;
                    break;
                case "--duration":
                    if (!TimeSpan.TryParse(value, out var parsedDuration)
                        || parsedDuration <= TimeSpan.Zero)
                    {
                        options = new(profile, TimeSpan.Zero, devices, seed, help);
                        error = "--duration은 양수 TimeSpan이어야 합니다.";
                        return false;
                    }
                    duration = parsedDuration;
                    break;
                case "--devices":
                    if (!int.TryParse(value, out devices))
                    {
                        options = new(profile, TimeSpan.Zero, devices, seed, help);
                        error = "--devices 값이 숫자가 아닙니다.";
                        return false;
                    }
                    break;
                case "--seed":
                    if (!int.TryParse(value, out seed))
                    {
                        options = new(profile, TimeSpan.Zero, devices, seed, help);
                        error = "--seed 값이 숫자가 아닙니다.";
                        return false;
                    }
                    break;
                default:
                    options = new(profile, TimeSpan.Zero, devices, seed, help);
                    error = $"알 수 없는 인수입니다: {argument}";
                    return false;
            }
        }

        if (!Profiles.TryGetValue(profile, out var profileDuration))
        {
            options = new(profile, TimeSpan.Zero, devices, seed, help);
            error = "profile은 smoke, quick, standard, extended, manual 중 하나여야 합니다.";
            return false;
        }
        if (!SupportedDeviceCounts.Contains(devices))
        {
            options = new(profile, TimeSpan.Zero, devices, seed, help);
            error = "devices는 10, 50, 100, 250 중 하나여야 합니다.";
            return false;
        }

        options = new(profile.ToLowerInvariant(), duration ?? profileDuration, devices, seed, help);
        error = string.Empty;
        return true;
    }

    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("SamsungSwitchWatch.StabilityHarness");
        writer.WriteLine("  --profile smoke|quick|standard|extended|manual");
        writer.WriteLine("  --devices 10|50|100|250");
        writer.WriteLine("  --seed <integer>");
        writer.WriteLine("  --duration hh:mm:ss   # deterministic short verification override");
        writer.WriteLine("Profiles: smoke=5m, quick=15m, standard=1h, extended=8h, manual=24h");
    }
}

internal sealed class SyntheticAgentClient : IAgentClient
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
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<EventChangePageDto> GetEventChangesAsync(
        long cursor,
        int limit,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<CommandResultDto> ExecuteRegisteredCheckAsync(
        string deviceId,
        string commandId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
        string deviceId,
        string command,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> AcknowledgeAsync(
        string eventId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
