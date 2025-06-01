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

var commandLineArgs = Environment.GetCommandLineArgs();

// Parse command line arguments for transport mode
var transportMode = TransportMode.SSE; // Default to SSE
if (commandLineArgs.Contains("--stdio"))
{
    transportMode = TransportMode.STDIO;
}

// Load configuration
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var memoryOptions = new MemoryServerOptions();
configuration.GetSection("MemoryServer").Bind(memoryOptions);
memoryOptions.Transport.Mode = transportMode;

Console.WriteLine($"🚀 Starting Memory MCP Server with {transportMode} transport");

if (transportMode == TransportMode.SSE)
{
    await RunSseServerAsync(commandLineArgs, memoryOptions);
}
else
{
    await RunStdioServerAsync(commandLineArgs, memoryOptions);
}

static async Task RunSseServerAsync(string[] args, MemoryServerOptions options)
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure logging for development
    builder.Logging.AddConsole();

    // Add services to the container
    builder.Services.AddMemoryCache();
    
    // Add routing services (required for UseRouting)
    builder.Services.AddRouting();

    // Configure options from appsettings
    builder.Services.Configure<DatabaseOptions>(
        builder.Configuration.GetSection("MemoryServer:Database"));
    builder.Services.Configure<MemoryServerOptions>(
        builder.Configuration.GetSection("MemoryServer"));

    // Register Database Session Pattern infrastructure
    if (builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing")
    {
        builder.Services.AddSingleton<ISqliteSessionFactory, TestSqliteSessionFactory>();
    }
    else
    {
        builder.Services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();
    }

    // Register core infrastructure
    builder.Services.AddSingleton<MemoryIdGenerator>();

    // Register session management services
    builder.Services.AddScoped<ISessionContextResolver, SessionContextResolver>();
    builder.Services.AddScoped<ISessionManager, SessionManager>();
    builder.Services.AddScoped<TransportSessionInitializer>();

    // Register memory services
    builder.Services.AddScoped<IMemoryRepository, MemoryRepository>();
    builder.Services.AddScoped<IMemoryService, MemoryService>();

    // Register graph database services
    builder.Services.AddScoped<IGraphRepository, GraphRepository>();
    builder.Services.AddScoped<IGraphExtractionService, GraphExtractionService>();
    builder.Services.AddScoped<IGraphDecisionEngine, GraphDecisionEngine>();
    builder.Services.AddScoped<IGraphMemoryService, GraphMemoryService>();

    // Register LLM services
    builder.Services.AddScoped<IPromptReader>(provider =>
    {
        var promptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "graph-extraction.yaml");
        return new PromptReader(promptsPath);
    });

    // Register LLM provider based on configuration
    builder.Services.AddScoped<IAgent>(provider =>
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

    // Register MCP tools
    builder.Services.AddScoped<MemoryMcpTools>();

    // Add MCP Server with HTTP transport (SSE)
    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly(typeof(MemoryMcpTools).Assembly);

    // Configure CORS for testing
    builder.Services.AddCors(corsOptions =>
    {
        corsOptions.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    var app = builder.Build();
    await ConfigureSseApplication(app);
    await app.RunAsync();
}

static async Task ConfigureSseApplication(WebApplication app)
{
    var appLogger = app.Services.GetRequiredService<ILogger<Program>>();
    
    // Initialize database with better error handling using session pattern
    try
    {
        var sessionFactory = app.Services.GetRequiredService<ISqliteSessionFactory>();
        await sessionFactory.InitializeDatabaseAsync();
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "Database initialization failed: {Message}", ex.Message);
        throw;
    }

    // Configure CORS
    app.UseCors();

    // Add middleware to extract URL parameters and headers for session context
    app.Use(async (context, next) =>
    {
        try
        {
            // Extract URL parameters
            var queryParameters = context.Request.Query
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

            // Extract HTTP headers
            var headers = context.Request.Headers
                .Where(h => h.Key.StartsWith("X-Memory-"))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

            // Initialize SSE session context if we have relevant parameters or headers
            if (queryParameters.Any(kvp => kvp.Key.EndsWith("_id")) || headers.Any())
            {
                var sessionInitializer = context.RequestServices.GetRequiredService<TransportSessionInitializer>();
                var sessionDefaults = await sessionInitializer.InitializeSseSessionAsync(queryParameters, headers);
                
                if (sessionDefaults != null && sessionInitializer.ValidateSessionContext(sessionDefaults))
                {
                    appLogger.LogInformation("SSE session context initialized: {SessionDefaults}", sessionDefaults);
                }
            }
        }
        catch (Exception ex)
        {
            appLogger.LogWarning(ex, "Failed to initialize SSE session context from request");
        }

        await next();
    });

    // Add basic health check endpoint for testing
    app.MapGet("/health", () => "OK");
    
    // Map MCP endpoints (this creates the /sse endpoint)
    app.MapMcp();

    appLogger.LogInformation("🌐 Memory MCP Server configured for SSE transport");
}

static async Task RunStdioServerAsync(string[] args, MemoryServerOptions options)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Configure logging for production use
    builder.Logging.AddConsole(consoleLogOptions =>
    {
        // Configure all logs to go to stderr for STDIO transport
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    // Add services to the container
    builder.Services.AddMemoryCache();

    // Configure options from appsettings
    builder.Services.Configure<DatabaseOptions>(
        builder.Configuration.GetSection("MemoryServer:Database"));
    builder.Services.Configure<MemoryServerOptions>(
        builder.Configuration.GetSection("MemoryServer"));

    // Register Database Session Pattern infrastructure
    if (builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing")
    {
        builder.Services.AddSingleton<ISqliteSessionFactory, TestSqliteSessionFactory>();
    }
    else
    {
        builder.Services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();
    }

    // Register core infrastructure
    builder.Services.AddSingleton<MemoryIdGenerator>();

    // Register session management services
    builder.Services.AddScoped<ISessionContextResolver, SessionContextResolver>();
    builder.Services.AddScoped<ISessionManager, SessionManager>();
    builder.Services.AddScoped<TransportSessionInitializer>();

    // Register memory services
    builder.Services.AddScoped<IMemoryRepository, MemoryRepository>();
    builder.Services.AddScoped<IMemoryService, MemoryService>();

    // Register graph database services
    builder.Services.AddScoped<IGraphRepository, GraphRepository>();
    builder.Services.AddScoped<IGraphExtractionService, GraphExtractionService>();
    builder.Services.AddScoped<IGraphDecisionEngine, GraphDecisionEngine>();
    builder.Services.AddScoped<IGraphMemoryService, GraphMemoryService>();

    // Register LLM services
    builder.Services.AddScoped<IPromptReader>(provider =>
    {
        var promptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "graph-extraction.yaml");
        return new PromptReader(promptsPath);
    });

    // Register LLM provider based on configuration
    builder.Services.AddScoped<IAgent>(provider =>
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

    // Register MCP tools
    builder.Services.AddScoped<MemoryMcpTools>();

    // Add MCP Server with STDIO transport
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(MemoryMcpTools).Assembly);

    var app = builder.Build();

    // Initialize database with better error handling using session pattern
    try
    {
        var sessionFactory = app.Services.GetRequiredService<ISqliteSessionFactory>();
        await sessionFactory.InitializeDatabaseAsync();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Database initialization failed: {Message}", ex.Message);
        throw;
    }

    // Initialize STDIO session context from environment variables
    try
    {
        using var scope = app.Services.CreateScope();
        var sessionInitializer = scope.ServiceProvider.GetRequiredService<TransportSessionInitializer>();
        var sessionDefaults = await sessionInitializer.InitializeStdioSessionAsync();
        
        if (sessionDefaults != null && sessionInitializer.ValidateSessionContext(sessionDefaults))
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("STDIO session context initialized: {SessionDefaults}", sessionDefaults);
        }
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Failed to initialize STDIO session context from environment variables");
    }

    try
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("🖥️ Memory MCP Server starting with STDIO transport");
        await app.RunAsync();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "STDIO server failed to start: {Message}", ex.Message);
        throw;
    }
}

public partial class Program { }

// Startup class for WebApplicationFactory testing
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Add services to the container
        services.AddMemoryCache();
        
        // Add routing services (required for UseRouting)
        services.AddRouting();

        // Configure options with default values for testing
        services.Configure<DatabaseOptions>(options =>
        {
            options.ConnectionString = ":memory:";
            options.EnableWAL = false;
        });
        
        services.Configure<MemoryServerOptions>(options =>
        {
            options.Transport.Mode = TransportMode.SSE;
            options.Transport.Port = 0;
            options.Transport.Host = "localhost";
            options.Transport.EnableCors = true;
            options.Transport.AllowedOrigins = new[] { "http://localhost:3000", "http://127.0.0.1:3000" };
        });

        // Register Database Session Pattern infrastructure for testing
        services.AddSingleton<ISqliteSessionFactory, TestSqliteSessionFactory>();

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

        // Register LLM services with mock agent for testing
        services.AddScoped<IPromptReader>(provider =>
        {
            var promptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "graph-extraction.yaml");
            return new PromptReader(promptsPath);
        });

        // Use mock agent for testing
        services.AddScoped<IAgent>(provider => new MockAgent("test-agent"));

        // Register MCP tools
        services.AddScoped<MemoryMcpTools>();

        // Add MCP Server with HTTP transport (SSE)
        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(MemoryMcpTools).Assembly);

        // Configure CORS for testing
        services.AddCors(corsOptions =>
        {
            corsOptions.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // Initialize database
        var sessionFactory = app.ApplicationServices.GetRequiredService<ISqliteSessionFactory>();
        sessionFactory.InitializeDatabaseAsync().GetAwaiter().GetResult();

        // Configure CORS
        app.UseCors();

        // Map MCP endpoints (this creates the /sse endpoint)
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            // Add basic health check endpoint for testing
            endpoints.MapGet("/health", () => "OK");
            
            endpoints.MapMcp();
        });
    }
}

// Simple mock agent for when API keys are not configured
public class MockAgent : IAgent
{
    private readonly string _name;
    
    public MockAgent(string name)
    {
        _name = name;
    }
    
    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new TextMessage 
        { 
            Text = $"Mock response from {_name}. LLM features are disabled - API key not configured.",
            Role = Role.Assistant,
            FromAgent = _name
        };
        return Task.FromResult<IEnumerable<IMessage>>(new[] { response });
    }
} 