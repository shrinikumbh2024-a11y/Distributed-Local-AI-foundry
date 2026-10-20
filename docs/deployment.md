# Deployment

## 1. Wire the real Foundry Local runtime

The agent runs `MockRuntime` by default so it works anywhere. To run real
models:

1. Install the SDK (Windows 11 24H2+, .NET 9+ recommended for WinML):
   ```powershell
   dotnet add src/Mesh.Agent package Microsoft.AI.Foundry.Local.WinML
   # or the portable package: Microsoft.AI.Foundry.Local
   ```
2. Add the adapter project and reference it:
   ```powershell
   dotnet sln add src/Mesh.Runtime.FoundryLocal/Mesh.Runtime.FoundryLocal.csproj
   dotnet add src/Mesh.Agent reference src/Mesh.Runtime.FoundryLocal/Mesh.Runtime.FoundryLocal.csproj
   ```
3. In `src/Mesh.Agent/Program.cs`, replace
   `new MockRuntime(options.TokensPerSec)` with `new FoundryLocalRuntime("mesh-agent")`.

Verify member names against the installed SDK — `Mesh.Runtime.FoundryLocal`
was authored offline against the published quickstart and is not
compile-verified.

## 2. Rollout to 100 nodes

On each machine:

```powershell
dotnet run --project src/Mesh.Agent -- \
  --orchestrator ws://<orchestrator-ip>:5198/agents \
  --name node-<NN> --fabric <switch-a|switch-b|...>
```

- `--fabric` should reflect the physical switch/rack so pipeline stages land on
  fast neighbors.
- Pin the agent as a service (systemd / NSSM / Task Scheduler) for auto-restart.

Start the orchestrator once:

```powershell
dotnet run --project src/Mesh.Orchestrator --urls http://0.0.0.0:5198
```

Open `http://<orchestrator-ip>:5198`.

## 3. Sizing guidance (i7 / 16 GB, 1 GbE)

| Model (Q4) | Strategy | Notes |
|---|---|---|
| Phi-4-mini 3.8B, Mistral/Llama 7–8B | A | ~6–10 tok/s/node; 100 nodes ≈ 100× throughput |
| Qwen2.5-14B | A | still fits; slower per node |
| Qwen2.5-32B / Llama-3.1-70B | B | 4–8 nodes; bandwidth-bound, only for large models |

Do **not** pipeline-split models that fit one node — over Ethernet it is slower.
