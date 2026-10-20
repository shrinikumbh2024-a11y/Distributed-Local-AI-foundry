using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mesh.Abstractions.Protocol;

/// <summary>
/// Wire envelope. Every message between agent and orchestrator is
/// <c>{ "type": "...", "payload": { ... } }</c> over a raw WebSocket.
/// </summary>
public sealed class MeshEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    public MeshEnvelope() { }

    public MeshEnvelope(string type, JsonElement payload)
    {
        Type = type;
        Payload = payload;
    }
}

/// <summary>Message type names used on the wire (stable contract).</summary>
public static class MessageTypes
{
    // agent -> orchestrator
    public const string Register = "register";
    public const string Heartbeat = "heartbeat";
    public const string ModelLoaded = "model-loaded";
    public const string ModelUnloaded = "model-unloaded";
    public const string StreamToken = "stream-token";
    public const string StreamActivation = "stream-activation";
    public const string Complete = "complete";
    public const string Error = "error";
    public const string Pong = "pong";

    // orchestrator -> agent
    public const string Welcome = "welcome";
    public const string Ping = "ping";
    public const string Dispatch = "dispatch";
    public const string LoadModel = "load-model";
    public const string UnloadModel = "unload-model";
    public const string Cancel = "cancel";
}

public sealed record RegisterMessage(NodeInfo Node);

public sealed record HeartbeatMessage(
    string NodeId,
    NodeLoad Load,
    IReadOnlyList<ModelPlacement> Models,
    double MeasuredTokensPerSec);

/// <summary>
/// Orchestrator asks an agent to serve a request. When <see cref="Plan"/> is
/// non-null the agent is one stage of a pipeline (approach B) and should
/// cooperate through the orchestrator relay.
/// </summary>
public sealed record DispatchMessage(
    InferenceRequest Request,
    /// <summary>The model descriptor, so the agent can (lazily) load it if needed.</summary>
    ModelDescriptor? Model,
    PipelinePlan? Plan,
    int StageIndex,
    /// <summary>
    /// Activation handed to a pipeline stage > 0 by the previous stage. Null for
    /// horizontal requests and for stage 0 (which consumes the prompt directly).
    /// </summary>
    byte[]? Activation = null);

public sealed record StreamTokenMessage(string RequestId, string NodeId, string Delta);

public sealed record StreamActivationMessage(string RequestId, string NodeId, int StageIndex, byte[] Payload);

public sealed record CompleteMessage(string RequestId, string NodeId, string FullText, InferenceUsage Usage);

public sealed record ErrorMessage(string RequestId, string NodeId, string Error);

public sealed record LoadModelMessage(string ModelId, ModelDescriptor? Model);

public sealed record UnloadModelMessage(string ModelId);

public sealed record CancelMessage(string RequestId);

public sealed record WelcomeMessage(string OrchestratorId);

public sealed record PingMessage(long TimestampUtcMs);

public sealed record PongMessage(long TimestampUtcMs, NodeInfo Node);

/// <summary>JSON codec helpers for the envelope protocol.</summary>
public static class MeshProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static MeshEnvelope Wrap<T>(string type, T payload) =>
        new(type, JsonSerializer.SerializeToElement(payload, Options));

    public static string SerializeEnvelope(MeshEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    public static MeshEnvelope? DeserializeEnvelope(string json) =>
        JsonSerializer.Deserialize<MeshEnvelope>(json, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T DeserializePayload<T>(MeshEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(Options) ?? throw new InvalidOperationException($"Failed to deserialize payload for '{envelope.Type}'.");
}
