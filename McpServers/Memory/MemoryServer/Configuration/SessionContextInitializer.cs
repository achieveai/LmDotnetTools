using MemoryServer.Services;

namespace MemoryServer.Configuration;

public static class SessionContextInitializer
{
  public static async Task InitializeStdioSessionAsync(IServiceProvider services)
  {
    var logger = services.GetRequiredService<ILogger<Program>>();
    
    try
    {
      using var scope = services.CreateScope();
      var sessionInitializer = scope.ServiceProvider.GetRequiredService<TransportSessionInitializer>();
      var sessionDefaults = await sessionInitializer.InitializeStdioSessionAsync();
      
      if (sessionDefaults != null && sessionInitializer.ValidateSessionContext(sessionDefaults))
      {
        logger.LogInformation("STDIO session context initialized: {SessionDefaults}", sessionDefaults);
      }
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to initialize STDIO session context from environment variables");
    }
  }
} 