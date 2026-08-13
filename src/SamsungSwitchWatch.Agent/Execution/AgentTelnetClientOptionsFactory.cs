using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent.Execution;

internal static class AgentTelnetClientOptionsFactory
{
    // Command echo, the captured prompt, ANSI/paging markers and line endings
    // are needed while locating the terminal prompt but are not returned as
    // operator output. Keep that parsing allowance small and deterministic.
    internal const int ParsingOverheadBytes = 16 * 1024;

    // A Telnet data byte can occupy two wire bytes when represented as IAC IAC.
    // One additional negotiation window allows the normal option exchange while
    // the Core negotiation and paging limits remain independently enforced.
    internal const int NegotiationOverheadBytes = 16 * 1024;

    public static TelnetClientOptions Create(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var maximumOutputBytes = checked(options.MaxOutputBytes + ParsingOverheadBytes);
        var maximumWireBytes = checked(
            maximumOutputBytes * 2 + NegotiationOverheadBytes);

        return new TelnetClientOptions(
            TelnetTimeouts.Default with
            {
                Session = TimeSpan.FromSeconds(options.Telnet.MaxSessionSeconds)
            },
            MaximumOutputBytes: maximumOutputBytes,
            MaximumWireBytes: maximumWireBytes)
        {
            SessionCloseRetryCount = options.Telnet.ImmediateSessionCloseRetryCount,
            SessionCloseRetryDelay =
                TimeSpan.FromSeconds(options.Telnet.ImmediateSessionCloseRetryDelaySeconds)
        };
    }
}
