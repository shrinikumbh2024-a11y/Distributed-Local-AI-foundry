namespace Mesh.Abstractions;

/// <summary>Estimates model memory footprint and single-node fit.</summary>
public interface IModelPlanner
{
    /// <summary>Estimated resident bytes for weights + overhead + KV budget.</summary>
    long EstimateMemoryBytes(ModelDescriptor model, long kvCacheBytes = 0);

    /// <summary>True if the model (plus KV budget) fits in one node.</summary>
    bool FitsInNode(ModelDescriptor model, NodeInfo node, long kvCacheBytes = 0);

    /// <summary>Minimum number of nodes needed to host the model via pipeline.</summary>
    int MinimumNodes(ModelDescriptor model, IReadOnlyList<NodeInfo> nodes, long kvCacheBytes = 0);
}

/// <summary>
/// The scheduling brain. Produces a <see cref="PlacementDecision"/> that either
/// routes the whole request to one node (horizontal) or splits the model across
/// nodes (pipeline), choosing automatically based on cluster capacity.
/// </summary>
public interface IScheduler
{
    PlacementDecision Plan(
        InferenceRequest request,
        ModelDescriptor model,
        IReadOnlyList<NodeInfo> nodes,
        SchedulingOptions? options = null);
}

/// <summary>A per-node inference runtime (whole-model execution).</summary>
public interface IRuntime
{
    string RuntimeName { get; }

    bool IsLoaded(string modelId);

    IReadOnlyCollection<string> LoadedModels { get; }

    /// <summary>Loads a model into memory (download/cache is the caller's job).</summary>
    Task LoadAsync(ModelDescriptor model, CancellationToken ct = default);

    Task UnloadAsync(string modelId, CancellationToken ct = default);

    /// <summary>
    /// Streams generated text deltas for a request. Implementations must honor
    /// <paramref name="ct"/> and stop emitting promptly on cancellation.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(InferenceRequest request, CancellationToken ct = default);
}
