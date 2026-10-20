using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Mesh.Orchestrator.Services;

using Mesh.Abstractions;
using Mesh.Abstractions.Protocol;

/// <summary>A request currently being served, with its streaming channel.</summary>
public sealed class InFlightRequest
{
    public required InferenceRequest Request { get; init; }
    public required ModelDescriptor Model { get; init; }
    public required PlacementDecision Decision { get; init; }
    public Channel<string> Stream { get; } = Channel.CreateUnbounded<string>();
    public StringBuilder Text { get; } = new();
    public TaskCompletionSource<StageResult> Stage { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? Error { get; set; }
    public int Completed;

    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record StageResult(byte[]? Activation, string? Error);

/// <summary>
/// Single source of truth for the orchestrator: the node registry, live agent
/// connections, in-flight requests, and KV prefix affinity.
/// </summary>
public sealed class ClusterState
{
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InFlightRequest> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _prefixAffinity = new(StringComparer.Ordinal);
    private long _totalRequests;

    /// <summary>Fired when node/request state changes; Blazor components subscribe.</summary>
    public event Action? Changed;

    // ----- node registry -----

    public IReadOnlyList<NodeInfo> Nodes =>
        _nodes.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public NodeInfo? GetNode(string id) =>
        _nodes.TryGetValue(id, out var n) ? n : null;

    public void UpsertNode(NodeInfo node)
    {
        _nodes[node.Id] = node;
        Notify();
    }

    public void MarkStale(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        foreach (var (id, node) in _nodes)
        {
            if (node.LastHeartbeatUtc < cutoff && node.Health != NodeHealth.Offline)
            {
                _nodes[id] = node with { Health = NodeHealth.Offline };
            }
        }
        Notify();
    }

    // ----- connections -----

    public void AddConnection(AgentConnection connection) =>
        _connections[connection.NodeId] = connection;

    public void RemoveConnection(string nodeId)
    {
        _connections.TryRemove(nodeId, out _);
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            _nodes[nodeId] = node with { Health = NodeHealth.Offline };
        }
        Notify();
    }

    public AgentConnection? GetConnection(string nodeId) =>
        _connections.TryGetValue(nodeId, out var c) ? c : null;

    // ----- in-flight requests -----

    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    public InFlightRequest BeginRequest(InferenceRequest request, ModelDescriptor model, PlacementDecision decision)
    {
        Interlocked.Increment(ref _totalRequests);
        var inflight = new InFlightRequest { Request = request, Model = model, Decision = decision };
        _inflight[request.RequestId] = inflight;
        Notify();
        return inflight;
    }

    public InFlightRequest? GetInflight(string requestId) =>
        _inflight.TryGetValue(requestId, out var r) ? r : null;

    public void OnStreamToken(StreamTokenMessage m)
    {
        if (_inflight.TryGetValue(m.RequestId, out var r))
        {
            r.Stream.Writer.TryWrite(m.Delta);
            r.Text.Append(m.Delta);
        }
    }

    public void OnStreamActivation(StreamActivationMessage m)
    {
        if (_inflight.TryGetValue(m.RequestId, out var r))
        {
            r.Stage.TrySetResult(new StageResult(m.Payload, null));
        }
    }

    public void OnComplete(CompleteMessage m)
    {
        if (_inflight.TryGetValue(m.RequestId, out var r))
        {
            r.Completed = 1;
            r.Stream.Writer.TryComplete();
            r.Stage.TrySetResult(new StageResult(null, null));

            if (r.Request.PrefixTag is not null)
            {
                _prefixAffinity[r.Request.PrefixTag] = m.NodeId;
            }

            _inflight.TryRemove(m.RequestId, out _);
            Notify();
        }
    }

    public void OnError(ErrorMessage m)
    {
        if (_inflight.TryGetValue(m.RequestId, out var r))
        {
            r.Error = m.Error;
            r.Stream.Writer.TryWrite($"\n[error: {m.Error}]");
            r.Stream.Writer.TryComplete();
            r.Stage.TrySetResult(new StageResult(null, m.Error));
            _inflight.TryRemove(m.RequestId, out _);
            Notify();
        }
    }

    // ----- prefix affinity -----

    public string? GetAffinity(string prefixTag) =>
        _prefixAffinity.TryGetValue(prefixTag, out var id) ? id : null;

    private void Notify() => Changed?.Invoke();
}
