using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Monitoring;

namespace SamsungSwitchWatch.Viewer.Services;

internal sealed record ManualQueryExecutionLease(
    CancellationTokenSource Cancellation,
    long ContextGeneration,
    TaskCompletionSource Completion);

internal sealed record ManualQueryValidationResult(
    bool IsAllowed,
    string? NormalizedCommand,
    bool IsSensitive,
    string? ErrorCode,
    string Message);

internal sealed class ManualQueryService : IAsyncDisposable
{
    internal const int MaximumHistoryCount = 20;

    private readonly object _cancellationSync = new();
    private readonly DeviceOperationGateRegistry _operationGates;
    private readonly List<string> _history = [];
    private ManualQueryExecutionLease? _activeLease;
    private Task? _disposeTask;
    private int _historyIndex;
    private string _historyDraft = string.Empty;
    private int _manualOperationActive;
    private long _contextGeneration;
    private int _disposed;

    public ManualQueryService(DeviceOperationGateRegistry? operationGates = null)
    {
        _operationGates = operationGates ?? new DeviceOperationGateRegistry();
    }

    public bool IsManualOperationActive =>
        Volatile.Read(ref _manualOperationActive) != 0;

    public int HistoryCount => _history.Count;

    public long ContextGeneration => Interlocked.Read(ref _contextGeneration);

    public bool TryBeginManualOperation()
    {
        lock (_cancellationSync)
        {
            return Volatile.Read(ref _disposed) == 0
                   && Interlocked.CompareExchange(
                       ref _manualOperationActive,
                       1,
                       0) == 0;
        }
    }

    public bool EndManualOperation() =>
        Interlocked.Exchange(ref _manualOperationActive, 0) != 0;

    public ManualQueryExecutionLease? TryBeginQuery(CancellationToken lifetimeToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        lock (_cancellationSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                cancellation.Dispose();
                throw new ObjectDisposedException(nameof(ManualQueryService));
            }
            if (_activeLease is not null)
            {
                cancellation.Dispose();
                return null;
            }

            var lease = new ManualQueryExecutionLease(
                cancellation,
                Interlocked.Read(ref _contextGeneration),
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            _activeLease = lease;
            return lease;
        }
    }

    public void ReleaseQuery(ManualQueryExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_cancellationSync)
        {
            if (ReferenceEquals(_activeLease, lease))
            {
                _activeLease = null;
            }
        }
        lease.Completion.TrySetResult();
        lease.Cancellation.Dispose();
    }

    public void CancelActiveQuery()
    {
        lock (_cancellationSync)
        {
            _activeLease?.Cancellation.Cancel();
        }
    }

    public long AdvanceContext() => Interlocked.Increment(ref _contextGeneration);

    public bool IsCurrent(long generation) =>
        generation == Interlocked.Read(ref _contextGeneration);

    public ManualQueryValidationResult Validate(
        string? command,
        int maximumLength,
        bool allowSensitive)
    {
        var validation = ReadOnlyQueryPolicy.Validate(command, maximumLength);
        if (!validation.IsAllowed)
        {
            return new ManualQueryValidationResult(
                false,
                null,
                false,
                "QUERY_COMMAND_BLOCKED",
                "한 줄짜리 show 조회 명령만 입력할 수 있습니다.");
        }

        var sensitive = validation.Sensitivity == ReadOnlyQuerySensitivity.Sensitive;
        if (sensitive && !allowSensitive)
        {
            return new ManualQueryValidationResult(
                false,
                validation.NormalizedCommand,
                true,
                "QUERY_COMMAND_BLOCKED",
                "민감한 설정 조회는 Agent 연결 설정에서 명시적으로 허용해야 합니다.");
        }

        return new ManualQueryValidationResult(
            true,
            validation.NormalizedCommand,
            sensitive,
            null,
            string.Empty);
    }

    public bool IsSensitive(string? command, int maximumLength)
    {
        var validation = ReadOnlyQueryPolicy.Validate(command, maximumLength);
        return validation.IsAllowed
               && validation.Sensitivity == ReadOnlyQuerySensitivity.Sensitive;
    }

    public async Task<ReadOnlyQueryResultDto> ExecuteAsync(
        ManualQueryExecutionLease lease,
        IAgentClient client,
        string deviceId,
        string command,
        bool useStatelessApi,
        bool allowSensitive,
        ManagedDeviceProfile? profile = null,
        ManagedDeviceSecrets? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var validation = Validate(
            command,
            ReadOnlyQueryPolicy.MaximumCommandLength,
            allowSensitive);
        if (!validation.IsAllowed)
        {
            throw new AgentClientException(
                validation.ErrorCode ?? "QUERY_COMMAND_BLOCKED",
                AgentConnectionState.Stale);
        }

        lock (_cancellationSync)
        {
            if (!ReferenceEquals(_activeLease, lease))
            {
                throw new InvalidOperationException("MANUAL_QUERY_LEASE_STALE");
            }
        }

        if (!useStatelessApi)
        {
            return await client.ExecuteReadOnlyQueryAsync(
                    deviceId,
                    validation.NormalizedCommand!,
                    lease.Cancellation.Token)
                .ConfigureAwait(false);
        }

        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(secrets);
        var request = new TelnetExecuteRequestDto(
            Guid.NewGuid().ToString("N"),
            profile.Host,
            23,
            profile.Model,
            secrets.Username,
            secrets.Password,
            secrets.EnablePassword,
            "manual",
            [validation.NormalizedCommand!],
            allowSensitive);
        using (await _operationGates.AcquireAsync(
                   profile.Host,
                   lease.Cancellation.Token).ConfigureAwait(false))
        {
            var execution = await client.ExecuteTelnetAsync(
                    request,
                    lease.Cancellation.Token)
                .ConfigureAwait(false);
            var output = execution.Commands.FirstOrDefault()
                         ?? new TelnetCommandOutputDto(
                             validation.NormalizedCommand!,
                             string.Empty,
                             false,
                             execution.CompletedUtc);
            return new ReadOnlyQueryResultDto(
                4,
                deviceId,
                output.Command,
                execution.StartedUtc,
                execution.CompletedUtc,
                execution.DurationMs,
                output.Output,
                output.Truncated,
                execution.SessionCount,
                execution.ReconnectCount);
        }
    }

    public void UpdateDraft(string command)
    {
        _historyIndex = _history.Count;
        _historyDraft = command ?? string.Empty;
    }

    public void AddHistory(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (_history.Count == 0
            || !_history[^1].Equals(command, StringComparison.Ordinal))
        {
            _history.Add(command);
            if (_history.Count > MaximumHistoryCount)
            {
                _history.RemoveAt(0);
            }
        }
        _historyIndex = _history.Count;
        _historyDraft = string.Empty;
    }

    public bool TryMoveHistory(
        string currentCommand,
        int direction,
        bool queryRunning,
        out string command)
    {
        command = currentCommand;
        if (queryRunning || _history.Count == 0 || direction == 0)
        {
            return false;
        }
        if (_historyIndex >= _history.Count)
        {
            _historyDraft = currentCommand;
        }

        var next = Math.Clamp(
            _historyIndex + Math.Sign(direction),
            0,
            _history.Count);
        if (next == _historyIndex)
        {
            return false;
        }

        _historyIndex = next;
        command = next == _history.Count
            ? _historyDraft
            : _history[next];
        return true;
    }

    public ValueTask DisposeAsync()
    {
        lock (_cancellationSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        ManualQueryExecutionLease? activeLease;
        lock (_cancellationSync)
        {
            activeLease = _activeLease;
            activeLease?.Cancellation.Cancel();
        }
        Interlocked.Exchange(ref _manualOperationActive, 0);
        if (activeLease is not null)
        {
            await activeLease.Completion.Task.ConfigureAwait(false);
        }
    }
}
