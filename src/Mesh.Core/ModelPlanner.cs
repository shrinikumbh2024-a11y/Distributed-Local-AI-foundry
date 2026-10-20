namespace Mesh.Core;

using Mesh.Abstractions;

/// <summary>
/// Estimates model memory footprint and whether a model fits a single node.
/// The numbers are planning estimates: weights = params × (quantBits / 8),
/// plus a 20% overhead for embeddings, activations and allocator slack, plus
/// the caller's KV-cache budget.
/// </summary>
public sealed class ModelPlanner : IModelPlanner
{
    private const double OverheadFactor = 1.20;

    public long EstimateMemoryBytes(ModelDescriptor model, long kvCacheBytes = 0)
    {
        double weightBytes = model.ParametersB * 1_000_000_000.0 * (model.QuantBits / 8.0);
        if (model.ModelBytes > 0)
        {
            // Pre-computed on-disk size is usually the weights; still apply overhead.
            weightBytes = model.ModelBytes;
        }

        return (long)(weightBytes * OverheadFactor) + kvCacheBytes;
    }

    public bool FitsInNode(ModelDescriptor model, NodeInfo node, long kvCacheBytes = 0)
    {
        var need = EstimateMemoryBytes(model, kvCacheBytes);
        return need <= node.Capability.UsableMemoryBytes;
    }

    public int MinimumNodes(ModelDescriptor model, IReadOnlyList<NodeInfo> nodes, long kvCacheBytes = 0)
    {
        var usable = nodes
            .Where(n => IsUsable(n))
            .Select(n => (long)n.Capability.UsableMemoryBytes)
            .DefaultIfEmpty(0)
            .Max();

        if (usable <= 0)
        {
            return int.MaxValue;
        }

        var need = EstimateMemoryBytes(model, kvCacheBytes);
        return (int)Math.Ceiling((double)need / usable);
    }

    /// <summary>Whether a node is a valid placement target (alive and not offline).</summary>
    public static bool IsUsable(NodeInfo node) =>
        node.Health is NodeHealth.Healthy or NodeHealth.Degraded;
}
