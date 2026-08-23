using System.Security.Cryptography;
using System.Text;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerPairingCodeTests
{
    [Fact]
    public void PairingCode_ParsesExactSpkiAndBearerToken()
    {
        var payload = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        var code = "SSW1." + ViewerPairingCode.Base64Url(payload);

        Assert.True(ViewerPairingCode.TryParse(code, out var spki, out var bearer));
        Assert.Equal(Convert.ToHexString(payload.AsSpan(0, 32)), spki);
        Assert.Equal(ViewerPairingCode.Base64Url(payload.AsSpan(32, 32)), bearer);

        CryptographicOperations.ZeroMemory(payload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SSW0.invalid")]
    [InlineData("SSW1.invalid")]
    [InlineData("SSW1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void PairingCode_RejectsMalformedValues(string? value)
    {
        Assert.False(ViewerPairingCode.TryParse(value, out _, out _));
    }

    [Fact]
    public void PairingCode_StoresOnlyProtectedTokenForSelectedAuthority()
    {
        var payload = RandomNumberGenerator.GetBytes(64);
        var code = "SSW1." + ViewerPairingCode.Base64Url(payload);
        var settings = new ViewerSettings
        {
            AgentUri = "https://agent.example.test:18443"
        };
        var protector = new SyntheticProtector();

        Assert.True(ViewerPairingCode.TryApply(
            settings,
            code,
            protector,
            out var reason), reason);
        Assert.True(settings.TryGetAgentTrustPin(out var pin));
        Assert.Equal(Convert.ToHexString(payload.AsSpan(0, 32)), pin);
        Assert.True(settings.TryGetProtectedAgentBearerToken(out var protectedToken));
        Assert.NotEmpty(Convert.FromBase64String(protectedToken));
        Assert.DoesNotContain(
            ViewerPairingCode.Base64Url(payload.AsSpan(32, 32)),
            protectedToken,
            StringComparison.Ordinal);
        Assert.True(settings.HasAgentPairingCredential());

        CryptographicOperations.ZeroMemory(payload);
    }

    private sealed class SyntheticProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes("protected:" + plainText));

        public string Unprotect(string protectedText) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(protectedText))["protected:".Length..];
    }
}
