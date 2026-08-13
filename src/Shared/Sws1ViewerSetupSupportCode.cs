using System.Globalization;

namespace SamsungSwitchWatch.Viewer.Setup.Diagnostics;

internal enum ViewerSetupDiagnosticOperation : byte
{
    Unknown = 0,
    Install = 1,
    Recovery = 2,
    RecoveryInspection = 3
}

internal enum ViewerSetupDiagnosticCode : byte
{
    Unknown = 0,
    Ok = 1,
    PackageInvalid = 2,
    InstallWriteFailed = 3,
    PackageNotFound = 4,
    ManifestInvalid = 5,
    PackageHashMismatch = 6,
    PathInvalid = 7,
    PathNotWritable = 8,
    AlreadyRunning = 9,
    RecoveryRequired = 10,
    ViewerRunning = 11,
    SmokeFailed = 12,
    LaunchFailed = 13,
    ShortcutFailed = 14,
    RollbackFailed = 15,
    Cancelled = 16,
    Unexpected = 17,
    QuarantineFailed = 18
}

internal enum ViewerSetupDiagnosticStage : byte
{
    None = 0,
    Lock = 1,
    RecoveryGate = 2,
    Path = 3,
    Package = 4,
    Shutdown = 5,
    Staging = 6,
    Backup = 7,
    Activation = 8,
    Smoke = 9,
    Shortcut = 10,
    Launch = 11,
    CommitCleanup = 12,
    Recovery = 13,
    Quarantine = 14,
    Unknown = 15
}

internal enum ViewerSetupDiagnosticRollbackState : byte
{
    NotRun = 0,
    Succeeded = 1,
    Failed = 2,
    Unknown = 3
}

internal enum ViewerSetupDiagnosticJournalState : byte
{
    None = 0,
    Recoverable = 1,
    Unreadable = 2,
    Unknown = 3
}

internal enum ViewerSetupDiagnosticQuarantineState : byte
{
    NotRun = 0,
    NotRequired = 1,
    IsolatedPending = 2,
    Restored = 3,
    Retained = 4,
    FinalizationPending = 5,
    RestoreFailed = 6,
    Unknown = 7
}

internal enum ViewerSetupDiagnosticPreviousInstallState : byte
{
    Unknown = 0,
    None = 1,
    Verified = 2,
    Invalid = 3
}

internal enum ViewerSetupDiagnosticStageState : byte
{
    NotRun = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

internal readonly record struct ViewerSetupDiagnosticStageStates(
    ViewerSetupDiagnosticStageState Package,
    ViewerSetupDiagnosticStageState ExistingInstall,
    ViewerSetupDiagnosticStageState Shutdown,
    ViewerSetupDiagnosticStageState Staging,
    ViewerSetupDiagnosticStageState Activation,
    ViewerSetupDiagnosticStageState Smoke,
    ViewerSetupDiagnosticStageState Shortcut,
    ViewerSetupDiagnosticStageState Launch,
    ViewerSetupDiagnosticStageState Recovery)
{
    public static ViewerSetupDiagnosticStageStates Empty { get; } = new(
        ViewerSetupDiagnosticStageState.NotRun,
        ViewerSetupDiagnosticStageState.NotRun,
        ViewerSetupDiagnosticStageState.NotRun,
        ViewerSetupDiagnosticStageState.NotRun,
        ViewerSetupDiagnosticStageState.NotRun,
        ViewerSetupDiagnosticStageState.NotRun,
        ViewerSetupDiagnosticStageState.NotRun,
        ViewerSetupDiagnosticStageState.NotRun,
        ViewerSetupDiagnosticStageState.NotRun);
}

internal sealed record ViewerSetupDiagnosticSnapshot(
    string ProductVersion,
    ViewerSetupDiagnosticOperation Operation,
    ViewerSetupDiagnosticCode FinalCode,
    ViewerSetupDiagnosticCode PrimaryCode,
    ViewerSetupDiagnosticStage FailedStage,
    ViewerSetupDiagnosticRollbackState RollbackState,
    ViewerSetupDiagnosticJournalState JournalState,
    ViewerSetupDiagnosticQuarantineState QuarantineState,
    ViewerSetupDiagnosticPreviousInstallState PreviousInstallState,
    ViewerSetupDiagnosticStageStates Stages);

internal readonly record struct Sws1ViewerSetupPayload(
    byte ProtocolVersion,
    byte ProductMajor,
    byte ProductMinor,
    byte ProductPatch,
    ViewerSetupDiagnosticOperation Operation,
    ViewerSetupDiagnosticCode FinalCode,
    ViewerSetupDiagnosticCode PrimaryCode,
    ViewerSetupDiagnosticStage FailedStage,
    ViewerSetupDiagnosticRollbackState RollbackState,
    ViewerSetupDiagnosticJournalState JournalState,
    ViewerSetupDiagnosticQuarantineState QuarantineState,
    ViewerSetupDiagnosticPreviousInstallState PreviousInstallState,
    ViewerSetupDiagnosticStageStates Stages)
{
    public const byte CurrentProtocolVersion = 1;
    public const byte UnknownMajor = 0x0F;
    public const byte UnknownMinorOrPatch = 0x3F;

    public static Sws1ViewerSetupPayload From(ViewerSetupDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var version = ParseVersion(snapshot.ProductVersion);
        return new Sws1ViewerSetupPayload(
            CurrentProtocolVersion,
            version.Major,
            version.Minor,
            version.Patch,
            snapshot.Operation,
            snapshot.FinalCode,
            snapshot.PrimaryCode,
            snapshot.FailedStage,
            snapshot.RollbackState,
            snapshot.JournalState,
            snapshot.QuarantineState,
            snapshot.PreviousInstallState,
            snapshot.Stages);
    }

    private static (byte Major, byte Minor, byte Patch) ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return UnknownVersion();
        var core = value.Trim();
        if (core.StartsWith('v') || core.StartsWith('V')) core = core[1..];
        var suffix = core.IndexOfAny(['-', '+']);
        if (suffix >= 0) core = core[..suffix];
        var parts = core.Split('.');
        if (parts.Length != 3 ||
            !byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !byte.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch) ||
            major >= UnknownMajor ||
            minor >= UnknownMinorOrPatch ||
            patch >= UnknownMinorOrPatch)
        {
            return UnknownVersion();
        }

        return (major, minor, patch);
    }

    private static (byte Major, byte Minor, byte Patch) UnknownVersion() =>
        (UnknownMajor, UnknownMinorOrPatch, UnknownMinorOrPatch);
}

internal static class Sws1ViewerSetupSupportCode
{
    private const string Prefix = "SWS1";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int PayloadBytes = 9;
    private const int EncodedBytes = 10;
    private const int EncodedCharacters = 16;

    public static string Encode(ViewerSetupDiagnosticSnapshot snapshot) =>
        Encode(Sws1ViewerSetupPayload.From(snapshot));

    public static string Encode(Sws1ViewerSetupPayload payload)
    {
        Validate(payload);
        Span<byte> bytes = stackalloc byte[EncodedBytes];
        bytes.Clear();
        var writer = new BitWriter(bytes[..PayloadBytes]);
        writer.Write(payload.ProtocolVersion, 2);
        writer.Write(payload.ProductMajor, 4);
        writer.Write(payload.ProductMinor, 6);
        writer.Write(payload.ProductPatch, 6);
        writer.Write((byte)payload.Operation, 2);
        writer.Write((byte)payload.FinalCode, 5);
        writer.Write((byte)payload.PrimaryCode, 5);
        writer.Write((byte)payload.FailedStage, 4);
        writer.Write((byte)payload.RollbackState, 2);
        writer.Write((byte)payload.JournalState, 2);
        writer.Write((byte)payload.QuarantineState, 3);
        writer.Write((byte)payload.PreviousInstallState, 2);
        WriteStages(ref writer, payload.Stages);
        writer.Write(0, 11);
        if (writer.BitsWritten != PayloadBytes * 8)
        {
            throw new InvalidOperationException("SWS1 payload must contain 72 bits.");
        }

        bytes[PayloadBytes] = ComputeCrc8(bytes[..PayloadBytes]);
        Span<char> encoded = stackalloc char[EncodedCharacters];
        for (var index = 0; index < encoded.Length; index++)
        {
            encoded[index] = Alphabet[ReadBits(bytes, index * 5, 5)];
        }

        return string.Create(
            24,
            encoded.ToString(),
            static (destination, source) =>
            {
                "SWS1-".AsSpan().CopyTo(destination);
                source.AsSpan(0, 4).CopyTo(destination[5..]);
                destination[9] = '-';
                source.AsSpan(4, 4).CopyTo(destination[10..]);
                destination[14] = '-';
                source.AsSpan(8, 4).CopyTo(destination[15..]);
                destination[19] = '-';
                source.AsSpan(12, 4).CopyTo(destination[20..]);
            });
    }

    public static bool TryDecode(string? value, out Sws1ViewerSetupPayload payload)
    {
        payload = default;
        if (!TryNormalize(value, out var encoded)) return false;

        Span<byte> bytes = stackalloc byte[EncodedBytes];
        bytes.Clear();
        for (var index = 0; index < EncodedCharacters; index++)
        {
            var decoded = DecodeCharacter(encoded[index]);
            if (decoded < 0) return false;
            WriteBits(bytes, index * 5, 5, decoded);
        }

        if (bytes[PayloadBytes] != ComputeCrc8(bytes[..PayloadBytes])) return false;

        try
        {
            var reader = new BitReader(bytes[..PayloadBytes]);
            var protocol = (byte)reader.Read(2);
            var major = (byte)reader.Read(4);
            var minor = (byte)reader.Read(6);
            var patch = (byte)reader.Read(6);
            var operation = (ViewerSetupDiagnosticOperation)reader.Read(2);
            var finalCode = (ViewerSetupDiagnosticCode)reader.Read(5);
            var primaryCode = (ViewerSetupDiagnosticCode)reader.Read(5);
            var failedStage = (ViewerSetupDiagnosticStage)reader.Read(4);
            var rollback = (ViewerSetupDiagnosticRollbackState)reader.Read(2);
            var journal = (ViewerSetupDiagnosticJournalState)reader.Read(2);
            var quarantine = (ViewerSetupDiagnosticQuarantineState)reader.Read(3);
            var previous = (ViewerSetupDiagnosticPreviousInstallState)reader.Read(2);
            var stages = ReadStages(ref reader);
            var reserved = reader.Read(11);
            if (protocol != Sws1ViewerSetupPayload.CurrentProtocolVersion ||
                reserved != 0 ||
                !IsCanonicalVersion(major, minor, patch) ||
                !AreDefined(operation, finalCode, primaryCode, failedStage, rollback, journal, quarantine, previous, stages))
            {
                return false;
            }

            payload = new Sws1ViewerSetupPayload(
                protocol,
                major,
                minor,
                patch,
                operation,
                finalCode,
                primaryCode,
                failedStage,
                rollback,
                journal,
                quarantine,
                previous,
                stages);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            payload = default;
            return false;
        }
    }

    private static void Validate(Sws1ViewerSetupPayload payload)
    {
        if (payload.ProtocolVersion != Sws1ViewerSetupPayload.CurrentProtocolVersion ||
            !IsCanonicalVersion(payload.ProductMajor, payload.ProductMinor, payload.ProductPatch) ||
            !AreDefined(payload.Operation, payload.FinalCode, payload.PrimaryCode, payload.FailedStage,
                payload.RollbackState, payload.JournalState, payload.QuarantineState,
                payload.PreviousInstallState, payload.Stages))
        {
            throw new ArgumentException("SWS1 payload contains an unsupported value.", nameof(payload));
        }
    }

    private static bool IsCanonicalVersion(byte major, byte minor, byte patch) =>
        major == Sws1ViewerSetupPayload.UnknownMajor &&
        minor == Sws1ViewerSetupPayload.UnknownMinorOrPatch &&
        patch == Sws1ViewerSetupPayload.UnknownMinorOrPatch ||
        major < Sws1ViewerSetupPayload.UnknownMajor &&
        minor < Sws1ViewerSetupPayload.UnknownMinorOrPatch &&
        patch < Sws1ViewerSetupPayload.UnknownMinorOrPatch;

    private static bool AreDefined(
        ViewerSetupDiagnosticOperation operation,
        ViewerSetupDiagnosticCode finalCode,
        ViewerSetupDiagnosticCode primaryCode,
        ViewerSetupDiagnosticStage stage,
        ViewerSetupDiagnosticRollbackState rollback,
        ViewerSetupDiagnosticJournalState journal,
        ViewerSetupDiagnosticQuarantineState quarantine,
        ViewerSetupDiagnosticPreviousInstallState previous,
        ViewerSetupDiagnosticStageStates stages) =>
        Enum.IsDefined(operation) && Enum.IsDefined(finalCode) && Enum.IsDefined(primaryCode) &&
        Enum.IsDefined(stage) && Enum.IsDefined(rollback) && Enum.IsDefined(journal) &&
        Enum.IsDefined(quarantine) && Enum.IsDefined(previous) &&
        Enum.IsDefined(stages.Package) && Enum.IsDefined(stages.ExistingInstall) &&
        Enum.IsDefined(stages.Shutdown) && Enum.IsDefined(stages.Staging) &&
        Enum.IsDefined(stages.Activation) && Enum.IsDefined(stages.Smoke) &&
        Enum.IsDefined(stages.Shortcut) && Enum.IsDefined(stages.Launch) &&
        Enum.IsDefined(stages.Recovery);

    private static void WriteStages(ref BitWriter writer, ViewerSetupDiagnosticStageStates stages)
    {
        writer.Write((byte)stages.Package, 2);
        writer.Write((byte)stages.ExistingInstall, 2);
        writer.Write((byte)stages.Shutdown, 2);
        writer.Write((byte)stages.Staging, 2);
        writer.Write((byte)stages.Activation, 2);
        writer.Write((byte)stages.Smoke, 2);
        writer.Write((byte)stages.Shortcut, 2);
        writer.Write((byte)stages.Launch, 2);
        writer.Write((byte)stages.Recovery, 2);
    }

    private static ViewerSetupDiagnosticStageStates ReadStages(ref BitReader reader) => new(
        (ViewerSetupDiagnosticStageState)reader.Read(2),
        (ViewerSetupDiagnosticStageState)reader.Read(2),
        (ViewerSetupDiagnosticStageState)reader.Read(2),
        (ViewerSetupDiagnosticStageState)reader.Read(2),
        (ViewerSetupDiagnosticStageState)reader.Read(2),
        (ViewerSetupDiagnosticStageState)reader.Read(2),
        (ViewerSetupDiagnosticStageState)reader.Read(2),
        (ViewerSetupDiagnosticStageState)reader.Read(2),
        (ViewerSetupDiagnosticStageState)reader.Read(2));

    private static bool TryNormalize(string? value, out string encoded)
    {
        encoded = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        Span<char> normalized = stackalloc char[Prefix.Length + EncodedCharacters];
        var count = 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character == '-') continue;
            if (count == normalized.Length) return false;
            normalized[count++] = char.ToUpperInvariant(character);
        }

        if (count != normalized.Length || !normalized[..Prefix.Length].SequenceEqual(Prefix)) return false;
        encoded = normalized[Prefix.Length..].ToString();
        return true;
    }

    private static int DecodeCharacter(char character)
    {
        var normalized = character switch { 'O' => '0', 'I' or 'L' => '1', _ => character };
        return Alphabet.IndexOf(normalized);
    }

    private static byte ComputeCrc8(ReadOnlySpan<byte> payload)
    {
        byte crc = 0;
        foreach (var value in payload)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
            }
        }
        return crc;
    }

    private static int ReadBits(ReadOnlySpan<byte> bytes, int bitOffset, int bitCount)
    {
        var result = 0;
        for (var bit = 0; bit < bitCount; bit++)
        {
            var absolute = bitOffset + bit;
            result = (result << 1) | ((bytes[absolute / 8] >> (7 - absolute % 8)) & 1);
        }
        return result;
    }

    private static void WriteBits(Span<byte> bytes, int bitOffset, int bitCount, int value)
    {
        for (var bit = 0; bit < bitCount; bit++)
        {
            var absolute = bitOffset + bit;
            var mask = (byte)(1 << (7 - absolute % 8));
            if (((value >> (bitCount - bit - 1)) & 1) != 0) bytes[absolute / 8] |= mask;
        }
    }

    private ref struct BitWriter
    {
        private readonly Span<byte> _bytes;
        private int _offset;

        public BitWriter(Span<byte> bytes)
        {
            _bytes = bytes;
            _offset = 0;
        }

        public int BitsWritten => _offset;

        public void Write(int value, int bits)
        {
            if (bits is <= 0 or > 31 || value < 0 || _offset + bits > _bytes.Length * 8 ||
                (long)value >= 1L << bits)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            WriteBits(_bytes, _offset, bits, value);
            _offset += bits;
        }
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _offset;

        public BitReader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _offset = 0;
        }

        public int Read(int bits)
        {
            if (bits is <= 0 or > 31 || _offset + bits > _bytes.Length * 8)
            {
                throw new ArgumentOutOfRangeException(nameof(bits));
            }
            var result = ReadBits(_bytes, _offset, bits);
            _offset += bits;
            return result;
        }
    }
}
