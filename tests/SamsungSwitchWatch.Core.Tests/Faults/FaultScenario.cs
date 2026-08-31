namespace SamsungSwitchWatch.Core.Tests.Faults;

public enum FaultScenarioKind
{
    Normal,
    SlowConnect,
    SlowRead,
    SlowWrite,
    ReadTimeout,
    WriteTimeout,
    ConnectionReset,
    DisconnectAfterBytes,
    DisconnectAfterCommand,
    PartialRead,
    PromptSplitAcrossPackets,
    HugeOutput,
    NeverEndingOutput,
    InvalidTelnetNegotiation,
    PagingLoop,
    ImmediateCloseAfterLogin,
    ImmediateCloseDuringCommand
}

internal sealed record FaultScenario
{
    public FaultScenarioKind Kind { get; init; } = FaultScenarioKind.Normal;

    public IReadOnlyList<byte[]> Reads { get; init; } = [];

    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(100);

    public int DisconnectAfterBytes { get; init; } = int.MaxValue;

    public int DisconnectAfterCommandNumber { get; init; } = 1;

    public int PartialReadBytes { get; init; } = 8;

    public int HugeOutputBytes { get; init; } = 2 * 1024 * 1024;

    public byte[] RepeatingOutput { get; init; } = "synthetic-output\r\n"u8.ToArray();

    public FaultScenario Validate()
    {
        if (Delay < TimeSpan.Zero
            || DisconnectAfterBytes < 0
            || DisconnectAfterCommandNumber < 1
            || PartialReadBytes < 1
            || HugeOutputBytes < 1
            || RepeatingOutput.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FaultScenario));
        }

        return this;
    }
}
