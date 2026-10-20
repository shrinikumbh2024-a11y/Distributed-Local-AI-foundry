using System.Runtime.CompilerServices;

using Mesh.Abstractions;

// NOTE: this file references the Microsoft Foundry Local .NET SDK
// (Microsoft.AI.Foundry.Local / Microsoft.AI.Foundry.Local.WinML) and the
// Betalgo.Ranul.OpenAI types it exposes. The SDK is fetched from NuGet, which
// is not reachable in the offline sandbox this repo was authored in, so this
// adapter is NOT compile-verified here. The API shape below follows the
// official quickstart (learn.microsoft.com/windows/ai/foundry-local/get-started);
// verify member names against the installed SDK via IntelliSense.
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mesh.Runtime.FoundryLocal;

/// <summary>
/// Real Foundry Local runtime. Runs the model in-process via the Foundry Local
/// native SDK (ONNX Runtime GenAI) on the node's CPU/GPU/NPU. Drop this into
/// Mesh.Agent's composition root in place of <see cref="MockRuntime"/>.
/// </summary>
public sealed class FoundryLocalRuntime : IRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public string RuntimeName => "FoundryLocal";

    public bool IsLoaded(string modelId)
    {
        lock (_gate) return _loaded.Contains(modelId);
    }

    public IReadOnlyCollection<string> LoadedModels
    {
        get { lock (_gate) return _loaded.ToArray(); }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await FoundryLocalManager.CreateAsync(
            new Configuration { AppName = "mesh-agent" },
            NullLogger.Instance);
        _initialized = true;
    }

    public async Task LoadAsync(ModelDescriptor model, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        var alias = model.Aliases.FirstOrDefault() ?? model.Id;

        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
        var m = await catalog.GetModelAsync(alias)
                ?? throw new InvalidOperationException($"Model alias '{alias}' not found in the Foundry Local catalog.");

        if (!await m.IsCachedAsync())
        {
            await m.DownloadAsync(_ => { });
        }

        await m.LoadAsync();

        lock (_gate)
        {
            _aliases[model.Id] = alias;
            _loaded.Add(model.Id);
        }
    }

    public Task UnloadAsync(string modelId, CancellationToken ct = default)
    {
        lock (_gate) _loaded.Remove(modelId);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        string alias;
        lock (_gate)
        {
            if (!_loaded.Contains(request.ModelId))
            {
                throw new InvalidOperationException($"Model '{request.ModelId}' is not loaded.");
            }
            alias = _aliases[request.ModelId];
        }

        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
        var model = await catalog.GetModelAsync(alias)
                    ?? throw new InvalidOperationException($"Model alias '{alias}' not found.");

        var chatClient = await model.GetChatClientAsync();
        chatClient.Settings.Temperature = request.Temperature;
        chatClient.Settings.MaxTokens = request.MaxTokens;
        chatClient.Settings.TopP = 0.9f;

        var messages = new[]
        {
            new ChatMessage { Role = "system", Content = request.SystemPrompt ?? "You are a helpful assistant." },
            new ChatMessage { Role = "user", Content = request.Prompt },
        };

        await foreach (var chunk in chatClient.CompleteChatStreamingAsync(messages, ct))
        {
            var delta = chunk.Choices?[0]?.Message?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }
}
