using System.IO.Pipelines;

namespace AchieveAi.LmDotnetTools.McpTransport;

/// <summary>
/// In-memory transport for MCP client using pipelines
/// </summary>
public class InMemoryClientTransport
{
  private readonly Pipe _inputPipe;
  private readonly Pipe _outputPipe;
  private readonly string _clientName;

  /// <summary>
  /// Creates a new instance of the InMemoryClientTransport
  /// </summary>
  /// <param name="inputPipe">Pipe for input</param>
  /// <param name="outputPipe">Pipe for output</param>
  /// <param name="clientName">Name of the client</param>
  public InMemoryClientTransport(Pipe inputPipe, Pipe outputPipe, string clientName)
  {
    _inputPipe = inputPipe;
    _outputPipe = outputPipe;
    _clientName = clientName;
  }

  /// <summary>
  /// Gets the name of the transport
  /// </summary>
  public string Name => $"InMemory:{_clientName}";

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
