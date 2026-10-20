using System.Net.WebSockets;
using System.Text;

namespace Mesh.Orchestrator.Services;

using Mesh.Abstractions;
using Mesh.Abstractions.Protocol;

/// <summary>
/// Server-side half of one agent's WebSocket connection. Routes incoming
/// protocol messages into <see cref="ClusterState"/> and provides a
/// concurrency-safe sender back to the agent.
/// </summary>
public sealed class AgentConnection
{
    private readonly WebSocket _socket;
    private readonly ClusterState _state;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public AgentConnection(WebSocket socket, ClusterState state)
    {
        _socket = socket;
        _state = state;
    }

    public string NodeId { get; private set; } = "pending";

    public async Task SendAsync(MeshEnvelope envelope, CancellationToken ct = default)
    {
        var json = MeshProtocol.SerializeEnvelope(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var env = MeshProtocol.DeserializeEnvelope(Encoding.UTF8.GetString(ms.ToArray()));
                if (env is not null)
                {
                    await HandleAsync(env, ct);
                }
            }
        }
        finally
        {
            _state.RemoveConnection(NodeId);
        }
    }

    private Task HandleAsync(MeshEnvelope env, CancellationToken ct)
    {
        switch (env.Type)
        {
            case MessageTypes.Register:
            {
                var r = MeshProtocol.DeserializePayload<RegisterMessage>(env);
                NodeId = r.Node.Id;
                _state.UpsertNode(r.Node);
                _state.AddConnection(this);
                return SendAsync(MeshProtocol.Wrap(MessageTypes.Welcome, new WelcomeMessage("orchestrator")), ct);
            }

            case MessageTypes.Heartbeat:
            {
                var h = MeshProtocol.DeserializePayload<HeartbeatMessage>(env);
                var existing = _state.GetNode(h.NodeId);
                if (existing is not null)
                {
                    _state.UpsertNode(existing with
                    {
                        Load = h.Load,
                        Models = h.Models,
                        LastHeartbeatUtc = DateTimeOffset.UtcNow,
                        Health = NodeHealth.Healthy,
                    });
                }
                return Task.CompletedTask;
            }

            case MessageTypes.StreamToken:
                _state.OnStreamToken(MeshProtocol.DeserializePayload<StreamTokenMessage>(env));
                return Task.CompletedTask;

            case MessageTypes.StreamActivation:
                _state.OnStreamActivation(MeshProtocol.DeserializePayload<StreamActivationMessage>(env));
                return Task.CompletedTask;

            case MessageTypes.Complete:
                _state.OnComplete(MeshProtocol.DeserializePayload<CompleteMessage>(env));
                return Task.CompletedTask;

            case MessageTypes.Error:
                _state.OnError(MeshProtocol.DeserializePayload<ErrorMessage>(env));
                return Task.CompletedTask;

            default:
                return Task.CompletedTask;
        }
    }
}
