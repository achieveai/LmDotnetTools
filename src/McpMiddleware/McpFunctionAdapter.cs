using ModelContextProtocol;
using ModelContextProtocol.Client;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Adapter for converting MCP clients to function delegates compatible with FunctionCallMiddleware
/// </summary>
public static class McpFunctionAdapter
{
  /// <summary>
  /// Creates function delegates for all tools available in the provided MCP clients
  /// </summary>
  /// <param name="mcpClients">Dictionary of MCP clients with their respective IDs</param>
  /// <returns>A dictionary mapping function names to handler delegates</returns>
  public static IDictionary<string, Func<string, Task<string>>> CreateFunctionMap(
    Dictionary<string, IMcpClient> mcpClients)
  {
    var functionMap = new Dictionary<string, Func<string, Task<string>>>();
    
    foreach (var (clientId, client) in mcpClients)
    {
      // Get all available tools from the client
      var tools = client.ListToolsAsync().GetAwaiter().GetResult();
      
      foreach (var tool in tools)
      {
        // Register a function for each tool that will handle:
        // - Argument parsing
        // - Tool calling
        // - Response formatting
        functionMap[tool.Name] = async (argsJson) => 
        {
          try 
          {
            // Parse arguments
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) 
              ?? new Dictionary<string, object?>();
            
            // Call the MCP tool
            var response = await client.CallToolAsync(tool.Name, args);
            
            // Format the response to match expected format
            string result = string.Join(Environment.NewLine, 
              response.Content != null
                ? response.Content
                    .Where(c => c?.Type == "text")
                    .Select(c => c?.Text ?? string.Empty)
                : Array.Empty<string>());
                
            return result;
          }
          catch (Exception ex)
          {
            // Handle MCP-specific errors
            return $"Error executing MCP tool {tool.Name}: {ex.Message}";
          }
        };
      }
    }
    
    return functionMap;
  }
}
