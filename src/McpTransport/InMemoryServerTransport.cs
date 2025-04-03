using System.IO.Pipelines;

namespace AchieveAi.LmDotnetTools.McpTransport;

/// <summary>
/// In-memory transport for MCP server using pipelines
/// </summary>
public class InMemoryServerTransport
{
  private readonly Pipe _inputPipe;
  private readonly Pipe _outputPipe;
  private readonly string _serverName;

  /// <summary>
  /// Creates a new instance of the InMemoryServerTransport
  /// </summary>
  /// <param name="inputPipe">Pipe for input</param>
  /// <param name="outputPipe">Pipe for output</param>
  /// <param name="serverName">Name of the server</param>
  public InMemoryServerTransport(Pipe inputPipe, Pipe outputPipe, string serverName)
  {
    _inputPipe = inputPipe;
    _outputPipe = outputPipe;
    _serverName = serverName;
  }

  /// <summary>
  /// Gets the name of the transport
  /// </summary>
  public string Name => $"InMemory:{_serverName}";

  /// <summary>
  /// Gets the reader for the transport
  /// </summary>
  public PipeReader GetReader() => _inputPipe.Reader;

  /// <summary>
  /// Gets the writer for the transport
  /// </summary>
  public PipeWriter GetWriter() => _outputPipe.Writer;

  /// <summary>
  /// Disposes the transport
  /// </summary>
  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
