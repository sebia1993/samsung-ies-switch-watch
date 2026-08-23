using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;

namespace SamsungSwitchWatch.Agent.Security;

public sealed class AgentAuthenticationMaterial : IDisposable
{
    public const int TokenLength = 32;
    private byte[]? _token;

    internal AgentAuthenticationMaterial(byte[] token)
    {
        if (token.Length != TokenLength)
        {
            throw new ArgumentException("Agent bearer token must be 32 bytes.", nameof(token));
        }

        _token = token.ToArray();
    }

    public string CreatePairingCode(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var token = _token ?? throw new ObjectDisposedException(nameof(AgentAuthenticationMaterial));
        var payload = new byte[64];
        try
        {
            Convert.FromHexString(identity.CertificatePublicKeySha256).CopyTo(payload, 0);
            token.CopyTo(payload, 32);
            return $"SSW1.{Base64Url.Encode(payload)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal bool FixedTimeEquals(ReadOnlySpan<byte> candidate)
    {
        var token = _token;
        return token is not null
            && candidate.Length == token.Length
            && CryptographicOperations.FixedTimeEquals(candidate, token);
    }

    public void Dispose()
    {
        var token = Interlocked.Exchange(ref _token, null);
        if (token is not null)
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }
}

public static class AgentAuthenticationStore
{
    internal const string MockBearerTokenEnvironmentVariable = "SSW_MOCK_BEARER_TOKEN";
    internal const int MaximumProtectedTokenBytes = 4096;
    internal const string TokenFileName = "api-bearer-token.dpapi";
    internal const string PublicSpkiFileName = "agent-spki-sha256.txt";
    private static readonly byte[] Entropy =
        SHA256.HashData("SamsungSwitchWatch.Agent.ApiBearer.v1"u8);

    public static AgentAuthenticationMaterial LoadOrCreate(
        AgentOptions options,
        AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);

        if (options.MockMode)
        {
            return LoadMock(Environment.GetEnvironmentVariable(
                MockBearerTokenEnvironmentVariable));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw InvalidAuthenticationMaterial();
        }

        Directory.CreateDirectory(options.DataDirectory);
        var tokenPath = Path.Combine(options.DataDirectory, TokenFileName);
        var material = File.Exists(tokenPath)
            ? LoadExisting(tokenPath)
            : Create(tokenPath);

        try
        {
            EnsurePublicSpki(options.DataDirectory, identity.CertificatePublicKeySha256);
            return material;
        }
        catch
        {
            material.Dispose();
            throw;
        }
    }

    internal static AgentAuthenticationMaterial LoadMock(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new AgentAuthenticationMaterial(RandomNumberGenerator.GetBytes(
                AgentAuthenticationMaterial.TokenLength));
        }

        Span<byte> token = stackalloc byte[AgentAuthenticationMaterial.TokenLength];
        try
        {
            if (!Base64Url.TryDecode32(configured, token))
            {
                throw InvalidAuthenticationMaterial();
            }

            return new AgentAuthenticationMaterial(token.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    [SupportedOSPlatform("windows")]
    private static AgentAuthenticationMaterial Create(string path)
    {
        var token = RandomNumberGenerator.GetBytes(AgentAuthenticationMaterial.TokenLength);
        byte[]? protectedToken = null;
        try
        {
            protectedToken = ProtectedData.Protect(
                token,
                Entropy,
                DataProtectionScope.LocalMachine);
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                stream.Write(protectedToken);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                return LoadExisting(path);
            }

            return new AgentAuthenticationMaterial(token);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw InvalidAuthenticationMaterial();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
            if (protectedToken is not null)
            {
                CryptographicOperations.ZeroMemory(protectedToken);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static AgentAuthenticationMaterial LoadExisting(string path)
    {
        byte[]? protectedToken = null;
        byte[]? token = null;
        try
        {
            protectedToken = ReadBounded(path, MaximumProtectedTokenBytes);
            token = ProtectedData.Unprotect(
                protectedToken,
                Entropy,
                DataProtectionScope.LocalMachine);
            if (token.Length != AgentAuthenticationMaterial.TokenLength)
            {
                throw new CryptographicException("Agent bearer token length is invalid.");
            }

            return new AgentAuthenticationMaterial(token);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
        {
            throw InvalidAuthenticationMaterial();
        }
        finally
        {
            if (protectedToken is not null)
            {
                CryptographicOperations.ZeroMemory(protectedToken);
            }
            if (token is not null)
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Agent authentication file size is invalid.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void EnsurePublicSpki(string dataDirectory, string expectedSpki)
    {
        var path = Path.Combine(dataDirectory, PublicSpkiFileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path, Encoding.ASCII).Trim();
            if (existing.Equals(expectedSpki, StringComparison.Ordinal))
            {
                return;
            }
        }

        var pending = path + ".pending";
        File.WriteAllText(pending, expectedSpki + Environment.NewLine, Encoding.ASCII);
        File.Move(pending, path, overwrite: true);
    }

    private static AgentConfigurationException InvalidAuthenticationMaterial() =>
        new(
            AgentErrorCodes.AuthenticationMaterialInvalid,
            "Agent authentication material could not be loaded.");
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static bool TryDecode32(string value, Span<byte> destination)
    {
        if (value.Length != 43 || destination.Length < 32)
        {
            return false;
        }

        Span<char> padded = stackalloc char[44];
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            padded[index] = character switch
            {
                '-' => '+',
                '_' => '/',
                >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' => character,
                _ => '\0'
            };
            if (padded[index] == '\0')
            {
                return false;
            }
        }
        padded[43] = '=';
        return Convert.TryFromBase64Chars(padded, destination, out var written) && written == 32;
    }
}
