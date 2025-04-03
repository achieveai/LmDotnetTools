namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Options for creating an MCP client
/// </summary>
public class McpClientOptions
{
  /// <summary>
  /// Gets or sets the ID of the client
  /// </summary>
  public string Id { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the name of the client
  /// </summary>
  public string Name { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the transport type
  /// </summary>
  public string TransportType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the transport options
    /// </summary>
    public Dictionary<string, object?> TransportOptions { get; set; } = new();
}
