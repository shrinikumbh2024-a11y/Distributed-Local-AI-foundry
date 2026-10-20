namespace Mesh.Core;

using Mesh.Abstractions;

/// <summary>
/// The router: picks horizontal (A) when any node can host the whole model,
/// otherwise falls back to pipeline partitioning (B), and reports why.
/// </summary>
public sealed class MeshRouter : IScheduler
{
    private readonly HorizontalScheduler _horizontal = new();
    private readonly PipelinePartitioner _partitioner = new();
    private readonly ModelPlanner _planner = new();

    public PlacementDecision Plan(
        InferenceRequest request,
        ModelDescriptor model,
        IReadOnlyList<NodeInfo> nodes,
        SchedulingOptions? options = null) =>
        PlanWithAffinity(request, model, nodes, affinityNodeId: null, options);

    /// <summary>
    /// Like <see cref="Plan"/> but accepts a KV-prefix affinity hint: the id of
    /// the node that last served <paramref name="request"/>'s prefix.
    /// </summary>
    public PlacementDecision PlanWithAffinity(
        InferenceRequest request,
        ModelDescriptor model,
        IReadOnlyList<NodeInfo> nodes,
        string? affinityNodeId,
        SchedulingOptions? options = null)
    {
        options ??= new SchedulingOptions();
        var kv = options.KvCacheBytesPerRequest;

        var healthy = nodes.Where(ModelPlanner.IsUsable).ToList();
        if (healthy.Count == 0)
        {
            return new PlacementDecision(
                SchedulingStrategy.Unavailable, null, null,
                "No healthy nodes are connected.", 0);
        }

        // Approach A — one node hosts the whole model.
        var best = _horizontal.SelectBest(request, model, nodes, options, affinityNodeId, out var hReason);
        if (best is not null)
        {
            var tps = ThroughputEstimator.EstimateNodeTokensPerSec(best, model.ParametersB);
            return new PlacementDecision(SchedulingStrategy.Horizontal, best.Id, null, hReason, tps);
        }

        // Approach B — split the model across nodes.
        var plan = _partitioner.Partition(model, nodes, options, kv, out var pReason);
        if (plan is not null)
        {
            var tps = ThroughputEstimator.EstimatePipelineTokensPerSec(plan, model, nodes);
            return new PlacementDecision(SchedulingStrategy.Pipeline, null, plan, pReason, tps);
        }

        var need = _planner.EstimateMemoryBytes(model, kv);
        return new PlacementDecision(
            SchedulingStrategy.Unavailable, null, null,
            $"{hReason} {pReason} (model needs ~{FormatBytes(need)}).", 0);
    }

    private static string FormatBytes(long b)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        var v = (double)b;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
