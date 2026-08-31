using System.Diagnostics.Metrics;

namespace SamsungSwitchWatch.Viewer.Diagnostics;

internal static class ViewerRuntimeDiagnostics
{
    internal const string MeterName = "SamsungSwitchWatch.Viewer";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> MonitorWorkDropped =
        Meter.CreateCounter<long>("ssw.monitor.work.dropped");
    private static readonly Counter<long> MonitorCollectionTotal =
        Meter.CreateCounter<long>("ssw.monitor.collection.total");
    private static readonly Counter<long> MonitorCollectionFailed =
        Meter.CreateCounter<long>("ssw.monitor.collection.failed");
    private static readonly Histogram<double> MonitorCollectionDuration =
        Meter.CreateHistogram<double>("ssw.monitor.collection.duration", "ms");
    private static readonly Counter<long> MonitorStaleResultDiscarded =
        Meter.CreateCounter<long>("ssw.monitor.stale_result.discarded");
    private static readonly Counter<long> EventBufferOverflow =
        Meter.CreateCounter<long>("ssw.events.buffer.overflow");
    private static readonly Counter<long> AgentConnectionFailures =
        Meter.CreateCounter<long>("ssw.agent.connection.failures");

    private static int _monitorQueueDepth;
    private static int _monitorActiveWorkers;
    private static int _eventBufferCount;

    static ViewerRuntimeDiagnostics()
    {
        Meter.CreateObservableGauge(
            "ssw.monitor.queue.depth",
            () => Math.Max(0, Volatile.Read(ref _monitorQueueDepth)));
        Meter.CreateObservableGauge(
            "ssw.monitor.active_workers",
            () => Math.Max(0, Volatile.Read(ref _monitorActiveWorkers)));
        Meter.CreateObservableGauge(
            "ssw.events.buffer.count",
            () => Math.Max(0, Volatile.Read(ref _eventBufferCount)));
    }

    public static void SetMonitorQueueDepth(int value) =>
        Volatile.Write(ref _monitorQueueDepth, Math.Max(0, value));

    public static void SetMonitorActiveWorkers(int value) =>
        Volatile.Write(ref _monitorActiveWorkers, Math.Max(0, value));

    public static void RecordMonitorWorkDropped(long count = 1)
    {
        if (count > 0) MonitorWorkDropped.Add(count);
    }

    public static void RecordMonitorCollection(TimeSpan duration, bool failed)
    {
        MonitorCollectionTotal.Add(1);
        if (failed) MonitorCollectionFailed.Add(1);
        MonitorCollectionDuration.Record(Math.Max(0, duration.TotalMilliseconds));
    }

    public static void RecordStaleResultDiscarded() =>
        MonitorStaleResultDiscarded.Add(1);

    public static void SetEventBufferCount(int value) =>
        Volatile.Write(ref _eventBufferCount, Math.Max(0, value));

    public static void RecordEventBufferOverflow() => EventBufferOverflow.Add(1);

    public static void RecordAgentConnectionFailure() => AgentConnectionFailures.Add(1);
}
