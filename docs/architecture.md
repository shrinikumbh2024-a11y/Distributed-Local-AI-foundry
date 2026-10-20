# Architecture

## Goal

One prompt from the UI runs across a LAN of ~100 CPU nodes (Intel i7 / 16 GB),
using whichever strategy fits: horizontal replication (A) or pipeline model
parallelism (B).

## The honest boundary

Microsoft's **Foundry Local** has two faces:

1. **Windows SDK** — `Microsoft.AI.Foundry.Local(.WinML)`. In-process, single
   device, ONNX Runtime GenAI, CPU/GPU/NPU. This is our *per-node engine*.
2. **Foundry Local on Azure Local** — multi-node Kubernetes + vLLM replicas +
   inference-aware routing (EPP) + model parallelism. This is the *enterprise
   target*, not an ad-hoc home LAN.

So `Mesh.Agent` wraps (1), and `Mesh.Orchestrator` provides the distribution
that (1) lacks. The `InferenceBackend` boundary in the code (via `IRuntime`) is
what lets the same UI/orchestrator later drive (2) instead.

## Components

```
UI (Blazor Server)
   │  channel<string> (in-process)
Orchestrator ── MeshRouter ── HorizontalScheduler (A) / PipelinePartitioner (B)
   │  raw WebSocket / JSON envelope protocol
   ├─ Agent (node 1) ── InferenceEngine ── IRuntime (Mock | FoundryLocal)
   ├─ Agent (node 2) ...
   └─ Agent (node N)
```

- **Control plane** (low bandwidth): register, heartbeat, capability, model
  placement.
- **Data plane** (high bandwidth): token deltas (A) and activation payloads (B).
  Both ride the same WebSocket but are clearly separated message types.

## Scheduling math

### Memory estimate

`bytes ≈ params × (quantBits/8) × 1.20 + kvBudget`.

- 7B Q4 ≈ 4.2 GB → fits a 10 GB node → **A**.
- 70B Q4 ≈ 42 GB → 4 nodes → **B**.

### A — horizontal

Score each healthy node that fits the model and pick the minimum:
`10·active + 20·queued + 40·(model cold) − 25·(prefix affinity)`. This spreads
requests and reuses KV-cached prefixes (cheap, high-value).

### B — pipeline

Greedy contiguous layer assignment. Nodes are ordered by fabric group (same
switch first, to minimize slow hops) then by usable memory. Each stage gets a
`[LayerStart, LayerEnd]` range and an estimated memory footprint.

### Throughput estimate

- Single node: `cores × 0.8 × (7 / paramsB)` unless the node reports a measured
  benchmark.
- Pipeline: `1 / (Σ per-stage compute + hops × activationSize / bandwidth)`,
  which captures the real bottlenecks (slowest stage + LAN transfer). This is
  why B only wins for large models.

## Scale roadmap

1. **LAN** (this repo): mDNS-less explicit registration + heartbeats, whole-request
   routing + shared model cache + prefix affinity.
2. **Office**: a "leader" node, join tokens, NFS/SMB model cache, mTLS.
3. **Datacenter**: replace the agent/WebSocket backend with Foundry Local on
   Azure Local (vLLM replicas, EPP routing, real model parallelism). The UI and
   `Mesh.Core` scheduler API stay the same; only the backend swaps.

## Security & resilience (production checklist)

- mTLS between agents and orchestrator; per-node join tokens.
- Heartbeats → offline detection (implemented); re-registration on reconnect.
- In-flight request failover + graceful shard migration for B.
- Bandwidth probe at join time to veto splitting over slow links.
- Per-node model cache dedup (peer fetch) so a model downloads once.
