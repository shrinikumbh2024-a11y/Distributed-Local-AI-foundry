namespace Mesh.Orchestrator.Services;

using Mesh.Abstractions;
using Mesh.Abstractions.Protocol;
using Mesh.Core;

public sealed record ChatSubmit(string RequestId, PlacementDecision Decision, string? Error);

public sealed record ChatRequest(
    string ModelId,
    string Prompt,
    string? SystemPrompt = null,
    int MaxTokens = 256,
    float Temperature = 0.7f,
    string? PrefixTag = null);

/// <summary>
/// Turns a UI prompt into a placement decision and drives execution: horizontal
/// dispatch to one node, or a sequential pipeline walk across stages for models
/// that don't fit a single node.
/// </summary>
public sealed class ChatService
{
    private readonly ClusterState _state;
    private readonly MeshRouter _router = new();
    private readonly SchedulingOptions _options = new();

    public ChatService(ClusterState state) => _state = state;

    public ChatSubmit Submit(string modelId, string prompt, string? systemPrompt, int maxTokens, float temperature, string? prefixTag)
    {
        var model = ModelCatalog.Find(modelId);
        if (model is null)
        {
            return new ChatSubmit(string.Empty,
                new PlacementDecision(SchedulingStrategy.Unavailable, null, null, $"Unknown model '{modelId}'.", 0),
                $"Unknown model '{modelId}'.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new InferenceRequest(requestId, model.Id, prompt, systemPrompt, maxTokens, temperature, prefixTag);

        var affinity = prefixTag is null ? null : _state.GetAffinity(prefixTag);
        var decision = _router.PlanWithAffinity(request, model, _state.Nodes, affinity, _options);

        var inflight = _state.BeginRequest(request, model, decision);
        _ = Task.Run(() => DriveAsync(inflight));

        return new ChatSubmit(requestId, decision, null);
    }

    private async Task DriveAsync(InFlightRequest r)
    {
        try
        {
            switch (r.Decision.Strategy)
            {
                case SchedulingStrategy.Horizontal:
                    await DriveHorizontalAsync(r);
                    break;
                case SchedulingStrategy.Pipeline:
                    await DrivePipelineAsync(r);
                    break;
                default:
                    _state.OnError(new ErrorMessage(r.Request.RequestId, "orchestrator", r.Decision.Reason));
                    break;
            }
        }
        catch (Exception ex)
        {
            _state.OnError(new ErrorMessage(r.Request.RequestId, "orchestrator", ex.Message));
        }
    }

    private async Task DriveHorizontalAsync(InFlightRequest r)
    {
        var conn = _state.GetConnection(r.Decision.NodeId!);
        if (conn is null)
        {
            _state.OnError(new ErrorMessage(r.Request.RequestId, "orchestrator", "target node went offline."));
            return;
        }

        await conn.SendAsync(MeshProtocol.Wrap(MessageTypes.Dispatch,
            new DispatchMessage(r.Request, r.Model, Plan: null, StageIndex: -1)));
    }

    private async Task DrivePipelineAsync(InFlightRequest r)
    {
        var plan = r.Decision.Pipeline!;
        byte[]? activation = null;

        for (var i = 0; i < plan.Stages.Count; i++)
        {
            var stage = plan.Stages[i];
            var conn = _state.GetConnection(stage.NodeId);
            if (conn is null)
            {
                _state.OnError(new ErrorMessage(r.Request.RequestId, "orchestrator", $"pipeline stage {i} node offline."));
                return;
            }

            r.Stage = new TaskCompletionSource<StageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await conn.SendAsync(MeshProtocol.Wrap(MessageTypes.Dispatch,
                new DispatchMessage(r.Request, r.Model, plan, StageIndex: i, Activation: activation)));

            var result = await r.Stage.Task;
            if (result.Error is not null)
            {
                return; // OnError already closed the stream.
            }

            activation = result.Activation;
        }
    }
}
