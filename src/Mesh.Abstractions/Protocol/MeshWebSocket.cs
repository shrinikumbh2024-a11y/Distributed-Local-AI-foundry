using System.Net.WebSockets;
using System.Text;

namespace Mesh.Abstractions.Protocol;

/// <summary>
/// Minimal WebSocket client for the agent side of the mesh protocol. Sends and
/// receives JSON envelopes as text frames. No external dependencies — uses the
/// BCL <see cref="ClientWebSocket"/>.
/// </summary>
public sealed class MeshWebSocket : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();

    public WebSocketState State => _ws.State;

    public async Task ConnectAsync(Uri uri, CancellationToken ct = default)
    {
        await _ws.ConnectAsync(uri, ct);
    }

    public async Task SendAsync(MeshEnvelope envelope, CancellationToken ct = default)
    {
        var json = MeshProtocol.SerializeEnvelope(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    /// <summary>Receives exactly one complete text frame and decodes it.</summary>
    public async Task<MeshEnvelope?> ReceiveAsync(CancellationToken ct = default)
    {
        var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer.Array!, buffer.Offset, result.Count);
        }
        while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(ms.ToArray());
        return MeshProtocol.DeserializeEnvelope(json);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            _ws.Dispose();
        }
        catch
        {
            // Best-effort cleanup on shutdown.
        }
    }
}
