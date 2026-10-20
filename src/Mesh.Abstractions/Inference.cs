namespace Mesh.Abstractions;

/// <summary>A single chat-completion request routed through the mesh.</summary>
public sealed record InferenceRequest(
    string RequestId,
    string ModelId,
    string Prompt,
    string? SystemPrompt = null,
    int MaxTokens = 256,
    float Temperature = 0.7f,
    /// <summary>
    /// Optional tag identifying a shared prompt prefix (system prompt, few-shot
    /// preamble, agent tools). The router prefers a node that already holds
    /// this prefix in its KV cache — a cheap, high-value throughput win.
    /// </summary>
    string? PrefixTag = null);

public sealed record InferenceUsage(
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    int TokensGenerated,
    double TokensPerSec);

public sealed record InferenceCompletion(
    string RequestId,
    string NodeId,
    string FullText,
    InferenceUsage Usage,
    string? Error = null);

/// <summary>One slice of a pipeline-partitioned model (approach B).</summary>
public sealed record PipelineStage(
    int Index,
    string NodeId,
    int LayerStart,
    int LayerEnd,
    long EstimatedMemoryBytes);

public sealed record PipelinePlan(
    string ModelId,
    int TotalLayers,
    IReadOnlyList<PipelineStage> Stages,
    SchedulingStrategy Strategy = SchedulingStrategy.Pipeline);

/// <summary>Result of the scheduler/router for a single request.</summary>
public sealed record PlacementDecision(
    SchedulingStrategy Strategy,
    string? NodeId,
    PipelinePlan? Pipeline,
    string Reason,
    double EstimatedTokensPerSec = 0);

/// <summary>Tunables for the scheduler.</summary>
public sealed record SchedulingOptions(
    int MaxPipelineStages = 8,
    long ReservedSystemBytesPerNode = 6L * 1024 * 1024 * 1024, // 6 GiB for OS + activations
    long KvCacheBytesPerRequest = 256L * 1024 * 1024,          // 256 MiB KV budget per in-flight request
    double MaxLoadFactor = 0.9,
    bool PreferLoadedModel = true,
    bool PreferPrefixAffinity = true);
