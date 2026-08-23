using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SamsungSwitchWatch.Agent.Api;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class AgentAuthenticationTests
{
    [Fact]
    public void PairingCode_ContainsExactSpkiAndThirtyTwoByteToken()
    {
        using var identity = EphemeralAgentIdentityFactory.Create();
        var token = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        using var authentication = new AgentAuthenticationMaterial(token);

        var pairingCode = authentication.CreatePairingCode(identity);
        var encoded = pairingCode["SSW1.".Length..].Replace('-', '+').Replace('_', '/');
        var payload = Convert.FromBase64String(encoded + "==");
        try
        {
            Assert.StartsWith("SSW1.", pairingCode, StringComparison.Ordinal);
            Assert.Equal(91, pairingCode.Length);
            Assert.Equal(
                identity.CertificatePublicKeySha256,
                Convert.ToHexString(payload.AsSpan(0, 32)));
            Assert.Equal(token, payload.AsSpan(32, 32).ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [Fact]
    public void BearerValidation_AcceptsOnlyExactBase64UrlToken()
    {
        var token = RandomNumberGenerator.GetBytes(32);
        using var authentication = new AgentAuthenticationMaterial(token);
        var encoded = Convert.ToBase64String(token)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var wrong = token.ToArray();
        wrong[^1] ^= 0x01;
        var wrongEncoded = Convert.ToBase64String(wrong)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.True(BearerAuthenticationMiddleware.IsAuthorized(
            $"Bearer {encoded}", authentication));
        Assert.True(BearerAuthenticationMiddleware.IsAuthorized(
            $"bearer {encoded}", authentication));
        Assert.False(BearerAuthenticationMiddleware.IsAuthorized(null, authentication));
        Assert.False(BearerAuthenticationMiddleware.IsAuthorized("Bearer", authentication));
        Assert.False(BearerAuthenticationMiddleware.IsAuthorized(
            $"Bearer {wrongEncoded}", authentication));
        Assert.False(BearerAuthenticationMiddleware.IsAuthorized(
            $"Basic {encoded}", authentication));

        CryptographicOperations.ZeroMemory(token);
        CryptographicOperations.ZeroMemory(wrong);
    }

    [Fact]
    public void MockAuthentication_UsesOnlyAnExactEnvironmentTokenWhenConfigured()
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var encoded = Convert.ToBase64String(token)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        try
        {
            using var authentication = AgentAuthenticationStore.LoadMock(encoded);

            Assert.True(BearerAuthenticationMiddleware.IsAuthorized(
                $"Bearer {encoded}",
                authentication));

            Assert.Throws<AgentConfigurationException>(() =>
                AgentAuthenticationStore.LoadMock("invalid"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    [Fact]
    public async Task ApiV5AndHealth_RequireBearerWhileV4RequiresUpgrade()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var unauthenticated = new HttpClient { BaseAddress = host.Client.BaseAddress };

        using var deniedLive = await unauthenticated.GetAsync("/health/live");
        using var deniedReady = await unauthenticated.GetAsync("/health/ready");
        using var deniedIdentity = await unauthenticated.GetAsync("/api/v5/identity");
        using var deniedTest = await unauthenticated.PostAsync(
            "/api/v5/telnet/test",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        using var deniedExecute = await unauthenticated.PostAsync(
            "/api/v5/telnet/execute",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        using var deniedV4 = await unauthenticated.GetAsync("/api/v4/identity");
        using var deniedHealthVariant = await unauthenticated.GetAsync("/health/live/");
        using var deniedHealthMethod = await unauthenticated.PostAsync(
            "/health/live",
            new StringContent(string.Empty));
        using var live = await host.Client.GetAsync("/health/live");
        using var ready = await host.Client.GetAsync("/health/ready");
        using var upgrade = await host.Client.GetAsync("/api/v4/identity");

        Assert.Equal(HttpStatusCode.Unauthorized, deniedLive.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedIdentity.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedTest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedExecute.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedV4.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedHealthVariant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedHealthMethod.StatusCode);
        Assert.Equal(426, (int)upgrade.StatusCode);
        Assert.Equal("Bearer", deniedLive.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("Bearer", deniedReady.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("Bearer", deniedIdentity.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("AUTH_REQUIRED", await ErrorCodeAsync(deniedLive));
        Assert.Equal("AUTH_REQUIRED", await ErrorCodeAsync(deniedReady));
        Assert.Equal("AUTH_REQUIRED", await ErrorCodeAsync(deniedIdentity));
        Assert.Equal("AUTH_REQUIRED", await ErrorCodeAsync(deniedTest));
        Assert.Equal("AUTH_REQUIRED", await ErrorCodeAsync(deniedExecute));
        Assert.Equal("AUTH_REQUIRED", await ErrorCodeAsync(deniedV4));
        Assert.Equal("AUTH_REQUIRED", await ErrorCodeAsync(deniedHealthVariant));
        Assert.Equal("AUTH_REQUIRED", await ErrorCodeAsync(deniedHealthMethod));
        Assert.Equal("AGENT_API_UPGRADE_REQUIRED", await ErrorCodeAsync(upgrade));

        using var liveJson = JsonDocument.Parse(await live.Content.ReadAsStringAsync());
        using var readyJson = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal(["status", "utc"], liveJson.RootElement.EnumerateObject()
            .Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["apiVersion", "productVersion", "protocol", "status", "utc"],
            readyJson.RootElement.EnumerateObject()
                .Select(property => property.Name).Order().ToArray());
        Assert.Equal(5, readyJson.RootElement.GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task ApiV5_WrongAndMalformedTokensReturnSameSafeResponse()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var wrong = new HttpClient { BaseAddress = host.Client.BaseAddress };
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        using var malformed = new HttpClient { BaseAddress = host.Client.BaseAddress };
        malformed.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer not-a-token");

        using var wrongResponse = await wrong.GetAsync("/api/v5/identity");
        using var malformedResponse = await malformed.GetAsync("/api/v5/identity");
        var wrongBody = await wrongResponse.Content.ReadAsStringAsync();
        var malformedBody = await malformedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, malformedResponse.StatusCode);
        Assert.Equal(wrongBody, malformedBody);
        Assert.DoesNotContain("token", wrongBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthenticationStore_PersistsDpapiProtectedTokenOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                ListenUrl = "https://0.0.0.0:18443"
            };
            using var identity = AgentIdentityStore.LoadOrCreate(options);
            string firstCode;
            using (var first = AgentAuthenticationStore.LoadOrCreate(options, identity))
            {
                firstCode = first.CreatePairingCode(identity);
            }
            using var second = AgentAuthenticationStore.LoadOrCreate(options, identity);

            Assert.Equal(firstCode, second.CreatePairingCode(identity));
            Assert.True(File.Exists(Path.Combine(folder, AgentAuthenticationStore.TokenFileName)));
            Assert.Equal(
                identity.CertificatePublicKeySha256,
                File.ReadAllText(Path.Combine(folder, AgentAuthenticationStore.PublicSpkiFileName)).Trim());
            Assert.DoesNotContain(
                firstCode["SSW1.".Length..],
                Convert.ToBase64String(File.ReadAllBytes(
                    Path.Combine(folder, AgentAuthenticationStore.TokenFileName))),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
