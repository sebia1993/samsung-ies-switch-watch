namespace SamsungSwitchWatch.Core.Profiles;

/// <summary>
/// Validates the Viewer-driven, read-only CLI surface exposed by the Agent.
/// Every single-line show command is classified and validated while control
/// characters and command separators are rejected before transport use.
/// </summary>
public static class ReadOnlyQueryPolicy
{
    public const int MaximumCommandLength = 128;
    public const int MaximumOutputBytes = 64 * 1024;
    public static readonly TimeSpan CommandIdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CommandHardTimeout = TimeSpan.FromSeconds(90);

    // Preserve the existing public member for compatible callers. It now
    // represents the command inactivity timeout rather than an absolute cap.
    public static readonly TimeSpan CommandTimeout = CommandIdleTimeout;

    public static ReadOnlyQueryValidation Validate(string? command, int maximumLength = MaximumCommandLength)
    {
        if (maximumLength is < 1 or > MaximumCommandLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.Empty);
        }

        // Check the unmodified value first. Trimming must never hide a second line,
        // control character, or shell-style command separator.
        if (command.Length > maximumLength)
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.TooLong);
        }

        if (command.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.ControlCharacter);
        }

        // Telnet transmits CLI data as ISO-8859-1. Reject characters that the
        // wire encoding cannot represent instead of silently replacing them
        // with '?' and executing a different command on the switch. Preserve
        // the established control-character classification above.
        if (command.Any(static character => character > '\u00ff'))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.UnsupportedCharacter);
        }

        if (command.IndexOfAny([';', '|', '&', '`', '$', '<', '>']) >= 0)
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.Separator);
        }

        var normalized = string.Join(' ', command.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0 || normalized.Length > maximumLength)
        {
            return ReadOnlyQueryValidation.Blocked(
                normalized.Length == 0 ? ReadOnlyQueryRejection.Empty : ReadOnlyQueryRejection.TooLong);
        }

        var parts = normalized.Split(' ');
        if (parts.Length < 2 || !string.Equals(parts[0], "show", StringComparison.OrdinalIgnoreCase))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.NotShowCommand);
        }

        var sensitivity = parts[1].Equals("running-config", StringComparison.OrdinalIgnoreCase)
                          || parts[1].Equals("startup-config", StringComparison.OrdinalIgnoreCase)
            ? ReadOnlyQuerySensitivity.Sensitive
            : ReadOnlyQuerySensitivity.Normal;

        return ReadOnlyQueryValidation.Allowed(normalized, sensitivity);
    }

    public static bool IsAllowed(string? command) => Validate(command).IsAllowed;
}

public enum ReadOnlyQueryRejection
{
    None,
    Empty,
    TooLong,
    ControlCharacter,
    Separator,
    NotShowCommand,
    UnsupportedCharacter
}

public enum ReadOnlyQuerySensitivity
{
    Normal,
    Sensitive
}

public sealed record ReadOnlyQueryValidation(
    bool IsAllowed,
    string? NormalizedCommand,
    ReadOnlyQueryRejection Rejection,
    ReadOnlyQuerySensitivity Sensitivity)
{
    internal static ReadOnlyQueryValidation Allowed(
        string command,
        ReadOnlyQuerySensitivity sensitivity) =>
        new(true, command, ReadOnlyQueryRejection.None, sensitivity);

    internal static ReadOnlyQueryValidation Blocked(ReadOnlyQueryRejection rejection) =>
        new(false, null, rejection, ReadOnlyQuerySensitivity.Normal);
}
