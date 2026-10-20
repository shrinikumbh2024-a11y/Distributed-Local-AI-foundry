namespace Mesh.Orchestrator.Services;

/// <summary>Periodically marks nodes whose heartbeats stopped as offline.</summary>
public sealed class NodeMonitor : BackgroundService
{
    private readonly ClusterState _state;
    private readonly ILogger<NodeMonitor> _logger;

    public NodeMonitor(ClusterState state, ILogger<NodeMonitor> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            _state.MarkStale(TimeSpan.FromSeconds(25));
        }
    }
}
