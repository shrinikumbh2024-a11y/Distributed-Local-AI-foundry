# Local AI Mesh

A distributed local-AI application in **C# / .NET 8**, built on the **Microsoft
Foundry Local** SDK. It pools the compute of ~100 LAN machines (each an Intel i7
with 16 GB RAM) into a single inference cluster with a Harness-style web UI
(chat, model manager, cluster topology, telemetry).

It implements **both** distribution strategies you asked for, chosen
automatically per request:

| Strategy | When | How |
|---|---|---|
| **A — Horizontal** | model fits one node | whole model on one node; requests spread across nodes for throughput |
| **B — Pipeline** | model too big for any node | contiguous layer ranges split across nodes; activations stream stage-to-stage |

> **Important reality check.** Foundry Local's Windows SDK is a *single-node,
> in-process* runtime — it does not do cross-machine inference by itself. This
> project is the missing **orchestration layer**: a per-node agent that runs the
> Foundry Local SDK, plus a scheduler/router that makes 100 agents behave as one
> cluster. Microsoft's own multi-node path (Foundry Local on Azure Local /
> Kubernetes) is the enterprise target this design can grow into; see
> `docs/architecture.md`.

## Projects

```
src/
  Mesh.Abstractions        DTOs, interfaces, wire protocol (no deps)
  Mesh.Core                the brain: planner + scheduler A + partitioner B + router
  Mesh.Runtime             IRuntime + deterministic MockRuntime + InferenceEngine
  Mesh.Agent               per-node worker (WebSocket client + capability probe)
  Mesh.Orchestrator        ASP.NET Core + Blazor UI + agent WebSocket server
  Mesh.Simulator           100-node A/B planning demo (no network needed)
  Mesh.Runtime.FoundryLocal  OPT-IN Foundry Local adapter (needs NuGet network)
tests/
  Mesh.Core.Tests          dependency-free test harness (no xUnit — offline)
```

## Why no external NuGet packages?

The core solution builds against the **.NET 8 shared framework only** (ASP.NET
Core, Blazor Server, `System.Net.WebSockets`, `System.Threading.Channels`). This
keeps it fully air-gapped and trivially deployable. The Foundry Local SDK is the
one optional external package, isolated in `Mesh.Runtime.FoundryLocal`.

## Run it

### 1. Planning demo (no agents, instant)

```powershell
dotnet run --project src/Mesh.Simulator
```

Simulates 100 nodes and prints the A/B decisions, pipeline stage layouts, and
load-balancing behavior.

### 2. Tests

```powershell
dotnet run --project tests/Mesh.Core.Tests
```

### 3. Live cluster

Start the orchestrator (UI at http://localhost:5198):

```powershell
dotnet run --project src/Mesh.Orchestrator --urls http://0.0.0.0:5198
```

Start agents (one per machine — repeat on each node):

```powershell
dotnet run --project src/Mesh.Agent -- \
  --orchestrator ws://<orchestrator-ip>:5198/agents \
  --name node-01 --fabric switch-a
```

Open the UI, pick a model, and chat. The decision badge on each reply shows
whether it used **A** or **B** and why.

> Inside the DeepSeek Harness sandbox, `dotnet` needs its caches redirected —
> use `./build.ps1` (or set the env vars it sets) instead of a bare `dotnet`.

### 4. One-click packaged app (self-contained, no .NET needed)

```powershell
dotnet publish src/Mesh.Orchestrator -c Release -r win-x64 --self-contained -o dist/orchestrator
dotnet publish src/Mesh.Agent       -c Release -r win-x64 --self-contained -o dist/agent
dotnet publish src/Mesh.Simulator   -c Release -r win-x64 --self-contained -o dist/simulator
```

Then double-click `dist/start-demo.bat` — it launches the UI, 4 simulated
i7/16 GB nodes, and opens your browser. See `dist/README.md` for details.

## Default model assumptions

The built-in catalog (`Mesh.Core/ModelCatalog.cs`) is a planning catalog of
quantized models with approximate layer counts. A 16 GB node has ~10 GB usable
after reserving 6 GB for the OS:

- **≤ ~14B Q4** → fits one node → **A** (horizontal).
- **~30B+ Q4** → needs 4–8 nodes → **B** (pipeline).
- A 70B Q4 (≈42 GB) splits across ~4 nodes.

Swap in the live Foundry Local catalog when you wire the real SDK.

## Honest limits (read before you trust the numbers)

- **CPU-only + 1 GbE** is the hard part. Pipeline (B) over Ethernet only pays off
  for models whose per-token compute dominates the inter-node transfer cost.
  Splitting a 7B model across machines is *slower* than one machine.
- The `MockRuntime` simulates token throughput so the whole stack runs anywhere;
  replace it with `FoundryLocalRuntime` for real inference.
- Pipeline **scheduling/partitioning is fully implemented and tested**; pipeline
  **byte-level execution** is a simulated control flow in this MVP, because
  Foundry Local's in-process SDK runs whole models, not layer slices. Real B on
  Foundry targets Azure Local multi-node (vLLM / model parallelism) or a sharded
  ONNX graph. See `docs/architecture.md`.

## Docs

- [`docs/architecture.md`](docs/architecture.md) — design, scheduling math, scale roadmap
- [`docs/deployment.md`](docs/deployment.md) — wiring the Foundry Local SDK + 100-node rollout

## License

MIT — see [LICENSE](LICENSE).
