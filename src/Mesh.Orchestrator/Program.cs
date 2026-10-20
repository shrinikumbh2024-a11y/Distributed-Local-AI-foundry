using Mesh.Orchestrator.Components;
using Mesh.Orchestrator.Services;
using Mesh.Core;

var builder = WebApplication.CreateBuilder(args);

// Console-only logging: the default Windows EventLog provider fails on locked-down
// hosts (and is useless in a headless/air-gapped deployment).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<ClusterState>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddHostedService<NodeMonitor>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseWebSockets();
app.UseAntiforgery();

// ---- Agent WebSocket endpoint (raw WS, no external deps) ----
var state = app.Services.GetRequiredService<ClusterState>();
app.Map("/agents", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Expected a WebSocket connection.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = new AgentConnection(socket, state);
    await connection.ReceiveLoopAsync(context.RequestAborted);
});

// ---- Diagnostics REST endpoints ----
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", now = DateTimeOffset.UtcNow }));
app.MapGet("/api/nodes", (ClusterState s) => Results.Ok(s.Nodes));
app.MapGet("/api/models", () => Results.Ok(ModelCatalog.Models));
app.MapGet("/api/stats", (ClusterState s) => Results.Ok(new
{
    nodes = s.Nodes.Count(n => n.Health == Mesh.Abstractions.NodeHealth.Healthy),
    total = s.Nodes.Count,
    totalRequests = s.TotalRequests,
}));

// Headless chat (also handy for scripting / integration tests).
app.MapPost("/api/chat", async (ChatRequest req, ChatService chat, ClusterState state) =>
{
    var submit = chat.Submit(req.ModelId, req.Prompt, req.SystemPrompt, req.MaxTokens, req.Temperature, req.PrefixTag);
    if (submit.Error is not null)
    {
        return Results.BadRequest(submit.Error);
    }

    var inflight = state.GetInflight(submit.RequestId);
    if (inflight is null)
    {
        return Results.Problem("Request was not registered.");
    }

    var sb = new System.Text.StringBuilder();
    await foreach (var chunk in inflight.Stream.Reader.ReadAllAsync())
    {
        sb.Append(chunk);
    }

    return Results.Ok(new
    {
        requestId = submit.RequestId,
        strategy = submit.Decision.Strategy,
        node = submit.Decision.NodeId,
        reason = submit.Decision.Reason,
        text = sb.ToString(),
    });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
