using SamsungSwitchWatch.Viewer.Monitoring;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class DeviceOperationGateRegistryTests
{
    [Fact]
    public async Task Acquire_SerializesSameDeviceAndRemovesUnusedGate()
    {
        var registry = new DeviceOperationGateRegistry();
        var first = await registry.AcquireAsync(
            "10.0.0.10",
            CancellationToken.None);
        var secondTask = registry.AcquireAsync(
            "10.0.0.10",
            CancellationToken.None).AsTask();

        await Task.Yield();
        Assert.False(secondTask.IsCompleted);
        Assert.Equal(1, registry.Count);

        first.Dispose();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, registry.Count);

        second.Dispose();
        second.Dispose();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task TryAcquire_WhenBusyReturnsNullWithoutLeakingReference()
    {
        var registry = new DeviceOperationGateRegistry();
        var first = await registry.AcquireAsync(
            "10.0.0.10",
            CancellationToken.None);

        var blocked = await registry.TryAcquireAsync(
            "10.0.0.10",
            CancellationToken.None);

        Assert.Null(blocked);
        Assert.Equal(1, registry.Count);

        first.Dispose();
        Assert.Equal(0, registry.Count);
    }
}
