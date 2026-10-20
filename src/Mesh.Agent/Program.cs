using Mesh.Agent;
using Mesh.Runtime;

var options = AgentOptions.Parse(args);

Console.WriteLine($"[{options.NodeId}] Mesh agent starting");
Console.WriteLine($"  orchestrator: {options.OrchestratorUri}");
Console.WriteLine($"  fabric group: {options.FabricGroup}");

// The runtime is pluggable. Default is the deterministic MockRuntime so the
// agent is runnable anywhere; point the factory at FoundryLocalRuntime when
// deploying real models (see src/Mesh.Runtime.FoundryLocal).
var engine = new InferenceEngine(new MockRuntime(options.TokensPerSec));

var agent = new AgentNode(options, engine);
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await agent.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown.
}

Console.WriteLine($"[{options.NodeId}] stopped");
