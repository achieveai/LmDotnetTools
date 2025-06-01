using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;
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

    // Register graph database services
    services.AddScoped<IGraphRepository, GraphRepository>();
    services.AddScoped<IGraphExtractionService, GraphExtractionService>();
    services.AddScoped<IGraphDecisionEngine, GraphDecisionEngine>();
    services.AddScoped<IGraphMemoryService, GraphMemoryService>();

    // Register LLM services
    services.AddLlmServices();

    // Register MCP tools
    services.AddScoped<MemoryMcpTools>();

    return services;
  }

  public static IServiceCollection AddDatabaseServices(
    this IServiceCollection services, 
    IHostEnvironment? environment = null)
  {
    if (environment?.IsDevelopment() == true || environment?.EnvironmentName == "Testing")
    {
      services.AddSingleton<ISqliteSessionFactory, TestSqliteSessionFactory>();
    }
    else
    {
      services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();
    }

    return services;
  }

  public static IServiceCollection AddLlmServices(this IServiceCollection services)
  {
    // Register prompt reader
    services.AddScoped<IPromptReader>(provider =>
    {
      var promptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "graph-extraction.yaml");
      return new PromptReader(promptsPath);
    });

    // Register LLM provider
    services.AddScoped<IAgent>(provider =>
    {
      var memoryOptions = provider.GetRequiredService<IOptions<MemoryServerOptions>>().Value;
      var logger = provider.GetRequiredService<ILogger<IAgent>>();

      if (memoryOptions.LLM.DefaultProvider.ToLower() == "anthropic")
      {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? memoryOptions.LLM.Anthropic.ApiKey;
        if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("${"))
        {
          logger.LogWarning("Anthropic API key not configured. LLM features will be disabled.");
          return new MockAgent("mock-anthropic");
        }
        var client = new AchieveAi.LmDotnetTools.AnthropicProvider.Agents.AnthropicClient(apiKey);
        return new AnthropicAgent("memory-anthropic", client);
      }
      else
      {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? memoryOptions.LLM.OpenAI.ApiKey;
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1";
        if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("${"))
        {
          logger.LogWarning("OpenAI API key not configured. LLM features will be disabled.");
          return new MockAgent("mock-openai");
        }
        var client = new AchieveAi.LmDotnetTools.OpenAIProvider.Agents.OpenClient(apiKey, baseUrl);
        return new OpenClientAgent("memory-openai", client);
      }
    });

    return services;
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
    // Register prompt reader
    services.AddScoped<IPromptReader>(provider =>
    {
      var promptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "graph-extraction.yaml");
      return new PromptReader(promptsPath);
    });

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