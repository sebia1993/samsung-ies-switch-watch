using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Connections;

internal readonly record struct AgentClientGeneration(
    IAgentClient Client,
    long Generation);

internal sealed class AgentConnectionCoordinator
{
    private readonly object _sync = new();
    private IAgentClient _client;
    private long _generation;
    private AgentConnectionState _httpState;
    private AgentConnectionState _realtimeState;

    public AgentConnectionCoordinator(
        IAgentClient initialClient,
        AgentConnectionState initialState = AgentConnectionState.Connecting)
    {
        _client = initialClient ?? throw new ArgumentNullException(nameof(initialClient));
        _httpState = initialState;
        _realtimeState = initialState;
    }

    public IAgentClient CurrentClient
    {
        get
        {
            lock (_sync) return _client;
        }
    }

    public long Generation
    {
        get
        {
            lock (_sync) return _generation;
        }
    }

    public AgentConnectionState HttpState
    {
        get
        {
            lock (_sync) return _httpState;
        }
    }

    public AgentConnectionState RealtimeState
    {
        get
        {
            lock (_sync) return _realtimeState;
        }
    }

    public AgentClientGeneration Capture()
    {
        lock (_sync) return new AgentClientGeneration(_client, _generation);
    }

    public IAgentClient Replace(
        IAgentClient replacement,
        Action? resetDependentState = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_sync)
        {
            var previous = _client;
            if (!ReferenceEquals(previous, replacement))
            {
                _client = replacement;
                _generation++;
                resetDependentState?.Invoke();
            }
            return previous;
        }
    }

    public bool TryRunIfCurrent(IAgentClient client, Action action)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            if (!ReferenceEquals(client, _client))
            {
                return false;
            }
            action();
            return true;
        }
    }

    public bool IsCurrent(IAgentClient client) =>
        IsCurrent(new AgentClientGeneration(client, Generation));

    public bool IsCurrent(AgentClientGeneration captured)
    {
        lock (_sync)
        {
            return ReferenceEquals(captured.Client, _client)
                   && captured.Generation == _generation;
        }
    }

    public bool SetHttpState(AgentConnectionState state)
    {
        lock (_sync)
        {
            if (_httpState == state) return false;
            _httpState = state;
            return true;
        }
    }

    public bool SetRealtimeState(AgentConnectionState state)
    {
        lock (_sync)
        {
            if (_realtimeState == state) return false;
            _realtimeState = state;
            return true;
        }
    }

    public AgentConnectionState GetCombinedState(bool initialized, bool hasSnapshot)
    {
        lock (_sync)
        {
            if (_httpState == AgentConnectionState.NeedsConnection
                || _realtimeState == AgentConnectionState.NeedsConnection)
            {
                return AgentConnectionState.NeedsConnection;
            }
            if (_httpState == AgentConnectionState.Demo
                || _realtimeState == AgentConnectionState.Demo)
            {
                return AgentConnectionState.Demo;
            }
            if (_httpState == AgentConnectionState.Offline)
            {
                return _realtimeState == AgentConnectionState.Connected && hasSnapshot
                    ? AgentConnectionState.Stale
                    : AgentConnectionState.Offline;
            }
            if (_httpState == AgentConnectionState.Stale
                || _realtimeState == AgentConnectionState.Offline)
            {
                return AgentConnectionState.Stale;
            }
            if (_realtimeState == AgentConnectionState.Reconnecting)
            {
                return AgentConnectionState.Reconnecting;
            }
            if (_realtimeState == AgentConnectionState.Connecting)
            {
                return initialized
                    ? AgentConnectionState.Reconnecting
                    : AgentConnectionState.Connecting;
            }
            if (_httpState == AgentConnectionState.Connecting)
            {
                return AgentConnectionState.Connecting;
            }
            return AgentConnectionState.Connected;
        }
    }
}
