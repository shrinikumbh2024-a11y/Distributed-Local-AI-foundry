namespace Mesh.Abstractions;

/// <summary>
/// A model from the catalog. <see cref="ParametersB"/> is billions of
/// parameters; <see cref="QuantBits"/> is the weight precision (4 = Q4, 8 = Q8,
/// 16 = FP16). <see cref="Layers"/> drives pipeline partitioning (approach B).
/// </summary>
public sealed record ModelDescriptor(
    string Id,
    string Family,
    double ParametersB,
    int QuantBits,
    int Layers,
    IReadOnlyList<string> Aliases)
{
    /// <summary>Optional pre-computed on-disk size; 0 means "estimate it".</summary>
    public long ModelBytes { get; init; }
}

/// <summary>Static hardware capability of a node.</summary>
public sealed record NodeCapability(
    int CpuCores,
    long TotalMemoryBytes,
    long UsableMemoryBytes,
    AcceleratorKind Accelerator,
    long AcceleratorMemoryBytes,
    string FabricGroup,
    string Os);

/// <summary>Volatile load snapshot of a node.</summary>
public sealed record NodeLoad(
    int ActiveRequests,
    int QueuedRequests,
    double MeasuredTokensPerSec,
    long MemoryUsedBytes);

/// <summary>Whether a model is present on a node (cached and/or loaded).</summary>
public sealed record ModelPlacement(
    string ModelId,
    bool Loaded,
    int Replicas);

/// <summary>Immutable descriptor of a participating node.</summary>
public sealed record NodeInfo(
    string Id,
    string Name,
    string Host,
    int Port,
    NodeCapability Capability,
    NodeLoad Load,
    IReadOnlyList<ModelPlacement> Models,
    NodeHealth Health,
    DateTimeOffset LastHeartbeatUtc);
