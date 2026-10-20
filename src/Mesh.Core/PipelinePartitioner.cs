namespace Mesh.Core;

using Mesh.Abstractions;

/// <summary>
/// Approach B — pipeline model parallelism. When a model is too large for any
/// single node, contiguous layer ranges are placed across several nodes; each
/// request's activations stream stage-to-stage. On a LAN this only pays off for
/// models whose per-token compute dominates the transfer cost (see
/// <see cref="ThroughputEstimator"/>); the scheduler decides that tradeoff.
/// </summary>
public sealed class PipelinePartitioner
{
    private readonly ModelPlanner _planner = new();

    public PipelinePlan? Partition(
        ModelDescriptor model,
        IReadOnlyList<NodeInfo> nodes,
        SchedulingOptions options,
        long kvCacheBytes,
        out string reason)
    {
        var usableNodes = nodes.Where(ModelPlanner.IsUsable).ToList();
        if (usableNodes.Count == 0)
        {
            reason = "No healthy nodes available for partitioning.";
            return null;
        }

        var totalLayers = Math.Max(model.Layers, 1);
        var perLayer = PerLayerBytes(model, totalLayers);
        var totalWeight = perLayer * totalLayers;

        // Conservative: the cluster must hold the weights plus one KV budget.
        var totalNeed = totalWeight + kvCacheBytes;
        var totalCapacity = usableNodes.Sum(n => (long)n.Capability.UsableMemoryBytes);
        if (totalCapacity < totalNeed)
        {
            reason = $"Cluster memory insufficient: need {Bytes(totalNeed)}, have {Bytes(totalCapacity)}.";
            return null;
        }

        var minNodes = _planner.MinimumNodes(model, usableNodes, kvCacheBytes);
        if (minNodes > options.MaxPipelineStages)
        {
            reason = $"Model needs ≥ {minNodes} nodes but MaxPipelineStages={options.MaxPipelineStages}.";
            return null;
        }

        // Order: same fabric group first (fewer / faster hops), then most memory.
        var ordered = usableNodes
            .OrderBy(n => n.Capability.FabricGroup, StringComparer.Ordinal)
            .ThenByDescending(n => n.Capability.UsableMemoryBytes)
            .ToList();

        // Greedy contiguous layer assignment by weight.
        var stages = new List<PipelineStage>();
        var layer = 0;
        var nodeIndex = 0;

        while (layer < totalLayers && nodeIndex < ordered.Count)
        {
            var node = ordered[nodeIndex];
            var start = layer;
            var used = 0L;

            while (layer < totalLayers && used + perLayer <= node.Capability.UsableMemoryBytes)
            {
                used += perLayer;
                layer++;
            }

            if (used == 0)
            {
                // This node cannot hold even one layer; skip it.
                nodeIndex++;
                continue;
            }

            stages.Add(new PipelineStage(stages.Count, node.Id, start, layer - 1, used));
            nodeIndex++;
        }

        if (layer < totalLayers)
        {
            reason = $"Could not place all {totalLayers} layers across the available nodes.";
            return null;
        }

        // Per-stage KV cache is small and allocated at request time, so stage
        // memory here is weight-only; the cluster-wide totalNeed check above
        // already reserves headroom for one request's KV budget.
        reason = $"Pipeline: {totalLayers} layers across {stages.Count} nodes.";
        return new PipelinePlan(model.Id, totalLayers, stages, SchedulingStrategy.Pipeline);
    }

    private long PerLayerBytes(ModelDescriptor model, int totalLayers)
    {
        var weightBytes = model.ModelBytes > 0
            ? model.ModelBytes
            : (long)(model.ParametersB * 1_000_000_000.0 * (model.QuantBits / 8.0));

        // Overhead factor matches ModelPlanner (1.20).
        return Math.Max((long)(weightBytes * 1.20 / totalLayers), 1);
    }

    private static string Bytes(long b)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        var v = (double)b;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
