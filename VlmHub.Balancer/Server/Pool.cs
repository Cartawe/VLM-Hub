namespace VlmHub.Balancer.Server;

internal sealed class ServerPool : IDisposable
{
    public IReadOnlyList<Runtime> Servers { get; }

    public ServerPool(IReadOnlyList<Runtime> servers)
    {
        Servers = servers;
    }

    public bool AnyOnlineSupports(string model) =>
        Servers.Any(server => server.IsOnlineAndSupports(model));

    public Runtime? SelectBest(string model)
    {
        return Servers
            .Where(server => server.CanDispatch(model))
            .OrderBy(server =>
                server.HasActiveModel(model)
                    ? 0
                    : 1)
            .ThenBy(server => server.GetLastAssignedAt())
            .FirstOrDefault();
    }

    public bool AnyOnline => Servers.Any(server => server.IsCurrentlyOnline());

    public void Dispose()
    {
        foreach (var server in Servers)
        {
            server.Dispose();
        }
    }
}
