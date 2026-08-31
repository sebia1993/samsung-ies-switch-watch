using SamsungSwitchWatch.Viewer.Monitoring;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class CircuitBreakerTests
{
    [Fact]
    public void ThreeAvailabilityFailures_OpenThenSingleHalfOpenProbeClosesOnSuccess()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var breaker = new DeviceCircuitBreaker(clock);

        Assert.True(breaker.TryEnter("switch-a"));
        breaker.RecordAvailabilityFailure("switch-a");
        Assert.True(breaker.TryEnter("switch-a"));
        breaker.RecordAvailabilityFailure("switch-a");
        Assert.True(breaker.TryEnter("switch-a"));
        breaker.RecordAvailabilityFailure("switch-a");

        Assert.Equal(DeviceCircuitState.Open, breaker.GetSnapshot("switch-a").State);
        Assert.False(breaker.TryEnter("switch-a"));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(DeviceCircuitState.HalfOpen, breaker.GetSnapshot("switch-a").State);
        Assert.True(breaker.TryEnter("switch-a"));
        Assert.False(breaker.TryEnter("switch-a"));

        breaker.RecordSuccess("switch-a");
        var recovered = breaker.GetSnapshot("switch-a");
        Assert.Equal(DeviceCircuitState.Closed, recovered.State);
        Assert.Equal(0, recovered.ConsecutiveAvailabilityFailures);
        Assert.True(breaker.TryEnter("switch-a"));
    }

    [Fact]
    public void HalfOpenAvailabilityFailure_ReopensForFullDelay()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var breaker = new DeviceCircuitBreaker(
            clock,
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10));

        Assert.True(breaker.TryEnter("switch-a"));
        breaker.RecordAvailabilityFailure("switch-a");
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(breaker.TryEnter("switch-a"));
        breaker.RecordAvailabilityFailure("switch-a");

        Assert.Equal(DeviceCircuitState.Open, breaker.GetSnapshot("switch-a").State);
        Assert.False(breaker.TryEnter("switch-a"));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(breaker.TryEnter("switch-a"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(breaker.TryEnter("switch-a"));
    }

    [Fact]
    public void CredentialOrConfigurationFailure_DoesNotOpenAvailabilityCircuit()
    {
        var breaker = new DeviceCircuitBreaker(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(30));

        Assert.True(breaker.TryEnter("switch-a"));
        breaker.RecordNonAvailabilityFailure("switch-a");

        Assert.Equal(DeviceCircuitState.Closed, breaker.GetSnapshot("switch-a").State);
        Assert.True(breaker.TryEnter("switch-a"));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
    }
}
