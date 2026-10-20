namespace Mesh.Core;

using Mesh.Abstractions;

/// <summary>
/// Coarse throughput estimates used for planning and the UI. These are
/// deliberately rough: they exist so the scheduler can rank options and print
/// useful numbers without a benchmark on every node. A node's measured
/// <see cref="NodeLoad.MeasuredTokensPerSec"/> (from its local benchmark)
/// always wins when available.
/// </summary>
public static class ThroughputEstimator
{
    /// <summary>Assumed activation payload per token transferred between pipeline stages.</summary>
    public const long ActivationBytesPerToken = 4 * 1024; // 4 KiB

    /// <summary>Default LAN bandwidth: 1 GbE in bytes/second.</summary>
    public const long DefaultBandwidthBytesPerSec = 125_000_000;

    /// <summary>
    /// CPU-only heuristic. A modern 8-core desktop i7 does roughly 6–10 tok/s on a
    /// 7B Q4 model; we model tokens/sec ≈ cores × 0.8 × (7 / paramsB). This is a
    /// planning number, not a guarantee.
    /// </summary>
    public static double EstimateNodeTokensPerSec(NodeInfo node, double parametersB)
    {
        if (node.Load.MeasuredTokensPerSec > 0.5)
        {
            return node.Load.MeasuredTokensPerSec;
        }

        var effectiveParams = Math.Max(parametersB, 0.1);
        return node.Capability.CpuCores * 0.8 * (7.0 / effectiveParams);
    }

    /// <summary>
    /// Pipeline throughput ≈ 1 / (Σ per-stage compute time + per-token transfer).
    /// This captures the two real bottlenecks: the slowest stage and the LAN hop
    /// every activation must cross. On 1 GbE and CPU nodes this is usually
    /// dominated by compute, which is the only regime where pipeline splitting
    /// over a LAN is worth doing.
    /// </summary>
    public static double EstimatePipelineTokensPerSec(
        PipelinePlan plan,
        ModelDescriptor model,
        IReadOnlyList<NodeInfo> nodes,
        long bandwidthBytesPerSec = DefaultBandwidthBytesPerSec)
    {
        var nodeById = nodes.ToDictionary(n => n.Id);
        var totalLayers = Math.Max(model.Layers, 1);

        double computeSecondsPerToken = 0;
        foreach (var stage in plan.Stages)
        {
            if (!nodeById.TryGetValue(stage.NodeId, out var node))
            {
                continue;
            }

            var layers = Math.Max(stage.LayerEnd - stage.LayerStart + 1, 1);
            var stageParams = model.ParametersB * layers / totalLayers;
            var stageTps = Math.Max(EstimateNodeTokensPerSec(node, stageParams), 1e-6);
            computeSecondsPerToken += 1.0 / stageTps;
        }

        var hops = Math.Max(plan.Stages.Count - 1, 0);
        var transferSecondsPerToken = hops * (ActivationBytesPerToken / (double)bandwidthBytesPerSec);

        return 1.0 / (computeSecondsPerToken + transferSecondsPerToken);
    }
}
