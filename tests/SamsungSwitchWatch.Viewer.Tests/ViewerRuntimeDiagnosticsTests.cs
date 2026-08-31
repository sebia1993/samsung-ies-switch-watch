using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using SamsungSwitchWatch.Viewer.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerRuntimeDiagnosticsTests
{
    [Fact]
    public void Metrics_ExposeRequiredSignalsWithoutSensitiveTags()
    {
        var observed = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == ViewerRuntimeDiagnostics.MeterName)
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            Assert.True(tags.IsEmpty);
            observed.Add(instrument.Name);
        });
        listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) =>
        {
            Assert.True(tags.IsEmpty);
            observed.Add(instrument.Name);
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            Assert.True(tags.IsEmpty);
            observed.Add(instrument.Name);
        });
        listener.Start();

        ViewerRuntimeDiagnostics.SetMonitorQueueDepth(2);
        ViewerRuntimeDiagnostics.SetMonitorActiveWorkers(1);
        ViewerRuntimeDiagnostics.RecordMonitorWorkDropped();
        ViewerRuntimeDiagnostics.RecordMonitorCollection(
            TimeSpan.FromMilliseconds(5),
            failed: true);
        ViewerRuntimeDiagnostics.RecordStaleResultDiscarded();
        ViewerRuntimeDiagnostics.SetEventBufferCount(3);
        ViewerRuntimeDiagnostics.RecordEventBufferOverflow();
        ViewerRuntimeDiagnostics.RecordAgentConnectionFailure();
        listener.RecordObservableInstruments();

        Assert.Contains("ssw.monitor.queue.depth", observed);
        Assert.Contains("ssw.monitor.active_workers", observed);
        Assert.Contains("ssw.monitor.work.dropped", observed);
        Assert.Contains("ssw.monitor.collection.total", observed);
        Assert.Contains("ssw.monitor.collection.failed", observed);
        Assert.Contains("ssw.monitor.collection.duration", observed);
        Assert.Contains("ssw.monitor.stale_result.discarded", observed);
        Assert.Contains("ssw.events.buffer.count", observed);
        Assert.Contains("ssw.events.buffer.overflow", observed);
        Assert.Contains("ssw.agent.connection.failures", observed);
    }
}
