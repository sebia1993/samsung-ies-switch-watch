using SamsungSwitchWatch.Viewer.Events;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class EventFeedCoordinatorTests
{
    [Fact]
    public void Buffer_OrdersChangesCoalescesSequenceAndPreservesLiveAlert()
    {
        var feed = new EventFeedCoordinator(4);
        feed.Buffer([Change(2, "old"), Change(1, "first")], 0, live: false, allowLiveAlerts: false);
        feed.Buffer([Change(2, "new")], 0, live: true, allowLiveAlerts: true);

        Assert.True(feed.TryTakeNext(0, out var first, out var firstLive));
        Assert.Equal(1, first.ChangeSequence);
        Assert.False(firstLive);
        Assert.True(feed.TryTakeNext(1, out var second, out var secondLive));
        Assert.Equal("new", second.Event.Title);
        Assert.True(secondLive);
        Assert.False(feed.TryTakeNext(2, out _, out _));
    }

    [Fact]
    public void Buffer_OverflowFailsClosedUntilConsumedThenAcceptsNewChanges()
    {
        var feed = new EventFeedCoordinator(2);

        feed.Buffer([Change(1, "one"), Change(2, "two"), Change(3, "three")], 0, true, true);

        Assert.Equal(0, feed.BufferedCount);
        Assert.True(feed.ConsumeOverflow());
        Assert.False(feed.ConsumeOverflow());
        feed.Buffer([Change(1, "recovered")], 0, false, false);
        Assert.True(feed.TryTakeNext(0, out var change, out var live));
        Assert.Equal("recovered", change.Event.Title);
        Assert.False(live);
    }

    [Fact]
    public void PumpScheduling_DetectsLostWakeupAndShutdownSuppressesRestart()
    {
        var feed = new EventFeedCoordinator(2);
        feed.Signal();
        Assert.True(feed.TrySchedulePump());
        Assert.False(feed.TrySchedulePump());
        var observed = feed.ObserveSignal();
        feed.Signal();

        Assert.True(feed.CompletePump(observed, shuttingDown: false));
        Assert.False(feed.IsPumpScheduled);
        Assert.True(feed.TrySchedulePump());
        Assert.False(feed.CompletePump(feed.ObserveSignal(), shuttingDown: true));
        Assert.Equal(2, feed.PumpStartCount);
    }

    [Fact]
    public void Reset_ClearsBufferedAndOverflowState()
    {
        var feed = new EventFeedCoordinator(1);
        feed.Buffer([Change(1, "one"), Change(2, "two")], 0, true, true);

        feed.Reset();

        Assert.Equal(0, feed.BufferedCount);
        Assert.False(feed.ConsumeOverflow());
    }

    private static AgentEventChangeDto Change(long sequence, string title) =>
        new(
            sequence,
            "upsert",
            new SwitchEventDto(
                sequence,
                $"event-{sequence}",
                "switch-a",
                "Switch A",
                DateTimeOffset.UnixEpoch.AddSeconds(sequence),
                DeviceHealth.Warning,
                "test",
                title,
                "detail"));
}
