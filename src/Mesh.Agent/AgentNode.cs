using System.Text;

namespace Mesh.Agent;

using Mesh.Abstractions;
using Mesh.Abstractions.Protocol;
using Mesh.Runtime;

/// <summary>
/// A single node of the mesh. Connects to the orchestrator over a raw
/// WebSocket, registers its capability, streams heartbeats, and executes
/// dispatched inference (horizontal, or one stage of a pipeline).
/// </summary>
public sealed class AgentNode : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly InferenceEngine _engine;
    private readonly NodeCapability _capability;
    private readonly List<ModelPlacement> _models = new();
    private MeshWebSocket? _ws;

    public AgentNode(AgentOptions options, InferenceEngine engine)
    {
        _options = options;
        _engine = engine;
        _capability = CapabilityProbe.Probe(
            options.FabricGroup,
            options.ReservedSystemBytes,
            options.Cores,
            (long)(options.RamGib * 1024 * 1024 * 1024));
    }

    public string NodeId => _options.NodeId;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var ws = new MeshWebSocket();
                _ws = ws;
                await ws.ConnectAsync(_options.OrchestratorUri, ct);

                await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.Register, new RegisterMessage(BuildInfo())), ct);

                var heartbeat = HeartbeatLoopAsync(ws, ct);
                await ReceiveLoopAsync(ws, ct);
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{NodeId}] connection error: {ex.Message} — retrying in 3s");
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HeartbeatLoopAsync(MeshWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var msg = new HeartbeatMessage(NodeId, BuildLoad(), BuildModels(), _engine.MeasuredTokensPerSec);
            await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.Heartbeat, msg), ct);
        }
    }

    private async Task ReceiveLoopAsync(MeshWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var env = await ws.ReceiveAsync(ct);
            if (env is null)
            {
                break;
            }

            await HandleAsync(env, ws, ct);
        }
    }

    private async Task HandleAsync(MeshEnvelope env, MeshWebSocket ws, CancellationToken ct)
    {
        switch (env.Type)
        {
            case MessageTypes.Ping:
                await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.Pong,
                    new PongMessage(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), BuildInfo())), ct);
                break;

            case MessageTypes.LoadModel:
            {
                var m = MeshProtocol.DeserializePayload<LoadModelMessage>(env);
                if (m.Model is not null && !_engine.IsLoaded(m.ModelId))
                {
                    await _engine.LoadAsync(m.Model, ct);
                    _models.RemoveAll(x => x.ModelId == m.ModelId);
                    _models.Add(new ModelPlacement(m.ModelId, Loaded: true, Replicas: 1));
                    Console.WriteLine($"[{NodeId}] loaded {m.ModelId}");
                }
                await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.ModelLoaded,
                    new ModelPlacement(m.ModelId, Loaded: true, Replicas: 1)), ct);
                break;
            }

            case MessageTypes.UnloadModel:
            {
                var m = MeshProtocol.DeserializePayload<UnloadModelMessage>(env);
                await _engine.UnloadAsync(m.ModelId, ct);
                _models.RemoveAll(x => x.ModelId == m.ModelId);
                await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.ModelUnloaded,
                    new ModelPlacement(m.ModelId, Loaded: false, Replicas: 0)), ct);
                break;
            }

            case MessageTypes.Dispatch:
            {
                var m = MeshProtocol.DeserializePayload<DispatchMessage>(env);
                _ = Task.Run(() => ExecuteAsync(m, ws, ct), ct);
                break;
            }

            case MessageTypes.Cancel:
            {
                var m = MeshProtocol.DeserializePayload<CancelMessage>(env);
                Console.WriteLine($"[{NodeId}] cancel request {m.RequestId}");
                break;
            }
        }
    }

    private async Task ExecuteAsync(DispatchMessage msg, MeshWebSocket ws, CancellationToken ct)
    {
        try
        {
            if (msg.Plan is null)
            {
                await RunHorizontalAsync(msg, ws, ct);
            }
            else
            {
                await RunPipelineStageAsync(msg, ws, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancel/shutdown.
        }
        catch (Exception ex)
        {
            await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.Error,
                new ErrorMessage(msg.Request.RequestId, NodeId, ex.Message)), ct);
        }
    }

    private async Task RunHorizontalAsync(DispatchMessage msg, MeshWebSocket ws, CancellationToken ct)
    {
        var request = msg.Request;
        if (msg.Model is not null && !_engine.IsLoaded(request.ModelId))
        {
            await _engine.LoadAsync(msg.Model, ct);
            _models.RemoveAll(x => x.ModelId == request.ModelId);
            _models.Add(new ModelPlacement(request.ModelId, Loaded: true, Replicas: 1));
        }

        var started = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        var tokens = 0;

        await foreach (var delta in _engine.StreamAsync(request, ct))
        {
            sb.Append(delta);
            tokens++;
            await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.StreamToken,
                new StreamTokenMessage(request.RequestId, NodeId, delta)), ct);
        }

        var duration = DateTimeOffset.UtcNow - started;
        await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.Complete,
            new CompleteMessage(request.RequestId, NodeId, sb.ToString(),
                new InferenceUsage(started, duration, tokens, tokens / Math.Max(duration.TotalSeconds, 1e-3)))), ct);
    }

    private async Task RunPipelineStageAsync(DispatchMessage msg, MeshWebSocket ws, CancellationToken ct)
    {
        var plan = msg.Plan!;
        var stage = plan.Stages[msg.StageIndex];
        var totalLayers = Math.Max(plan.TotalLayers, 1);
        var layers = Math.Max(stage.LayerEnd - stage.LayerStart + 1, 1);

        // Simulated compute for this slice of the model.
        await Task.Delay(TimeSpan.FromMilliseconds(120 + 320.0 * layers / totalLayers), ct);

        var isLast = msg.StageIndex == plan.Stages.Count - 1;
        if (isLast)
        {
            var words = new[] { "[pipeline", "result", "—", "produced", "across", $"{plan.Stages.Count}", "nodes]" };
            var sb = new StringBuilder();
            var started = DateTimeOffset.UtcNow;
            foreach (var w in words)
            {
                sb.Append(w).Append(' ');
                await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.StreamToken,
                    new StreamTokenMessage(msg.Request.RequestId, NodeId, w + " ")), ct);
                await Task.Delay(TimeSpan.FromSeconds(0.12), ct);
            }
            var duration = DateTimeOffset.UtcNow - started;
            await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.Complete,
                new CompleteMessage(msg.Request.RequestId, NodeId, sb.ToString(),
                    new InferenceUsage(started, duration, words.Length, words.Length / Math.Max(duration.TotalSeconds, 1e-3)))), ct);
        }
        else
        {
            // ~4 KiB activation crosses to the next stage (the LAN transfer we model).
            var payload = new byte[4 * 1024];
            Random.Shared.NextBytes(payload);
            await ws.SendAsync(MeshProtocol.Wrap(MessageTypes.StreamActivation,
                new StreamActivationMessage(msg.Request.RequestId, NodeId, msg.StageIndex, payload)), ct);
        }
    }

    private NodeInfo BuildInfo() => new(
        NodeId,
        _options.NodeName,
        _options.Host,
        _options.Port,
        _capability,
        BuildLoad(),
        BuildModels(),
        NodeHealth.Healthy,
        DateTimeOffset.UtcNow);

    private NodeLoad BuildLoad() => new(
        _engine.ActiveRequests,
        _engine.QueuedRequests,
        _engine.MeasuredTokensPerSec,
        _models.Sum(m => EstimateMemory(m.ModelId)));

    private List<ModelPlacement> BuildModels() => _models.ToList();

    private long EstimateMemory(string modelId)
    {
        // The runtime owns memory accounting; report 0 for the mock (see Foundry adapter for real numbers).
        return 0;
    }

    public ValueTask DisposeAsync() => _ws?.DisposeAsync() ?? ValueTask.CompletedTask;
}
