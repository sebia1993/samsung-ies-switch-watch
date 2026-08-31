using System.IO;
using SamsungSwitchWatch.Viewer.Devices;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Monitoring;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class DeviceLifecycleServiceTests
{
    [Fact]
    public void AdvanceRevision_InvalidatesQueuedAndRunningTokens()
    {
        var lifecycle = new DeviceLifecycleService();
        var initial = lifecycle.GetOrCreateRevision("switch-a");
        var token = new DeviceLifecycleToken("switch-a", initial);

        var changed = lifecycle.AdvanceRevision("switch-a");

        Assert.NotEqual(initial, changed);
        Assert.False(lifecycle.IsCurrent(token));
        Assert.True(lifecycle.IsCurrent("switch-a", changed));
        Assert.False(lifecycle.IsCurrent("deleted-switch", changed));
    }

    [Fact]
    public async Task ConcurrentRevisionChanges_AreUniqueAndLeaveOnlyNewestCurrent()
    {
        var lifecycle = new DeviceLifecycleService();
        var revisions = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => Task.Run(() => lifecycle.AdvanceRevision("switch-a"))));

        Assert.Equal(revisions.Length, revisions.Distinct().Count());
        var newest = revisions.Max();
        Assert.True(lifecycle.IsCurrent("switch-a", newest));
        Assert.All(revisions.Where(value => value != newest), value =>
            Assert.False(lifecycle.IsCurrent("switch-a", value)));
    }

    [Fact]
    public void CredentialBlock_IsOwnedAndClearedByLifecycleService()
    {
        var lifecycle = new DeviceLifecycleService();

        lifecycle.BlockForCredentialFailure("switch-a");
        Assert.True(lifecycle.IsCredentialBlocked("switch-a"));

        lifecycle.ClearCredentialBlock("switch-a");
        Assert.False(lifecycle.IsCredentialBlocked("switch-a"));
    }

    [Fact]
    public void SaveAndDelete_OwnPersistenceRevisionCredentialAndResetLifecycle()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "ssw-device-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var devices = new ManagedDeviceStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector());
            var monitoring = new ViewerMonitoringStore(
                Path.Combine(folder, "monitoring.json"));
            var lifecycle = new DeviceLifecycleService();
            var resetIds = new List<string>();

            var created = lifecycle.Save(
                devices,
                monitoring,
                monitoringStoreOperational: true,
                new ManagedDeviceDraft
                {
                    DisplayName = "switch-a",
                    Model = "IES4224GP",
                    Host = "192.0.2.10",
                    Username = "operator",
                    Password = "password"
                },
                resetIds.Add);
            var firstRevision = lifecycle.GetOrCreateRevision(created.Profile.Id);
            lifecycle.BlockForCredentialFailure(created.Profile.Id);

            devices.MarkConnectionTest(created.Profile.Id, true, "OK");
            var edit = devices.CreateEditDraft(created.Profile.Id);
            edit.DisplayName = "switch-a-renamed";
            var saved = lifecycle.Save(
                devices,
                monitoring,
                monitoringStoreOperational: true,
                edit,
                resetIds.Add);

            Assert.Equal("switch-a-renamed", saved.Profile.DisplayName);
            Assert.False(lifecycle.IsCredentialBlocked(created.Profile.Id));
            Assert.False(lifecycle.IsCurrent(created.Profile.Id, firstRevision));

            lifecycle.BlockForCredentialFailure(created.Profile.Id);
            var deleted = lifecycle.Delete(
                devices,
                monitoring,
                monitoringStoreOperational: true,
                created.Profile.Id,
                resetIds.Add);

            Assert.True(deleted.Removed);
            Assert.Empty(devices.Load());
            Assert.False(lifecycle.IsCredentialBlocked(created.Profile.Id));
            Assert.Equal(3, resetIds.Count(id => id == created.Profile.Id));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class TestProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("protected:" + plainText));

        public string Unprotect(string protectedText)
        {
            var decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedText));
            return decoded["protected:".Length..];
        }
    }
}
