namespace Mesh.Core;

using Mesh.Abstractions;

/// <summary>
/// Approach A — horizontal scheduling. The whole model runs on one node; we
/// spread requests across the many nodes that can each host a full copy. This
/// is the primary strategy for a 100 × i7 / 16 GB CPU cluster running ≤ ~14B
/// models: it scales throughput (not single-prompt latency) linearly with nodes.
/// </summary>
public sealed class HorizontalScheduler
{
    private readonly ModelPlanner _planner = new();

    /// <summary>
    /// Selects the best node for a whole-model request, or null when nothing
    /// qualifies. Scoring prefers: healthy nodes that fit the model, low load,
    /// already-loaded model, and KV prefix affinity.
    /// </summary>
    public NodeInfo? SelectBest(
        InferenceRequest request,
        ModelDescriptor model,
        IReadOnlyList<NodeInfo> nodes,
        SchedulingOptions options,
        string? affinityNodeId,
        out string reason)
    {
        var kv = options.KvCacheBytesPerRequest;
        var candidates = nodes
            .Where(ModelPlanner.IsUsable)
            .Where(n => _planner.FitsInNode(model, n, kv))
            .ToList();

        if (candidates.Count == 0)
        {
            reason = "No healthy node has enough memory for the full model.";
            return null;
        }

        var withinLoad = candidates
            .Where(n => LoadFactor(n) <= options.MaxLoadFactor)
            .ToList();

        // If everything is saturated, still choose the least loaded rather than fail.
        var pool = withinLoad.Count > 0 ? withinLoad : candidates;

        var best = pool
            .OrderBy(n => Score(n, model, request, options, affinityNodeId))
            .First();

        reason = BuildReason(best, model, request, affinityNodeId);
        return best;
    }

    /// <summary>Lower is better.</summary>
    private static double Score(
        NodeInfo node,
        ModelDescriptor model,
        InferenceRequest request,
        SchedulingOptions options,
        string? affinityNodeId)
    {
        var score = 0.0;

        // Load: queued requests cost more than active ones.
        score += node.Load.ActiveRequests * 10.0;
        score += node.Load.QueuedRequests * 20.0;

        // Prefer a node that already has the model resident (avoids load latency).
        var loaded = node.Models.Any(m => m.ModelId == model.Id && m.Loaded);
        if (!loaded && options.PreferLoadedModel)
        {
            score += 40.0;
        }

        // KV prefix affinity: send repeat/agent prompts back to the same node.
        if (options.PreferPrefixAffinity &&
            request.PrefixTag is not null &&
            affinityNodeId is not null &&
            node.Id == affinityNodeId)
        {
            score -= 25.0;
        }

        return score;
    }

    private static double LoadFactor(NodeInfo node)
    {
        var total = Math.Max(node.Capability.UsableMemoryBytes, 1);
        return Math.Clamp(node.Load.MemoryUsedBytes / (double)total, 0, 1);
    }

    private static string BuildReason(NodeInfo node, ModelDescriptor model, InferenceRequest request, string? affinityNodeId)
    {
        var loaded = node.Models.Any(m => m.ModelId == model.Id && m.Loaded);
        var prefix = request.PrefixTag is not null && affinityNodeId == node.Id ? " prefix-affinity" : "";
        return $"Horizontal: node '{node.Name}' (load={node.Load.ActiveRequests}/{node.Load.QueuedRequests} active/queued," +
               $" model {(loaded ? "resident" : "cold-load")}{prefix}).";
    }
}
