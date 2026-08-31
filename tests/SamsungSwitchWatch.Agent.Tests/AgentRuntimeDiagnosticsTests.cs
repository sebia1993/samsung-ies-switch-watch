using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using SamsungSwitchWatch.Agent.Diagnostics;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class AgentRuntimeDiagnosticsTests
{
    [Fact]
    public void Metrics_ExposeBoundedTagFreeRuntimeSignals()
    {
        var observed = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == AgentRuntimeDiagnostics.MeterName)
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
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

        AgentRuntimeDiagnostics.RecordRequestStarted();
        using (AgentRuntimeDiagnostics.BeginActiveSession())
        {
            AgentRuntimeDiagnostics.RecordReconnects(1);
        }
        AgentRuntimeDiagnostics.RecordRequestFailed("COMMAND_TIMEOUT");
        AgentRuntimeDiagnostics.RecordDuration(TimeSpan.FromMilliseconds(5));
        AgentRuntimeDiagnostics.RecordRateLimited();
        AgentRuntimeDiagnostics.RecordBusy();

        Assert.Contains("ssw.telnet.active_sessions", observed);
        Assert.Contains("ssw.telnet.request.total", observed);
        Assert.Contains("ssw.telnet.request.failed", observed);
        Assert.Contains("ssw.telnet.reconnect.total", observed);
        Assert.Contains("ssw.telnet.duration", observed);
        Assert.Contains("ssw.telnet.timeout.total", observed);
        Assert.Contains("ssw.agent.rate_limited.total", observed);
        Assert.Contains("ssw.agent.busy.total", observed);
    }
}
