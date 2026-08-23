using System.Security.Cryptography;

namespace SamsungSwitchWatch.Viewer.Services;

internal static class ViewerPairingCode
{
    private const string Prefix = "SSW1.";

    public static bool TryApply(
        ViewerSettings settings,
        string? pairingCode,
        IViewerSecretProtector protector,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);
        if (!TryParse(pairingCode, out var spki, out var bearerToken))
        {
            reason = "Agent Setup에 표시된 SSW1 페어링 코드를 입력해 주세요.";
            return false;
        }

        try
        {
            settings.SetAgentTrustPin(spki);
            settings.SetProtectedAgentBearerToken(protector.Protect(bearerToken));
            reason = string.Empty;
            return true;
        }
        catch
        {
            settings.RemoveAgentTrustPin();
            settings.RemoveProtectedAgentBearerToken();
            reason = "페어링 정보를 Windows 사용자 자격 증명으로 보호하지 못했습니다.";
            return false;
        }
    }

    internal static bool TryParse(
        string? pairingCode,
        out string spkiSha256,
        out string bearerToken)
    {
        spkiSha256 = string.Empty;
        bearerToken = string.Empty;
        var value = pairingCode?.Trim();
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + 86)
        {
            return false;
        }

        var encoded = value[Prefix.Length..];
        Span<char> padded = stackalloc char[88];
        for (var index = 0; index < encoded.Length; index++)
        {
            var character = encoded[index];
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
        padded[86] = '=';
        padded[87] = '=';

        Span<byte> payload = stackalloc byte[64];
        try
        {
            if (!Convert.TryFromBase64Chars(padded, payload, out var written) || written != 64)
            {
                return false;
            }

            spkiSha256 = Convert.ToHexString(payload[..32]);
            bearerToken = Base64Url(payload[32..]);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
