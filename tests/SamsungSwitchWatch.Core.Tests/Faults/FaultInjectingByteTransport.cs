using System.Net.Sockets;
using System.Text;
using SamsungSwitchWatch.Core.Transport;

namespace SamsungSwitchWatch.Core.Tests.Faults;

internal sealed class FaultInjectingByteTransport : IByteTransport
{
    private static readonly byte[] InvalidNegotiation =
        [255, 250, 1, .. Enumerable.Repeat((byte)0, 256)];
    private static readonly byte[] PagingMarker = "--More--"u8.ToArray();

    private readonly FaultScenario _scenario;
    private readonly Queue<byte[]> _reads = [];
    private byte[]? _currentRead;
    private int _currentOffset;
    private int _totalReadBytes;
    private int _generatedOutputBytes;
    private int _commandWriteCount;
    private int _commandReadCount;
    private int _disposed;

    public FaultInjectingByteTransport(FaultScenario scenario)
    {
        _scenario = (scenario ?? throw new ArgumentNullException(nameof(scenario))).Validate();
        foreach (var read in _scenario.Reads)
        {
            _reads.Enqueue(read.ToArray());
        }
    }

    public bool IsConnected { get; private set; }

    public bool ConnectWasCalled { get; private set; }

    public bool WasClosed { get; private set; }

    public List<byte[]> Writes { get; } = [];

    public async ValueTask ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ConnectWasCalled = true;
        if (_scenario.Kind == FaultScenarioKind.SlowConnect)
        {
            await Task.Delay(_scenario.Delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
    }

    public async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_scenario.Kind == FaultScenarioKind.SlowRead)
        {
            await Task.Delay(_scenario.Delay, cancellationToken).ConfigureAwait(false);
        }

        if (_commandWriteCount > 0)
        {
            switch (_scenario.Kind)
            {
                case FaultScenarioKind.ReadTimeout:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .ConfigureAwait(false);
                    return 0;
                case FaultScenarioKind.ConnectionReset:
                    throw new IOException(
                        "Synthetic connection reset.",
                        new SocketException((int)SocketError.ConnectionReset));
                case FaultScenarioKind.DisconnectAfterCommand
                    when _commandWriteCount >= _scenario.DisconnectAfterCommandNumber:
                case FaultScenarioKind.ImmediateCloseAfterLogin:
                    IsConnected = false;
                    return 0;
                case FaultScenarioKind.ImmediateCloseDuringCommand
                    when _commandReadCount > 0:
                    IsConnected = false;
                    return 0;
                case FaultScenarioKind.HugeOutput:
                    return CopyGeneratedOutput(buffer, stopAtConfiguredSize: true);
                case FaultScenarioKind.NeverEndingOutput:
                    if (_scenario.Delay > TimeSpan.Zero)
                    {
                        await Task.Delay(_scenario.Delay, cancellationToken).ConfigureAwait(false);
                    }
                    return CopyGeneratedOutput(buffer, stopAtConfiguredSize: false);
                case FaultScenarioKind.PagingLoop:
                    return CopyToBuffer(PagingMarker, buffer, PagingMarker.Length);
            }
        }

        if (_scenario.Kind == FaultScenarioKind.InvalidTelnetNegotiation)
        {
            return CopyToBuffer(InvalidNegotiation, buffer, InvalidNegotiation.Length);
        }

        var read = CopyScriptedRead(buffer);
        if (_commandWriteCount > 0 && read > 0)
        {
            _commandReadCount++;
        }
        return read;
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();
        var copy = buffer.ToArray();
        var line = Encoding.Latin1.GetString(copy).Trim();
        var isCommand = line.StartsWith("show ", StringComparison.OrdinalIgnoreCase);

        if (_scenario.Kind == FaultScenarioKind.WriteTimeout && isCommand)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (_scenario.Kind == FaultScenarioKind.SlowWrite)
        {
            await Task.Delay(_scenario.Delay, cancellationToken).ConfigureAwait(false);
        }

        Writes.Add(copy);
        if (isCommand)
        {
            _commandWriteCount++;
            _commandReadCount = 0;
        }
    }

    public ValueTask CloseAsync()
    {
        IsConnected = false;
        WasClosed = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private int CopyScriptedRead(Memory<byte> buffer)
    {
        if (_scenario.Kind == FaultScenarioKind.DisconnectAfterBytes
            && _totalReadBytes >= _scenario.DisconnectAfterBytes)
        {
            IsConnected = false;
            return 0;
        }

        if (_currentRead is null || _currentOffset >= _currentRead.Length)
        {
            if (_reads.Count == 0)
            {
                IsConnected = false;
                return 0;
            }
            _currentRead = _reads.Dequeue();
            _currentOffset = 0;
        }

        var maximum = _scenario.Kind switch
        {
            FaultScenarioKind.PartialRead => _scenario.PartialReadBytes,
            FaultScenarioKind.PromptSplitAcrossPackets => 1,
            FaultScenarioKind.ImmediateCloseDuringCommand => _scenario.PartialReadBytes,
            _ => int.MaxValue
        };
        if (_scenario.Kind == FaultScenarioKind.DisconnectAfterBytes)
        {
            maximum = Math.Min(
                maximum,
                _scenario.DisconnectAfterBytes - _totalReadBytes);
        }

        var count = Math.Min(
            Math.Min(buffer.Length, _currentRead.Length - _currentOffset),
            maximum);
        if (count <= 0)
        {
            IsConnected = false;
            return 0;
        }

        _currentRead.AsMemory(_currentOffset, count).CopyTo(buffer);
        _currentOffset += count;
        _totalReadBytes += count;
        return count;
    }

    private int CopyGeneratedOutput(Memory<byte> buffer, bool stopAtConfiguredSize)
    {
        if (stopAtConfiguredSize
            && _generatedOutputBytes >= _scenario.HugeOutputBytes)
        {
            IsConnected = false;
            return 0;
        }

        var remaining = stopAtConfiguredSize
            ? _scenario.HugeOutputBytes - _generatedOutputBytes
            : int.MaxValue;
        var count = CopyToBuffer(
            _scenario.RepeatingOutput,
            buffer,
            Math.Min(remaining, _scenario.RepeatingOutput.Length));
        _generatedOutputBytes += count;
        _commandReadCount++;
        return count;
    }

    private static int CopyToBuffer(
        ReadOnlySpan<byte> source,
        Memory<byte> destination,
        int maximum)
    {
        var count = Math.Min(Math.Min(source.Length, destination.Length), maximum);
        source[..count].CopyTo(destination.Span);
        return count;
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected)
        {
            throw new InvalidOperationException("The synthetic transport is not connected.");
        }
    }
}

internal sealed class FaultInjectingByteTransportFactory(
    params FaultScenario[] scenarios) : IByteTransportFactory
{
    private readonly Queue<FaultScenario> _scenarios = new(scenarios);

    public int CreateCalls { get; private set; }

    public IReadOnlyList<FaultInjectingByteTransport> Created => _created;

    private readonly List<FaultInjectingByteTransport> _created = [];

    public IByteTransport Create()
    {
        CreateCalls++;
        if (!_scenarios.TryDequeue(out var scenario))
        {
            throw new InvalidOperationException("No deterministic fault scenario remains.");
        }

        var transport = new FaultInjectingByteTransport(scenario);
        _created.Add(transport);
        return transport;
    }
}
