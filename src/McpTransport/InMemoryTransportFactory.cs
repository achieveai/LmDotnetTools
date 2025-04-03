using System.IO.Pipelines;

namespace AchieveAi.LmDotnetTools.McpTransport;

/// <summary>
/// Factory for creating paired in-memory transports
/// </summary>
public static class InMemoryTransportFactory
{
  /// <summary>
  /// Creates a pair of in-memory transports for client and server
  /// </summary>
  /// <param name="name">Name for the transport pair</param>
  /// <returns>A tuple containing the client and server transports</returns>
  public static (InMemoryClientTransport ClientTransport, InMemoryServerTransport ServerTransport) CreateTransportPair(string name)
  {
    // Create pipes for client-to-server and server-to-client communication
    var clientToServerPipe = new Pipe();
    var serverToClientPipe = new Pipe();

    // Create client transport that reads from server-to-client pipe and writes to client-to-server pipe
    var clientTransport = new InMemoryClientTransport(
      serverToClientPipe,
      clientToServerPipe,
      $"{name}:Client");

    // Create server transport that reads from client-to-server pipe and writes to server-to-client pipe
    var serverTransport = new InMemoryServerTransport(
      clientToServerPipe,
      serverToClientPipe,
      $"{name}:Server");

    return (clientTransport, serverTransport);
  }
}
