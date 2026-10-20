using Mesh.Abstractions;
using Mesh.Core;

// Lightweight test harness (no xUnit — the sandbox has no NuGet network).
// Runs assertions against Mesh.Core and exits non-zero on any failure.

var failures = 0;

void Check(bool condition, string name)
{
    if (condition)
    {
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  FAIL  {name}");
    }
}

const long GiB = 1024L * 1024 * 1024;
const long MiB = 1024L * 1024;

static NodeInfo Node(string id, int active = 0, int queued = 0, string[]? loaded = null, long usable = 10 * GiB, string fabric = "switch-a") =>
    new(id, id, "10.0.0.1", 0,
        new NodeCapability(8, 16 * GiB, usable, AcceleratorKind.None, 0, fabric, "Windows"),
        new NodeLoad(active, queued, 6.0, 0),
        (loaded ?? Array.Empty<string>()).Select(m => new ModelPlacement(m, true, 1)).ToList(),
        NodeHealth.Healthy, DateTimeOffset.UtcNow);

Console.WriteLine("== ModelPlanner ==");
var planner = new ModelPlanner();
var phiMini = ModelCatalog.Find("phi-4-mini")!;
var llama70 = ModelCatalog.Find("llama-3.1-70b")!;

var phiMem = planner.EstimateMemoryBytes(phiMini);
Check(phiMem is > 2 * GiB and < 3 * GiB, $"phi-4-mini Q4 ≈ 2.2 GiB (got {phiMem / GiB:0.0} GiB)");

var llama70Mem = planner.EstimateMemoryBytes(llama70);
Check(llama70Mem > 35 * GiB, $"llama-70B Q4 ≈ 42 GiB (got {llama70Mem / GiB:0.0} GiB)");

var oneNode = Node("n1");
Check(planner.FitsInNode(phiMini, oneNode), "phi-4-mini fits a 16 GB node");
Check(!planner.FitsInNode(llama70, oneNode), "llama-70B does NOT fit a 16 GB node");

Console.WriteLine("== HorizontalScheduler (A) ==");
var horizontal = new HorizontalScheduler();
var opts = new SchedulingOptions();

var busy = Node("busy", active: 5, queued: 3);
var idle = Node("idle", active: 0, queued: 0);
var loaded = Node("loaded", active: 1, queued: 0, loaded: new[] { phiMini.Id });
var best = horizontal.SelectBest(
    new InferenceRequest("r1", phiMini.Id, "hi", null, 128, 0.7f, null),
    phiMini, new[] { busy, idle, loaded }, opts, null, out _);
Check(best?.Id == "loaded", $"prefers node with model already resident (got {best?.Id})");

var best2 = horizontal.SelectBest(
    new InferenceRequest("r2", phiMini.Id, "hi", null, 128, 0.7f, null),
    phiMini, new[] { busy, idle }, opts, null, out _);
Check(best2?.Id == "idle", $"prefers least-loaded node (got {best2?.Id})");

var affA = Node("aff-a", active: 0, queued: 0);
var affB = Node("aff-b", active: 0, queued: 0);
var aff = horizontal.SelectBest(
    new InferenceRequest("r3", phiMini.Id, "hi", null, 128, 0.7f, "sys"),
    phiMini, new[] { affA, affB }, opts, "aff-b", out _);
Check(aff?.Id == "aff-b", $"prefix affinity breaks an equal-load tie (got {aff?.Id})");

Console.WriteLine("== PipelinePartitioner (B) ==");
var partitioner = new PipelinePartitioner();
var manyNodes = Enumerable.Range(0, 100).Select(i => Node($"n{i}")).ToList();
var plan = partitioner.Partition(llama70, manyNodes, opts, 256 * MiB, out var preason);
Check(plan is not null, $"70B partitions across cluster ({preason})");
if (plan is not null)
{
    Check(plan.Stages.Count is >= 4 and <= 8, $"70B uses 4-8 stages (got {plan.Stages.Count})");
    var covered = plan.Stages.Sum(s => s.LayerEnd - s.LayerStart + 1);
    Check(covered == llama70.Layers, $"all {llama70.Layers} layers covered (got {covered})");
    Check(plan.Stages.Select(s => s.NodeId).Distinct().Count() == plan.Stages.Count, "stages on distinct nodes");
    Check(plan.Stages.All(s => s.LayerStart <= s.LayerEnd), "layer ranges are valid");
}

Console.WriteLine("== MeshRouter (A vs B vs unavailable) ==");
var router = new MeshRouter();

var dA = router.Plan(new InferenceRequest("a", phiMini.Id, "hi", null, 128, 0.7f, null), phiMini, manyNodes, opts);
Check(dA.Strategy == SchedulingStrategy.Horizontal, $"small model -> horizontal (got {dA.Strategy})");

var dB = router.Plan(new InferenceRequest("b", llama70.Id, "hi", null, 128, 0.7f, null), llama70, manyNodes, opts);
Check(dB.Strategy == SchedulingStrategy.Pipeline, $"70B -> pipeline (got {dB.Strategy})");

var dU = router.Plan(new InferenceRequest("u", llama70.Id, "hi", null, 128, 0.7f, null), llama70, new[] { oneNode }, opts);
Check(dU.Strategy == SchedulingStrategy.Unavailable, $"70B on a single node -> unavailable (got {dU.Strategy})");

Console.WriteLine("== ThroughputEstimator ==");
var tpsA = ThroughputEstimator.EstimateNodeTokensPerSec(Node("n"), phiMini.ParametersB);
Check(tpsA is > 0 and < 100, $"single-node phi-4-mini est {tpsA:0.#} tok/s is plausible");
if (plan is not null)
{
    var tpsB = ThroughputEstimator.EstimatePipelineTokensPerSec(plan, llama70, manyNodes);
    Check(double.IsFinite(tpsB) && tpsB > 0, $"pipeline 70B est {tpsB:0.##} tok/s is finite and positive");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
