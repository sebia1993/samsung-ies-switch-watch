using System.Text.RegularExpressions;

namespace SamsungSwitchWatch.Core.Parsing;

public enum SwitchModelDetectionStatus
{
    Detected,
    NotDetected,
    Ambiguous
}

public sealed record SwitchModelDetectionResult(
    SwitchModelDetectionStatus Status,
    string? Model);

/// <summary>
/// Detects one exact registered model token from sanitized command output.
/// The caller supplies the registered model set so this parser cannot invent
/// or silently accept an unsupported device family.
/// </summary>
public static class SwitchModelDetector
{
    public static SwitchModelDetectionResult Detect(
        string? output,
        IEnumerable<string> supportedModels)
    {
        ArgumentNullException.ThrowIfNull(supportedModels);

        var models = supportedModels
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static model => model.Length)
            .ToArray();
        if (models.Length == 0)
        {
            throw new ArgumentException(
                "At least one supported switch model is required.",
                nameof(supportedModels));
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return new SwitchModelDetectionResult(
                SwitchModelDetectionStatus.NotDetected,
                null);
        }

        var pattern = $@"(?<![A-Z0-9])(?:{string.Join('|', models.Select(Regex.Escape))})(?![A-Z0-9])";
        var detected = Regex.Matches(
                output,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => models.First(model =>
                model.Equals(match.Value, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return detected.Length switch
        {
            1 => new SwitchModelDetectionResult(
                SwitchModelDetectionStatus.Detected,
                detected[0]),
            0 => new SwitchModelDetectionResult(
                SwitchModelDetectionStatus.NotDetected,
                null),
            _ => new SwitchModelDetectionResult(
                SwitchModelDetectionStatus.Ambiguous,
                null)
        };
    }
}
