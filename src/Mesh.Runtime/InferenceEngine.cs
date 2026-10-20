using System.Runtime.CompilerServices;

namespace Mesh.Runtime;

using Mesh.Abstractions;

/// <summary>
/// Per-node execution unit: wraps an <see cref="IRuntime"/>, tracks load and a
/// rolling measured throughput, and turns a request into a token stream. Used by
/// both the real agent and the simulator.
/// </summary>
public sealed class InferenceEngine
{
    private readonly IRuntime _runtime;
    private int _active;
    private int _queued;
    private double _measuredTps;

    public InferenceEngine(IRuntime runtime)
    {
        _runtime = runtime;
    }

    public IRuntime Runtime => _runtime;

    public int ActiveRequests => Volatile.Read(ref _active);
    public int QueuedRequests => Volatile.Read(ref _queued);
    public double MeasuredTokensPerSec => Volatile.Read(ref _measuredTps);
    public IReadOnlyCollection<string> LoadedModels => _runtime.LoadedModels;

    public bool IsLoaded(string modelId) => _runtime.IsLoaded(modelId);

    public Task LoadAsync(ModelDescriptor model, CancellationToken ct = default) =>
        _runtime.LoadAsync(model, ct);

    public Task UnloadAsync(string modelId, CancellationToken ct = default) =>
        _runtime.UnloadAsync(modelId, ct);

    public async IAsyncEnumerable<string> StreamAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Interlocked.Increment(ref _active);
        var started = DateTimeOffset.UtcNow;
        var tokens = 0;

        try
        {
            await foreach (var delta in _runtime.StreamAsync(request, ct))
            {
                tokens++;
                yield return delta;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            if (tokens > 0)
            {
                var seconds = Math.Max((DateTimeOffset.UtcNow - started).TotalSeconds, 1e-3);
                var tps = tokens / seconds;
                // Exponential moving average over measured throughput.
                var prev = Volatile.Read(ref _measuredTps);
                Volatile.Write(ref _measuredTps, prev <= 0 ? tps : prev * 0.7 + tps * 0.3);
            }
        }
    }
}
