using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Mesh.Runtime;

using Mesh.Abstractions;

/// <summary>
/// Deterministic simulated runtime for development and tests — no model is
/// downloaded. It models per-node CPU throughput (tokens/sec) and memory
/// footprint so the whole scheduler/router/streaming stack runs end-to-end on
/// one machine. Swap in <c>FoundryLocalRuntime</c> for real inference.
/// </summary>
public sealed class MockRuntime : IRuntime
{
    private readonly double _tokensPerSec;
    private readonly ConcurrentDictionary<string, ModelDescriptor> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public MockRuntime(double tokensPerSec = 6.0)
    {
        _tokensPerSec = Math.Max(tokensPerSec, 0.1);
    }

    public string RuntimeName => "Mock";

    public bool IsLoaded(string modelId) => _loaded.ContainsKey(modelId);

    public IReadOnlyCollection<string> LoadedModels => _loaded.Keys.ToArray();

    public Task LoadAsync(ModelDescriptor model, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _loaded[model.Id] = model;
        return Task.CompletedTask;
    }

    public Task UnloadAsync(string modelId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _loaded.TryRemove(modelId, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_loaded.TryGetValue(request.ModelId, out var model))
        {
            throw new InvalidOperationException($"Model '{request.ModelId}' is not loaded on this runtime.");
        }

        // Scale the simulated rate by model size so bigger models are slower.
        var tps = _tokensPerSec * Math.Clamp(7.0 / Math.Max(model.ParametersB, 1.0), 0.15, 1.0);
        var words = BuildResponse(model, request);

        for (var i = 0; i < words.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1.0 / tps), ct);
            var suffix = i < words.Count - 1 ? " " : "";
            yield return words[i] + suffix;
        }
    }

    private static List<string> BuildResponse(ModelDescriptor model, InferenceRequest request)
    {
        var prompt = request.Prompt.Length > 60 ? request.Prompt[..60] : request.Prompt;
        return new List<string>
        {
            "[Mock", model.Family, "node]",
            "This", "is", "a", "simulated", "response", "to:", "\"", prompt, "\".",
            "Generated", "at", "~6-10", "tokens/sec", "on", "a", "CPU", "node."
        };
    }
}
