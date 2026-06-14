namespace RefractorForge.Collab;

/// <summary>
/// The relay's handle to one connected client: a one-way sink it can push lines into.
/// Implemented by the TCP transport (writes to a socket) and by the in-process loopback
/// transport (hands the line straight to the client object) used in tests.
/// </summary>
public interface IClientEndpoint
{
    string ClientId { get; }
    void Deliver(string line);
    /// <summary>Forcibly close this client's connection (for an admin kick). No-op for in-process endpoints.</summary>
    void Close() { }
}

/// <summary>The client's handle to the relay: where it pushes its own lines.</summary>
public interface IServerEndpoint
{
    void Receive(string clientId, string line);
}

/// <summary>
/// In-process, synchronous transport. Connecting a client wires the relay and client directly
/// together so a message send resolves immediately on the same thread. This makes multi-client
/// convergence fully deterministic and trivially testable with no sockets or threads.
/// </summary>
public sealed class LoopbackLink : IClientEndpoint, IServerEndpoint
{
    public string ClientId { get; }
    private readonly RelayServer _server;
    private CollabClient? _client;

    public LoopbackLink(RelayServer server, string clientId)
    {
        _server = server;
        ClientId = clientId;
    }

    /// <summary>Bind the client side and register with the relay (triggers initial state sync).</summary>
    public void Attach(CollabClient client)
    {
        _client = client;
        _server.Register(this);
    }

    // Relay -> client
    public void Deliver(string line) => _client?.OnLine(line);

    // Client -> relay
    public void Receive(string clientId, string line) => _server.OnLine(clientId, line);
}
