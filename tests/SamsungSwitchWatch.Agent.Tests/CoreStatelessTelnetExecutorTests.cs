using System.Net;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Execution;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class CoreStatelessTelnetExecutorTests
{
    private static readonly DeviceProfileRegistry Profiles = new(
    [
        Ies4224GpProfile.Create(),
        Ies4028XpProfile.Create(),
        Ies4226XpProfile.Create()
    ]);

    [Fact]
    public async Task TestPurpose_ExecutesVersionOnceAndReturnsOnlyCanonicalModel()
    {
        var telnet = new StubAdHocTelnetClient(
            "Model Name : ies4028xp\r\nSoftware Version : 1.2.3\r\nSW#");
        var executor = new CoreStatelessTelnetExecutor(
            telnet,
            Profiles,
            new AgentOptions());

        var result = await executor.ExecuteAsync(Request("test", []));

        Assert.Equal(["show version"], telnet.Commands);
        Assert.Equal("IES4028XP", result.DetectedModel);
        Assert.Empty(result.Commands);
        Assert.Equal(1, result.SessionCount);
    }

    [Theory]
    [InlineData("Unknown Samsung switch", AgentErrorCodes.ModelNotDetected)]
    [InlineData(
        "IES4224GP active, IES4226XP backup",
        AgentErrorCodes.ModelAmbiguous)]
    public async Task TestPurpose_FailsClosedWhenModelIsNotUnique(
        string output,
        string expectedCode)
    {
        var executor = new CoreStatelessTelnetExecutor(
            new StubAdHocTelnetClient(output),
            Profiles,
            new AgentOptions());

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            executor.ExecuteAsync(Request("test", [])));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(422, exception.StatusCode);
        Assert.DoesNotContain(output, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualPurpose_PreservesRequestedCommandAndDoesNotAddModelMetadata()
    {
        var telnet = new StubAdHocTelnetClient("Port 1 Up\r\nSW#");
        var executor = new CoreStatelessTelnetExecutor(
            telnet,
            Profiles,
            new AgentOptions());

        var result = await executor.ExecuteAsync(
            Request("manual", ["show port status"]));

        Assert.Equal(["show port status"], telnet.Commands);
        Assert.Null(result.DetectedModel);
        Assert.Equal("show port status", Assert.Single(result.Commands).Command);
    }

    private static StatelessTelnetRequest Request(
        string purpose,
        IReadOnlyList<string> commands) => new(
        "request-1",
        IPAddress.Parse("192.168.20.10"),
        "IES4224GP",
        new TelnetCredentials("operator", "password", "enable-password"),
        commands,
        purpose);

    private sealed class StubAdHocTelnetClient(string output) : IAdHocTelnetClient
    {
        public IReadOnlyList<string> Commands { get; private set; } = [];

        public Task<TelnetInteractiveResult> ExecuteAsync(
            TelnetEndpoint endpoint,
            TelnetCredentials credentials,
            TelnetPromptProfile promptProfile,
            IReadOnlyList<string> commands,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands = commands.ToArray();
            var now = DateTimeOffset.UtcNow;
            var outputs = commands.Select((command, index) => new CommandOutput(
                $"adhoc-{index}",
                command,
                output,
                output,
                now)).ToArray();
            return Task.FromResult(new TelnetInteractiveResult(
                outputs,
                TelnetPrivilege.Privileged,
                '#',
                now,
                now));
        }
    }
}
