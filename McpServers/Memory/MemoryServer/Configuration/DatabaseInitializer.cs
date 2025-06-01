using MemoryServer.Infrastructure;

namespace MemoryServer.Configuration;

public static class DatabaseInitializer
{
  public static async Task InitializeAsync(IServiceProvider services)
  {
    var logger = services.GetRequiredService<ILogger<Program>>();
    
    try
    {
      var sessionFactory = services.GetRequiredService<ISqliteSessionFactory>();
      await sessionFactory.InitializeDatabaseAsync();
      logger.LogInformation("Database initialized successfully");
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Database initialization failed: {Message}", ex.Message);
      throw;
    }
  }
} 