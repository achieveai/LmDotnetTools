using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;
using MemoryServer.Utils;
using Microsoft.Extensions.Options;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Utils;


namespace MemoryServer.Configuration;

public static class ServiceCollectionExtensions
{
  public static IServiceCollection AddMemoryServerCore(
    this IServiceCollection services, 
    IConfiguration configuration, 
    IHostEnvironment? environment = null)
  {
    // Add memory cache
    services.AddMemoryCache();

    // Configure options from appsettings
    services.Configure<DatabaseOptions>(
      configuration.GetSection("MemoryServer:Database"));
    services.Configure<MemoryServerOptions>(
      configuration.GetSection("MemoryServer"));

    // Register Database Session Pattern infrastructure
    services.AddDatabaseServices(environment);

    // Register core infrastructure
    services.AddSingleton<MemoryIdGenerator>();

    // Register session management services
    services.AddScoped<ISessionContextResolver, SessionContextResolver>();
    services.AddScoped<ISessionManager, SessionManager>();
    services.AddScoped<TransportSessionInitializer>();

    // Register memory services
    services.AddScoped<IMemoryRepository, MemoryRepository>();
    services.AddScoped<IMemoryService, MemoryService>();

    // Register embedding services for vector storage
    services.AddScoped<IEmbeddingManager, EmbeddingManager>();

    // Register graph database services
    services.AddScoped<IGraphRepository, GraphRepository>();
    services.AddScoped<IGraphExtractionService, GraphExtractionService>();
    services.AddScoped<IGraphDecisionEngine, GraphDecisionEngine>();
    services.AddScoped<IGraphMemoryService, GraphMemoryService>();

    // Register unified search engine for Phase 6
    services.AddScoped<IUnifiedSearchEngine, UnifiedSearchEngine>();

    // Register reranking engine for Phase 7
    services.AddScoped<IRerankingEngine, RerankingEngine>();

    // Register deduplication engine for Phase 8
    services.AddScoped<IDeduplicationEngine, DeduplicationEngine>();

    // Register result enrichment engine for Phase 8
    services.AddScoped<IResultEnricher, ResultEnricher>();

    // Register LLM services
    services.AddLlmServices();

    // Register LmConfig integration
    services.AddScoped<ILmConfigService, LmConfigService>();

    // Register MCP tools
    services.AddScoped<MemoryMcpTools>();

    return services;
  }

  public static IServiceCollection AddDatabaseServices(
    this IServiceCollection services, 
    IHostEnvironment? environment = null)
  {
    // For MCP server, always use production database services
    // Test services should only be used in actual test projects
    services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();

    return services;
  }

  public static IServiceCollection AddLlmServices(this IServiceCollection services)
  {
    // Register prompt reader that loads from embedded resources with file system fallback
    services.AddScoped<IPromptReader, EmbeddedPromptReader>();

    // Note: IAgent is now provided through ILmConfigService.CreateAgentAsync() 
    // instead of direct dependency injection for better model selection and provider management

    return services;
  }

  // Note: Agent creation methods removed as they are now handled by ILmConfigService
  // which provides better model selection, provider management, and configuration

  public static IServiceCollection AddMcpServices(
    this IServiceCollection services, 
    TransportMode transportMode)
  {
    var mcpBuilder = services.AddMcpServer();

    if (transportMode == TransportMode.SSE)
    {
      mcpBuilder.WithHttpTransport();
    }
    else
    {
      mcpBuilder.WithStdioServerTransport();
    }

    mcpBuilder.WithToolsFromAssembly(typeof(MemoryMcpTools).Assembly);

    return services;
  }

  public static IServiceCollection AddTestLlmServices(this IServiceCollection services)
  {
    // Register prompt reader that loads from embedded resources with file system fallback
    services.AddScoped<IPromptReader, EmbeddedPromptReader>();

    // Use mock agent for testing
    services.AddScoped<IAgent>(provider => new MockAgent("test-agent"));

    return services;
  }

  public static void InitializeDatabaseSync(this IServiceProvider services)
  {
    var sessionFactory = services.GetRequiredService<ISqliteSessionFactory>();
    
    // For testing scenarios, we need to initialize synchronously
    // Use a dedicated thread to avoid deadlock issues
    var initTask = Task.Run(async () => await sessionFactory.InitializeDatabaseAsync());
    initTask.Wait();
  }
} 