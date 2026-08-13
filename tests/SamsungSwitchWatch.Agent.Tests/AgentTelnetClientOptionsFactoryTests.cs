using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Execution;
using SamsungSwitchWatch.Core.Diagnostics;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class AgentTelnetClientOptionsFactoryTests
{
    [Theory]
    [InlineData(1024, 17_408, 51_200)]
    [InlineData(65_536, 81_920, 180_224)]
    public void Create_BoundsDecodedAndWireBytesNearTheApiOutputLimit(
        int apiOutputBytes,
        int expectedDecodedBytes,
        int expectedWireBytes)
    {
        var options = CreateAgentOptions(apiOutputBytes);

        var result = AgentTelnetClientOptionsFactory.Create(options);

        Assert.Equal(expectedDecodedBytes, result.MaximumOutputBytes);
        Assert.Equal(expectedWireBytes, result.MaximumWireBytes);
        Assert.Equal(TimeSpan.FromSeconds(240), result.Timeouts.Session);
        Assert.Equal(1, result.SessionCloseRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(2), result.SessionCloseRetryDelay);
    }

    [Fact]
    public void Create_MaximumRequestAndConcurrencyHaveABoundedWireEnvelope()
    {
        var options = CreateAgentOptions(65_536);
        options.MaxCommandsPerRequest = AgentOptions.MaximumCommandsPerRequestLimit;
        options.MaxConcurrentExecutions = AgentOptions.MaximumConcurrentExecutionLimit;

        var result = AgentTelnetClientOptionsFactory.Create(options);
        var maximumConcurrentWireBytes = checked(
            (long)result.MaximumWireBytes *
            options.MaxCommandsPerRequest *
            options.MaxConcurrentExecutions);
        var maximumConcurrentDecodedBytes = checked(
            (long)result.MaximumOutputBytes *
            options.MaxCommandsPerRequest *
            options.MaxConcurrentExecutions);

        Assert.Equal(10_485_760, maximumConcurrentDecodedBytes);
        Assert.True(maximumConcurrentDecodedBytes <= 10L * 1024 * 1024);
        Assert.Equal(23_068_672, maximumConcurrentWireBytes);
        Assert.True(maximumConcurrentWireBytes < 24L * 1024 * 1024);
    }

    [Fact]
    public void FailureMapper_PreservesOutputLimitContract()
    {
        var mapped = TelnetFailureMapper.Map(new SwitchWatchException(
            new DiagnosticError(
                ErrorCodes.OutputLimitExceeded,
                "command",
                "Synthetic bounded output failure.")));

        Assert.Equal(AgentErrorCodes.OutputLimitExceeded, mapped.Code);
        Assert.Equal(502, mapped.StatusCode);
        Assert.Null(mapped.Details);
    }

    private static AgentOptions CreateAgentOptions(int maxOutputBytes) => new()
    {
        MaxOutputBytes = maxOutputBytes,
        Telnet = new TelnetSessionOptions
        {
            MaxSessionSeconds = 240,
            ImmediateSessionCloseRetryCount = 1,
            ImmediateSessionCloseRetryDelaySeconds = 2
        }
    };
}
