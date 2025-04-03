namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Factory for creating MCP clients
/// </summary>
public static class McpClientFactory
{
  /// <summary>
  /// Creates a new MCP client
  /// </summary>
  /// <param name="options">Options for the client</param>
  /// <returns>The created client</returns>
  public static IMcpClient Create(McpClientOptions options)
  {
    // For now, we'll return a mock implementation
    return new MockMcpClient(options.Id, options.Name);
  }
}
