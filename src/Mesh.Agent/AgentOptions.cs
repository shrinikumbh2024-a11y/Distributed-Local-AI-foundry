namespace Mesh.Agent;

public sealed record AgentOptions(
    Uri OrchestratorUri,
    string NodeId,
    string NodeName,
    string Host,
    int Port,
    string FabricGroup,
    long ReservedSystemBytes,
    double TokensPerSec,
    int Cores,
    double RamGib)
{
    public static AgentOptions Parse(string[] args)
    {
        var orchestrator = GetArg(args, "--orchestrator") ?? "ws://127.0.0.1:5198/agents";
        var nodeId = GetArg(args, "--id") ?? $"node-{Guid.NewGuid():N}"[..8];
        var name = GetArg(args, "--name") ?? nodeId;
        var host = GetArg(args, "--host") ?? "127.0.0.1";
        var port = int.TryParse(GetArg(args, "--port"), out var p) ? p : 0;
        var fabric = GetArg(args, "--fabric") ?? "switch-a";
        var tps = double.TryParse(GetArg(args, "--tps"), out var t) ? t : 6.0;
        var cores = int.TryParse(GetArg(args, "--cores"), out var c) ? c : 0;
        var ramGib = double.TryParse(GetArg(args, "--ram-gb"), out var r) ? r : 0.0;

        return new AgentOptions(
            new Uri(orchestrator),
            nodeId,
            name,
            host,
            port,
            fabric,
            ReservedSystemBytes: 6L * 1024 * 1024 * 1024,
            TokensPerSec: tps,
            Cores: cores,
            RamGib: ramGib);
    }

    private static string? GetArg(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
