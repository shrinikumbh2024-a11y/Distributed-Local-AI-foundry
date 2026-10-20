namespace Mesh.Abstractions;

/// <summary>Kind of accelerator available on a node.</summary>
public enum AcceleratorKind
{
    /// <summary>No meaningful accelerator; CPU inference only.</summary>
    None = 0,
    Cpu = 1,
    CudaGpu = 2,
    Npu = 3,
}

public enum NodeHealth
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Offline = 3,
}

/// <summary>
/// How a request was scheduled. Horizontal means the whole model runs on one
/// node (throughput via replication); Pipeline means the model is split across
/// several nodes (capacity for models that exceed a single node).
/// </summary>
public enum SchedulingStrategy
{
    Horizontal = 0,
    Pipeline = 1,
    Unavailable = 2,
}
