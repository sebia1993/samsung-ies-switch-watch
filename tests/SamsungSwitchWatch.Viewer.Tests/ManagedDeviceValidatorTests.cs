using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ManagedDeviceValidatorTests
{
    [Fact]
    public void ConnectionInput_AllowsModelToBePendingBeforeAgentDetection()
    {
        var draft = ValidDraft();
        draft.Model = string.Empty;

        var valid = ManagedDeviceValidator.TryValidateConnectionInput(
            draft,
            passwordRequired: true,
            out var reason);

        Assert.True(valid);
        Assert.Empty(reason);
    }

    [Fact]
    public void SaveValidation_RequiresDetectedSupportedModel()
    {
        var draft = ValidDraft();
        draft.Model = string.Empty;

        var valid = ManagedDeviceValidator.TryValidate(
            draft,
            passwordRequired: true,
            out var reason);

        Assert.False(valid);
        Assert.Contains("자동 판별", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionInput_StillRejectsInvalidEndpointAndCredentials()
    {
        var draft = ValidDraft();
        draft.Model = string.Empty;
        draft.Host = "not-an-ip";

        Assert.False(ManagedDeviceValidator.TryValidateConnectionInput(
            draft,
            passwordRequired: true,
            out var reason));
        Assert.Contains("IPv4", reason, StringComparison.Ordinal);
    }

    private static ManagedDeviceDraft ValidDraft() => new()
    {
        DisplayName = "ACCESS-SW-01",
        Model = "IES4224GP",
        Host = "192.0.2.10",
        Username = "operator",
        Password = "password"
    };
}
