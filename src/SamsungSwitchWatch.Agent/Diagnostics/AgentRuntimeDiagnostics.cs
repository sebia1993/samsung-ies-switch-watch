using System.Diagnostics.Metrics;

namespace SamsungSwitchWatch.Agent.Diagnostics;

internal static class AgentRuntimeDiagnostics
{
    internal const string MeterName = "SamsungSwitchWatch.Agent";

    private static readonly Meter Meter = new(MeterName);
    private static readonly UpDownCounter<long> ActiveSessions =
        Meter.CreateUpDownCounter<long>("ssw.telnet.active_sessions");
    private static readonly Counter<long> RequestTotal =
        Meter.CreateCounter<long>("ssw.telnet.request.total");
    private static readonly Counter<long> RequestFailed =
        Meter.CreateCounter<long>("ssw.telnet.request.failed");
    private static readonly Counter<long> ReconnectTotal =
        Meter.CreateCounter<long>("ssw.telnet.reconnect.total");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("ssw.telnet.duration", "ms");
    private static readonly Counter<long> TimeoutTotal =
        Meter.CreateCounter<long>("ssw.telnet.timeout.total");
    private static readonly Counter<long> RateLimitedTotal =
        Meter.CreateCounter<long>("ssw.agent.rate_limited.total");
    private static readonly Counter<long> BusyTotal =
        Meter.CreateCounter<long>("ssw.agent.busy.total");

    public static void RecordRequestStarted() => RequestTotal.Add(1);

    public static IDisposable BeginActiveSession()
    {
        ActiveSessions.Add(1);
        return new ActiveSessionLease();
    }

    public static void RecordRequestFailed(string errorCode)
    {
        RequestFailed.Add(1);
        if (errorCode.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase))
        {
            TimeoutTotal.Add(1);
        }
    }

    public static void RecordReconnects(int count)
    {
        if (count > 0) ReconnectTotal.Add(count);
    }

    public static void RecordDuration(TimeSpan duration) =>
        Duration.Record(Math.Max(0, duration.TotalMilliseconds));

    public static void RecordRateLimited() => RateLimitedTotal.Add(1);

    public static void RecordBusy() => BusyTotal.Add(1);

    private sealed class ActiveSessionLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ActiveSessions.Add(-1);
            }
        }
    }
}
