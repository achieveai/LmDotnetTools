using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.McpTransport;

/// <summary>
/// Extension methods for McpServerBuilder
/// </summary>
public static class McpServerBuilderExtensions
{
  /// <summary>
  /// Configures the server to use a custom transport
  /// </summary>
  /// <param name="builder">The server builder</param>
  /// <param name="transport">The server transport to use</param>
  /// <returns>The server builder for chaining</returns>
  public static IMcpServerBuilder WithServerTransport(this IMcpServerBuilder builder, InMemoryServerTransport transport)
  {
    try
    {
      // Get the ServerBuilder type using reflection
      var serverBuilderType = builder.GetType();
      
      // Try to find a property or field that holds the transport
      var transportProperty = serverBuilderType.GetProperty("Transport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
      if (transportProperty != null && transportProperty.CanWrite)
      {
        transportProperty.SetValue(builder, transport);
        return builder;
      }
      
      var transportField = serverBuilderType.GetField("_transport", BindingFlags.NonPublic | BindingFlags.Instance) ?? 
                          serverBuilderType.GetField("transport", BindingFlags.NonPublic | BindingFlags.Instance);
      if (transportField != null)
      {
        transportField.SetValue(builder, transport);
        return builder;
      }
      
      // Look for a method to set the transport
      var methods = serverBuilderType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
      var setTransportMethod = Array.Find(methods, m => 
        (m.Name.Contains("Transport") || m.Name.Contains("transport")) && 
        m.GetParameters().Length == 1);
      
      if (setTransportMethod != null)
      {
        setTransportMethod.Invoke(builder, new object[] { transport });
        return builder;
      }
      
      // If we can't find a direct way to set the transport, try to use a configuration method
      var configureMethod = Array.Find(methods, m => 
        m.Name.Contains("Configure") && 
        m.GetParameters().Length == 1);
      
      if (configureMethod != null)
      {
        configureMethod.Invoke(builder, new object[] { transport });
        return builder;
      }
      
      // If all else fails, just return the builder and log a warning
      Console.WriteLine("Warning: Could not set transport on server builder. Server may not work as expected.");
      return builder;
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Error setting transport: {ex.Message}");
      return builder;
    }
  }
}
