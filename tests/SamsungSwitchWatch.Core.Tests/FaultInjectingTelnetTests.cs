using System.Text;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;
using SamsungSwitchWatch.Core.Tests.Faults;

namespace SamsungSwitchWatch.Core.Tests;

public sealed class FaultInjectingTelnetTests
{
    [Fact]
    public void ScenarioCatalog_ContainsEveryRequiredDeterministicFault()
    {
        Assert.Equal(
        [
            FaultScenarioKind.Normal,
            FaultScenarioKind.SlowConnect,
            FaultScenarioKind.SlowRead,
            FaultScenarioKind.SlowWrite,
            FaultScenarioKind.ReadTimeout,
            FaultScenarioKind.WriteTimeout,
            FaultScenarioKind.ConnectionReset,
            FaultScenarioKind.DisconnectAfterBytes,
            FaultScenarioKind.DisconnectAfterCommand,
            FaultScenarioKind.PartialRead,
            FaultScenarioKind.PromptSplitAcrossPackets,
            FaultScenarioKind.HugeOutput,
            FaultScenarioKind.NeverEndingOutput,
            FaultScenarioKind.InvalidTelnetNegotiation,
            FaultScenarioKind.PagingLoop,
            FaultScenarioKind.ImmediateCloseAfterLogin,
            FaultScenarioKind.ImmediateCloseDuringCommand
        ], Enum.GetValues<FaultScenarioKind>());
    }

    [Theory]
    [InlineData(FaultScenarioKind.Normal)]
    [InlineData(FaultScenarioKind.PartialRead)]
    [InlineData(FaultScenarioKind.PromptSplitAcrossPackets)]
    [InlineData(FaultScenarioKind.SlowRead)]
    [InlineData(FaultScenarioKind.SlowWrite)]
    public async Task FragmentationAndBoundedDelay_CompleteNormally(
        FaultScenarioKind kind)
    {
        var scenario = new FaultScenario
        {
            Kind = kind,
            Reads = SuccessfulReads("show system"),
            Delay = TimeSpan.FromMilliseconds(1),
            PartialReadBytes = 3
        };
        var factory = new FaultInjectingByteTransportFactory(scenario);
        var client = CreateClient(factory);

        var result = await client.ExecuteRegisteredAsync(
            Endpoint(),
            Credentials(),
            Profile((CommandIds.System, "show system")),
            [CommandIds.System]);

        Assert.Single(result.Outputs);
        Assert.Equal(1, result.SessionCount);
        Assert.Equal(0, result.ReconnectCount);
        Assert.True(Assert.Single(factory.Created).WasClosed);
    }

    [Fact]
    public async Task SlowConnect_MapsToTcpTimeoutAndDoesNotRetry()
    {
        var factory = new FaultInjectingByteTransportFactory(
            new FaultScenario
            {
                Kind = FaultScenarioKind.SlowConnect,
                Delay = TimeSpan.FromMilliseconds(200)
            },
            Normal("show system"));
        var client = CreateClient(factory, connectTimeout: TimeSpan.FromMilliseconds(20), retryCount: 1);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                Profile((CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.TcpTimeout, failure.Error.Code);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Theory]
    [InlineData(FaultScenarioKind.ReadTimeout, "command-idle")]
    [InlineData(FaultScenarioKind.WriteTimeout, "command-write")]
    public async Task CommandTimeouts_DoNotRetry(
        FaultScenarioKind kind,
        string expectedStage)
    {
        var factory = new FaultInjectingByteTransportFactory(
            new FaultScenario
            {
                Kind = kind,
                Reads = AuthenticationReads()
            },
            Normal("show system"));
        var client = CreateClient(factory, retryCount: 1);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                Profile((CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.CommandTimeout, failure.Error.Code);
        Assert.Equal(expectedStage, failure.Error.Stage);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotRetry()
    {
        var failedAuthentication = new FaultScenario
        {
            Reads =
            [
                Bytes("Login:"),
                Bytes("Password:"),
                Bytes("Authentication failed\r\nLogin:")
            ]
        };
        var factory = new FaultInjectingByteTransportFactory(
            failedAuthentication,
            Normal("show system"));
        var client = CreateClient(factory, retryCount: 1);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                Profile((CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.AuthFailed, failure.Error.Code);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public async Task EnableFailure_DoesNotRetry()
    {
        var failedEnable = new FaultScenario
        {
            Reads =
            [
                Bytes("Login:"),
                Bytes("Password:"),
                Bytes("ACCESS-SW-01>"),
                Bytes("Password:"),
                Bytes("Authentication failed\r\nACCESS-SW-01>")
            ]
        };
        var factory = new FaultInjectingByteTransportFactory(
            failedEnable,
            Normal("show system"));
        var client = CreateClient(factory, retryCount: 1);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteAsync(
                Endpoint(),
                new TelnetCredentials("operator", "login-secret", "enable-secret"),
                Ies4224GpProfile.Create().Telnet,
                ["show system"]));

        Assert.Equal(ErrorCodes.EnableFailed, failure.Error.Code);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public async Task DisconnectOnCommandB_ReconnectsOnceAndRunsOnlyBAndC()
    {
        var first = new FaultScenario
        {
            Kind = FaultScenarioKind.DisconnectAfterCommand,
            DisconnectAfterCommandNumber = 2,
            Reads =
            [
                .. AuthenticationReads(),
                Bytes("show version\r\nModel IES4224GP\r\nACCESS-SW-01#")
            ]
        };
        var second = new FaultScenario
        {
            Reads =
            [
                .. AuthenticationReads(),
                Bytes("show system\r\nUptime 1 day\r\nACCESS-SW-01#"),
                Bytes("show port status\r\n1 Up\r\nACCESS-SW-01#")
            ]
        };
        var factory = new FaultInjectingByteTransportFactory(first, second);
        var client = CreateClient(factory, retryCount: 1);
        var profile = Profile(
            (CommandIds.Version, "show version"),
            (CommandIds.System, "show system"),
            (CommandIds.InterfaceStatus, "show port status"));

        var result = await client.ExecuteRegisteredAsync(
            Endpoint(),
            Credentials(),
            profile,
            [CommandIds.Version, CommandIds.System, CommandIds.InterfaceStatus]);

        Assert.Equal(
            [CommandIds.Version, CommandIds.System, CommandIds.InterfaceStatus],
            result.Outputs.Select(output => output.CommandId));
        Assert.Equal(2, result.SessionCount);
        Assert.Equal(1, result.ReconnectCount);
        AssertCommands(factory.Created[0], "show version", "show system");
        AssertCommands(factory.Created[1], "show system", "show port status");
        Assert.DoesNotContain(factory.Created[1].Writes, write =>
            Encoding.Latin1.GetString(write).Contains("show version", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FaultScenarioKind.ConnectionReset)]
    [InlineData(FaultScenarioKind.ImmediateCloseAfterLogin)]
    [InlineData(FaultScenarioKind.DisconnectAfterCommand)]
    public async Task CommandSessionClose_RetriesAtMostOnce(FaultScenarioKind kind)
    {
        FaultScenario Closing() => new()
        {
            Kind = kind,
            Reads = AuthenticationReads(),
            DisconnectAfterCommandNumber = 1
        };
        var factory = new FaultInjectingByteTransportFactory(Closing(), Closing());
        var client = CreateClient(factory, retryCount: 1);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                Profile((CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.TelnetSessionClosed, failure.Error.Code);
        Assert.Equal("command", failure.Error.Stage);
        Assert.Equal(2, factory.CreateCalls);
    }

    [Fact]
    public async Task ImmediateCloseDuringCommand_IsClassifiedAsSessionClose()
    {
        var factory = new FaultInjectingByteTransportFactory(new FaultScenario
        {
            Kind = FaultScenarioKind.ImmediateCloseDuringCommand,
            Reads =
            [
                .. AuthenticationReads(),
                Bytes("partial command output without prompt")
            ],
            PartialReadBytes = 7
        });
        var client = CreateClient(factory);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                Profile((CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.TelnetSessionClosed, failure.Error.Code);
        Assert.Equal("command", failure.Error.Stage);
    }

    [Fact]
    public async Task DisconnectAfterBytes_StopsAtExactConfiguredBoundary()
    {
        var transport = new FaultInjectingByteTransport(new FaultScenario
        {
            Kind = FaultScenarioKind.DisconnectAfterBytes,
            Reads = [Bytes("abcdef")],
            DisconnectAfterBytes = 3
        });
        await transport.ConnectAsync("192.0.2.10", 23, CancellationToken.None);
        var buffer = new byte[16];

        var first = await transport.ReadAsync(buffer, CancellationToken.None);
        var second = await transport.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(3, first);
        Assert.Equal("abc", Encoding.ASCII.GetString(buffer, 0, first));
        Assert.Equal(0, second);
        await transport.DisposeAsync();
    }

    [Theory]
    [InlineData(1_024, 8_192)]
    [InlineData(2_097_152, 1_024)]
    public async Task HugeOutput_EnforcesDecodedAndWireByteLimits(
        int maximumOutputBytes,
        int maximumWireBytes)
    {
        var factory = new FaultInjectingByteTransportFactory(new FaultScenario
        {
            Kind = FaultScenarioKind.HugeOutput,
            Reads = AuthenticationReads(),
            HugeOutputBytes = 16 * 1024,
            RepeatingOutput = Enumerable.Repeat((byte)'x', 256).ToArray()
        });
        var client = CreateClient(
            factory,
            maximumOutputBytes: maximumOutputBytes,
            maximumWireBytes: maximumWireBytes);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                Profile((CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.OutputLimitExceeded, failure.Error.Code);
        Assert.True(Assert.Single(factory.Created).WasClosed);
    }

    [Fact]
    public async Task NeverEndingOutput_StopsAtCommandHardTimeout()
    {
        var factory = new FaultInjectingByteTransportFactory(new FaultScenario
        {
            Kind = FaultScenarioKind.NeverEndingOutput,
            Reads = AuthenticationReads(),
            Delay = TimeSpan.FromMilliseconds(2),
            RepeatingOutput = "still-running\r\n"u8.ToArray()
        });
        var client = CreateClient(
            factory,
            commandHardTimeout: TimeSpan.FromMilliseconds(40));

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                ProfileWithTimeout(TimeSpan.FromMilliseconds(20),
                    (CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.CommandTimeout, failure.Error.Code);
        Assert.Equal("command-hard-limit", failure.Error.Stage);
    }

    [Fact]
    public async Task InvalidNegotiation_IsBoundedAndFailsClosed()
    {
        var factory = new FaultInjectingByteTransportFactory(new FaultScenario
        {
            Kind = FaultScenarioKind.InvalidTelnetNegotiation
        });
        var client = CreateClient(factory, maximumNegotiationBytes: 16);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                Profile((CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.TelnetNegotiationFailed, failure.Error.Code);
        Assert.Equal("telnet-negotiation", failure.Error.Stage);
    }

    [Fact]
    public async Task PagingLoop_StopsAtConfiguredAdvanceLimit()
    {
        var factory = new FaultInjectingByteTransportFactory(new FaultScenario
        {
            Kind = FaultScenarioKind.PagingLoop,
            Reads = AuthenticationReads()
        });
        var client = CreateClient(factory, maximumPagingAdvances: 2);

        var failure = await Assert.ThrowsAsync<SwitchWatchException>(() =>
            client.ExecuteRegisteredAsync(
                Endpoint(),
                Credentials(),
                Profile((CommandIds.System, "show system")),
                [CommandIds.System]));

        Assert.Equal(ErrorCodes.CommandTimeout, failure.Error.Code);
        Assert.Equal("pager-limit", failure.Error.Stage);
        Assert.Equal(2, Assert.Single(factory.Created).Writes.Count(write =>
            write.SequenceEqual(new byte[] { 0x20 })));
    }

    private static TelnetClient CreateClient(
        FaultInjectingByteTransportFactory factory,
        TimeSpan? connectTimeout = null,
        int retryCount = 0,
        int maximumOutputBytes = 2 * 1024 * 1024,
        int maximumWireBytes = 2 * 1024 * 1024,
        int maximumNegotiationBytes = 16 * 1024,
        TimeSpan? commandHardTimeout = null,
        int maximumPagingAdvances = 32)
    {
        var timeouts = new TelnetTimeouts(
            connectTimeout ?? TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(20))
        {
            Write = TimeSpan.FromMilliseconds(50),
            Session = TimeSpan.FromSeconds(2)
        };
        return new TelnetClient(
            factory,
            new TelnetClientOptions(
                timeouts,
                maximumOutputBytes,
                256,
                maximumNegotiationBytes,
                maximumWireBytes)
            {
                CommandHardTimeout = commandHardTimeout ?? TimeSpan.FromMilliseconds(250),
                MaximumPagingAdvances = maximumPagingAdvances,
                SessionCloseRetryCount = retryCount,
                SessionCloseRetryDelay = TimeSpan.FromMilliseconds(1),
                SessionSafetyMargin = TimeSpan.FromMilliseconds(10)
            });
    }

    private static DeviceCommandProfile Profile(
        params (string Id, string Command)[] commands) =>
        ProfileWithTimeout(TimeSpan.FromMilliseconds(100), commands);

    private static DeviceCommandProfile ProfileWithTimeout(
        TimeSpan timeout,
        params (string Id, string Command)[] commands)
    {
        var baseProfile = Ies4224GpProfile.Create();
        return new DeviceCommandProfile(
            baseProfile.Model,
            baseProfile.Telnet,
            commands.Select(command => new ReadOnlyCommandDefinition(
                command.Id,
                command.Id,
                command.Command,
                timeout,
                60)).ToArray());
    }

    private static FaultScenario Normal(params string[] commands) => new()
    {
        Kind = FaultScenarioKind.Normal,
        Reads = SuccessfulReads(commands)
    };

    private static byte[][] SuccessfulReads(params string[] commands) =>
    [
        .. AuthenticationReads(),
        .. commands.Select(command =>
            Bytes($"{command}\r\nsynthetic output\r\nACCESS-SW-01#"))
    ];

    private static byte[][] AuthenticationReads() =>
    [
        Bytes("Login:"),
        Bytes("Password:"),
        Bytes("ACCESS-SW-01#")
    ];

    private static TelnetEndpoint Endpoint() => new("192.0.2.10");

    private static TelnetCredentials Credentials() =>
        new("operator", "login-secret");

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);

    private static void AssertCommands(
        FaultInjectingByteTransport transport,
        params string[] commands)
    {
        var writes = transport.Writes
            .Select(Encoding.Latin1.GetString)
            .Select(value => value.Trim())
            .Where(value => value.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(commands, writes);
    }
}
