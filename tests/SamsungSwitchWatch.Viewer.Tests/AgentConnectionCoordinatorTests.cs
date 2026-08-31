using SamsungSwitchWatch.Viewer.Connections;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class AgentConnectionCoordinatorTests
{
    [Fact]
    public void Replace_IncrementsGenerationAndInvalidatesCapturedClient()
    {
        var first = new UnavailableAgentClient();
        var second = new UnavailableAgentClient();
        var coordinator = new AgentConnectionCoordinator(first);
        var captured = coordinator.Capture();
        var resetCount = 0;

        var previous = coordinator.Replace(second, () => resetCount++);

        Assert.Same(first, previous);
        Assert.Same(second, coordinator.CurrentClient);
        Assert.Equal(1, coordinator.Generation);
        Assert.False(coordinator.IsCurrent(captured));
        Assert.True(coordinator.IsCurrent(coordinator.Capture()));
        Assert.Equal(1, resetCount);
        Assert.False(coordinator.TryRunIfCurrent(first, () =>
            throw new InvalidOperationException("stale callback ran")));
        Assert.True(coordinator.TryRunIfCurrent(second, () => { }));

        coordinator.Replace(second, () => resetCount++);
        Assert.Equal(1, coordinator.Generation);
        Assert.Equal(1, resetCount);
    }

    [Theory]
    [InlineData(AgentConnectionState.NeedsConnection, AgentConnectionState.Connected, false, false, AgentConnectionState.NeedsConnection)]
    [InlineData(AgentConnectionState.Demo, AgentConnectionState.Connected, false, false, AgentConnectionState.Demo)]
    [InlineData(AgentConnectionState.Offline, AgentConnectionState.Connected, true, true, AgentConnectionState.Stale)]
    [InlineData(AgentConnectionState.Offline, AgentConnectionState.Connected, true, false, AgentConnectionState.Offline)]
    [InlineData(AgentConnectionState.Connected, AgentConnectionState.Reconnecting, true, true, AgentConnectionState.Reconnecting)]
    [InlineData(AgentConnectionState.Connected, AgentConnectionState.Connecting, true, true, AgentConnectionState.Reconnecting)]
    [InlineData(AgentConnectionState.Connected, AgentConnectionState.Connected, true, true, AgentConnectionState.Connected)]
    public void CombinedState_PreservesChannelPrecedence(
        AgentConnectionState http,
        AgentConnectionState realtime,
        bool initialized,
        bool hasSnapshot,
        AgentConnectionState expected)
    {
        var coordinator = new AgentConnectionCoordinator(new UnavailableAgentClient());
        coordinator.SetHttpState(http);
        coordinator.SetRealtimeState(realtime);

        Assert.Equal(expected, coordinator.GetCombinedState(initialized, hasSnapshot));
    }

    [Fact]
    public async Task ConcurrentCaptureAndReplacement_NeverProducesCurrentStaleGeneration()
    {
        var coordinator = new AgentConnectionCoordinator(new UnavailableAgentClient());
        var captures = new System.Collections.Concurrent.ConcurrentBag<AgentClientGeneration>();

        var readers = Enumerable.Range(0, 4).Select(async _ =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                captures.Add(coordinator.Capture());
                await Task.Yield();
            }
        });
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                coordinator.Replace(new UnavailableAgentClient());
            }
        });

        await Task.WhenAll(readers.Append(writer));

        captures.Add(coordinator.Capture());
        Assert.All(captures, captured =>
            Assert.InRange(captured.Generation, 0, coordinator.Generation));
        Assert.All(captures.GroupBy(captured => captured.Generation), generation =>
            Assert.Single(generation.Select(item => item.Client)
                .Distinct(ReferenceEqualityComparer.Instance)));
        Assert.True(coordinator.IsCurrent(coordinator.Capture()));
    }
}
