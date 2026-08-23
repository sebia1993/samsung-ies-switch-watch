using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SamsungSwitchWatch.Agent.Setup.Deployment;

internal static class SetupPairingCodeReader
{
    private const string TokenFileName = "api-bearer-token.dpapi";
    private const string SpkiFileName = "agent-spki-sha256.txt";
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy =
        SHA256.HashData("SamsungSwitchWatch.Agent.ApiBearer.v1"u8);

    public static bool TryRead(string dataDirectory, out string pairingCode)
    {
        pairingCode = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var root = Path.GetFullPath(dataDirectory);
            var spkiPath = Path.Combine(root, SpkiFileName);
            var tokenPath = Path.Combine(root, TokenFileName);
            var spki = File.ReadAllText(spkiPath, Encoding.ASCII).Trim();
            if (spki.Length != 64 || !spki.All(character =>
                    character is >= '0' and <= '9'
                        or >= 'A' and <= 'F'
                        or >= 'a' and <= 'f'))
            {
                return false;
            }

            if (!TryReadToken(tokenPath, out var token)) return false;
            try
            {
                var payload = new byte[64];
                try
                {
                    Convert.FromHexString(spki).CopyTo(payload, 0);
                    token.CopyTo(payload, 32);
                    pairingCode = "SSW1." + Convert.ToBase64String(payload)
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_');
                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException
                or Win32Exception or FormatException)
        {
            return false;
        }
    }

    internal static bool TryReadBearerToken(
        string dataDirectory,
        out string bearerToken)
    {
        bearerToken = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var root = Path.GetFullPath(dataDirectory);
            var tokenPath = Path.Combine(root, TokenFileName);
            if (!TryReadToken(tokenPath, out var token)) return false;
            try
            {
                bearerToken = Convert.ToBase64String(token)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException
                or Win32Exception or FormatException)
        {
            return false;
        }
    }

    private static bool TryReadToken(string tokenPath, out byte[] token)
    {
        token = [];
        var protectedToken = ReadBounded(tokenPath, 4096);
        try
        {
            token = Unprotect(protectedToken);
            if (token.Length == 32) return true;
            CryptographicOperations.ZeroMemory(token);
            token = [];
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedToken);
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
            throw new InvalidDataException("Pairing material size is invalid.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] Unprotect(byte[] protectedBytes)
    {
        using var input = DataBlobHandle.FromBytes(protectedBytes);
        using var entropy = DataBlobHandle.FromBytes(Entropy);
        if (!CryptUnprotectData(
                ref input.Blob,
                IntPtr.Zero,
                ref entropy.Blob,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new byte[output.Size];
            if (output.Size > 0) Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                for (var index = 0; index < output.Size; index++)
                {
                    Marshal.WriteByte(output.Data, index, 0);
                }
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private sealed class DataBlobHandle : IDisposable
    {
        private DataBlobHandle(byte[] bytes)
        {
            Blob = new DataBlob { Size = bytes.Length };
            if (bytes.Length == 0) return;
            Blob.Data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, Blob.Data, bytes.Length);
        }

        public DataBlob Blob;
        public static DataBlobHandle FromBytes(byte[] bytes) => new(bytes);

        public void Dispose()
        {
            if (Blob.Data == IntPtr.Zero) return;
            for (var index = 0; index < Blob.Size; index++)
            {
                Marshal.WriteByte(Blob.Data, index, 0);
            }
            Marshal.FreeHGlobal(Blob.Data);
            Blob = default;
        }
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
