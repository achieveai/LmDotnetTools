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

    // Register LLM provider following established patterns
    services.AddScoped<IAgent>(provider =>
    {
      var memoryOptions = provider.GetRequiredService<IOptions<MemoryServerOptions>>().Value;
      var logger = provider.GetRequiredService<ILogger<IAgent>>();

      if (memoryOptions.LLM.DefaultProvider.ToLower() == "anthropic")
      {
        return CreateAnthropicAgent(memoryOptions, logger);
      }
      else
      {
        return CreateOpenAIAgent(memoryOptions, logger);
      }
    });

    return services;
  }

  /// <summary>
  /// Gets API key from environment variables with fallback options
  /// Following the pattern used throughout the codebase
  /// </summary>
  private static string GetApiKeyFromEnv(string primaryKey, string[]? fallbackKeys = null, string defaultValue = "")
  {
    var apiKey = Environment.GetEnvironmentVariable(primaryKey);
    if (!string.IsNullOrEmpty(apiKey))
    {
      return apiKey;
    }

    if (fallbackKeys != null)
    {
      foreach (var fallbackKey in fallbackKeys)
      {
        apiKey = Environment.GetEnvironmentVariable(fallbackKey);
        if (!string.IsNullOrEmpty(apiKey))
        {
          return apiKey;
        }
      }
    }

    return defaultValue;
  }

  /// <summary>
  /// Gets API base URL from environment variables with fallback options
  /// Following the pattern used throughout the codebase
  /// </summary>
  private static string GetApiBaseUrlFromEnv(string primaryKey, string[]? fallbackKeys = null, string defaultValue = "https://api.openai.com/v1")
  {
    var baseUrl = Environment.GetEnvironmentVariable(primaryKey);
    if (!string.IsNullOrEmpty(baseUrl))
    {
      return baseUrl;
    }

    if (fallbackKeys != null)
    {
      foreach (var fallbackKey in fallbackKeys)
      {
        baseUrl = Environment.GetEnvironmentVariable(fallbackKey);
        if (!string.IsNullOrEmpty(baseUrl))
        {
          return baseUrl;
        }
      }
    }

    return defaultValue;
  }

  /// <summary>
  /// Creates an Anthropic agent using consistent API key patterns
  /// </summary>
  private static IAgent CreateAnthropicAgent(MemoryServerOptions memoryOptions, ILogger logger)
  {
    // Use consistent pattern: Environment variable first, then config fallback
    var apiKey = GetApiKeyFromEnv(
      "ANTHROPIC_API_KEY", 
      fallbackKeys: null, 
      defaultValue: memoryOptions.LLM.Anthropic.ApiKey ?? "");
    
    if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("${"))
    {
      logger.LogWarning("Anthropic API key not configured. Using MockAgent for LLM features.");
      return new MockAgent("mock-anthropic");
    }
    
    try
    {
      var client = new AnthropicClient(apiKey);
      logger.LogInformation("Anthropic LLM provider initialized successfully");
      return new AnthropicAgent("memory-anthropic", client);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to initialize Anthropic client. Using MockAgent.");
      return new MockAgent("mock-anthropic");
    }
  }

  /// <summary>
  /// Creates an OpenAI agent using consistent API key and base URL patterns
  /// </summary>
  private static IAgent CreateOpenAIAgent(MemoryServerOptions memoryOptions, ILogger logger)
  {
    // Use consistent pattern: Environment variable first, then config fallback
    var apiKey = GetApiKeyFromEnv(
      "OPENAI_API_KEY",
      fallbackKeys: new[] { "LLM_API_KEY" },
      defaultValue: memoryOptions.LLM.OpenAI.ApiKey ?? "");
    
    var baseUrl = GetApiBaseUrlFromEnv(
      "OPENAI_BASE_URL",
      fallbackKeys: new[] { "OPENAI_API_URL", "LLM_API_BASE_URL" },
      defaultValue: "https://api.openai.com/v1");
    
    if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("${"))
    {
      logger.LogWarning("OpenAI API key not configured. Using MockAgent for LLM features.");
      return new MockAgent("mock-openai");
    }
    
    try
    {
      var client = new OpenClient(apiKey, baseUrl);
      logger.LogInformation("OpenAI LLM provider initialized successfully with base URL: {BaseUrl}", baseUrl);
      return new OpenClientAgent("memory-openai", client);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to initialize OpenAI client. Using MockAgent.");
      return new MockAgent("mock-openai");
    }
  }

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