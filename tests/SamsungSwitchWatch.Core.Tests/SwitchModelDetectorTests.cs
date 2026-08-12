using SamsungSwitchWatch.Core.Parsing;

namespace SamsungSwitchWatch.Core.Tests;

public sealed class SwitchModelDetectorTests
{
    private static readonly string[] SupportedModels =
        ["IES4224GP", "IES4028XP", "IES4226XP"];

    [Theory]
    [InlineData("Model Name : IES4224GP", "IES4224GP")]
    [InlineData("product=ies4028xp", "IES4028XP")]
    [InlineData("Samsung Ethernet Switch IES4226XP\r\nSW#", "IES4226XP")]
    [InlineData("IES4224GP\r\nIES4224GP", "IES4224GP")]
    public void Detect_ReturnsOneCanonicalRegisteredModel(
        string output,
        string expected)
    {
        var result = SwitchModelDetector.Detect(output, SupportedModels);

        Assert.Equal(SwitchModelDetectionStatus.Detected, result.Status);
        Assert.Equal(expected, result.Model);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown Samsung Ethernet Switch")]
    [InlineData("IES4224GPX")]
    [InlineData("XIES4028XP")]
    public void Detect_DoesNotGuessUnknownOrPartialTokens(string? output)
    {
        var result = SwitchModelDetector.Detect(output, SupportedModels);

        Assert.Equal(SwitchModelDetectionStatus.NotDetected, result.Status);
        Assert.Null(result.Model);
    }

    [Fact]
    public void Detect_RejectsOutputContainingMultipleRegisteredModels()
    {
        var result = SwitchModelDetector.Detect(
            "Active IES4224GP, fallback IES4226XP",
            SupportedModels);

        Assert.Equal(SwitchModelDetectionStatus.Ambiguous, result.Status);
        Assert.Null(result.Model);
    }

    [Fact]
    public void Detect_RequiresARegisteredModelSet()
    {
        Assert.Throws<ArgumentException>(() =>
            SwitchModelDetector.Detect("IES4224GP", []));
    }
}
