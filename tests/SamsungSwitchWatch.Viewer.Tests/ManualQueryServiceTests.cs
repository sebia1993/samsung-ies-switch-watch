using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ManualQueryServiceTests
{
    [Fact]
    public async Task QueryLease_IsExclusiveAndDisposeCancelsActiveQueryIdempotently()
    {
        var service = new ManualQueryService();
        using var lifetime = new CancellationTokenSource();
        var lease = Assert.IsType<ManualQueryExecutionLease>(
            service.TryBeginQuery(lifetime.Token));

        Assert.Null(service.TryBeginQuery(lifetime.Token));
        Assert.False(lease.Cancellation.IsCancellationRequested);

        var firstDispose = service.DisposeAsync().AsTask();
        Assert.True(lease.Cancellation.IsCancellationRequested);
        Assert.False(firstDispose.IsCompleted);

        service.ReleaseQuery(lease);
        await firstDispose;
        await service.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() =>
            service.TryBeginQuery(CancellationToken.None));
    }

    [Fact]
    public void ManualOperation_IsExclusiveAndCanBeReleased()
    {
        var service = new ManualQueryService();

        Assert.True(service.TryBeginManualOperation());
        Assert.True(service.IsManualOperationActive);
        Assert.False(service.TryBeginManualOperation());
        Assert.True(service.EndManualOperation());
        Assert.False(service.IsManualOperationActive);
        Assert.False(service.EndManualOperation());
    }

    [Fact]
    public void ContextGeneration_RejectsStaleCompletion()
    {
        var service = new ManualQueryService();
        var captured = service.ContextGeneration;

        service.AdvanceContext();

        Assert.False(service.IsCurrent(captured));
        Assert.True(service.IsCurrent(service.ContextGeneration));
    }

    [Fact]
    public void History_IsBoundedDeduplicatedAndRestoresDraft()
    {
        var service = new ManualQueryService();
        for (var index = 0; index < ManualQueryService.MaximumHistoryCount + 5; index++)
        {
            service.AddHistory($"show item {index}");
        }
        service.AddHistory("show item 24");
        service.UpdateDraft("show draft");

        Assert.Equal(ManualQueryService.MaximumHistoryCount, service.HistoryCount);
        Assert.True(service.TryMoveHistory("show draft", -1, false, out var latest));
        Assert.Equal("show item 24", latest);
        Assert.True(service.TryMoveHistory(latest, 1, false, out var draft));
        Assert.Equal("show draft", draft);
        Assert.False(service.TryMoveHistory(draft, -1, true, out _));
    }
}
