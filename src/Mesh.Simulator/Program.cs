using Mesh.Abstractions;
using Mesh.Core;

// ============================================================================
// Simulates the target cluster (100 × Intel i7 / 16 GB, CPU-only, 1 GbE LAN)
// and exercises BOTH scheduling strategies against the real Mesh.Core logic.
// No network, no models downloaded — this validates the planning math.
// ============================================================================

var nodes = BuildCluster(100);
var router = new MeshRouter();
var options = new SchedulingOptions();

Console.WriteLine($"Simulated cluster: {nodes.Count} nodes, {nodes.Sum(n => n.Capability.CpuCores)} CPU cores, " +
                  $"{FormatBytes(nodes.Sum(n => n.Capability.TotalMemoryBytes))} RAM pooled.");
Console.WriteLine($"Per-node usable RAM: {FormatBytes(nodes[0].Capability.UsableMemoryBytes)} (6 GiB reserved for OS).");
Console.WriteLine();

// --- Approach A: models that fit a single node -----------------------------
Console.WriteLine("=== APPROACH A — horizontal (whole model on one node) ===");
foreach (var id in new[] { "phi-4-mini", "llama-3.1-8b", "qwen2.5-14b" })
{
    var model = ModelCatalog.Find(id)!;
    var req = new InferenceRequest($"req-{id}", model.Id, "hello", null, 256, 0.7f, null);
    var d = router.Plan(req, model, nodes, options);
    Console.WriteLine($"  {model.Id,-14} ({model.ParametersB,4} B Q{model.QuantBits})  ->  {d.Strategy,-11} node={d.NodeId,-8} est {d.EstimatedTokensPerSec,5:0.#} tok/s");
    Console.WriteLine($"       reason: {d.Reason}");
}
Console.WriteLine();

// --- Approach B: models too big for one node --------------------------------
Console.WriteLine("=== APPROACH B — pipeline split (model too big for one node) ===");
foreach (var id in new[] { "qwen2.5-32b", "llama-3.1-70b" })
{
    var model = ModelCatalog.Find(id)!;
    var req = new InferenceRequest($"req-{id}", model.Id, "hello", null, 256, 0.7f, null);
    var d = router.Plan(req, model, nodes, options);
    Console.WriteLine($"  {model.Id,-14} ({model.ParametersB,4} B Q{model.QuantBits})  ->  {d.Strategy}");
    if (d.Pipeline is not null)
    {
        Console.WriteLine($"       {d.Pipeline.Stages.Count} stages, est {d.EstimatedTokensPerSec:0.##} tok/s (bandwidth-bound)");
        foreach (var s in d.Pipeline.Stages)
        {
            Console.WriteLine($"       stage {s.Index}: {s.NodeId}  layers {s.LayerStart}..{s.LayerEnd}  ~{FormatBytes(s.EstimatedMemoryBytes)}");
        }
    }
    else
    {
        Console.WriteLine($"       {d.Reason}");
    }
}
Console.WriteLine();

// --- Horizontal load-balancing across many requests -------------------------
Console.WriteLine("=== A: load-balancing 30 concurrent phi-4-mini requests ===");
var modelA = ModelCatalog.Find("phi-4-mini")!;
var picks = new Dictionary<string, int>();
for (var i = 0; i < 30; i++)
{
    var d = router.Plan(new InferenceRequest($"r{i}", modelA.Id, "hi", null, 128, 0.7f, null), modelA, nodes, options);
    var node = d.NodeId ?? "?";
    picks[node] = picks.GetValueOrDefault(node) + 1;
    // Simulate the request landing on the node (increment its load) so the
    // scheduler spreads subsequent requests instead of piling on one node.
    var picked = nodes.First(n => n.Id == node);
    var idx = nodes.IndexOf(picked);
    nodes[idx] = picked with { Load = picked.Load with { ActiveRequests = picked.Load.ActiveRequests + 1 } };
}
Console.WriteLine($"  Distinct nodes used: {picks.Count} / {nodes.Count}");
Console.WriteLine($"  Top 5: {string.Join(", ", picks.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}={kv.Value}"))}");

Console.WriteLine();
Console.WriteLine("Done.");
return 0;

static List<NodeInfo> BuildCluster(int count)
{
    var rnd = new Random(42);
    var list = new List<NodeInfo>(count);
    const long total = 16L * 1024 * 1024 * 1024;          // 16 GiB
    const long reserved = 6L * 1024 * 1024 * 1024;        // 6 GiB for OS

    for (var i = 0; i < count; i++)
    {
        var fabric = i % 4 == 0 ? "switch-b" : "switch-a";
        var models = new List<ModelPlacement>();
        if (i % 3 == 0) models.Add(new ModelPlacement("phi-4-mini", true, 1));
        if (i % 5 == 0) models.Add(new ModelPlacement("llama-3.1-8b", true, 1));

        list.Add(new NodeInfo(
            $"node-{i:D3}", $"node-{i:D3}", $"10.0.0.{i + 10}", 0,
            new NodeCapability(8, total, total - reserved, AcceleratorKind.None, 0, fabric, "Windows"),
            new NodeLoad(rnd.Next(0, 4), rnd.Next(0, 2), 6.0, 0),
            models, NodeHealth.Healthy, DateTimeOffset.UtcNow));
    }

    return list;
}

static string FormatBytes(long b)
{
    string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
    var v = (double)b;
    var i = 0;
    while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
    return $"{v:0.#} {units[i]}";
}
