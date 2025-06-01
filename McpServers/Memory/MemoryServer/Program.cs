using MemoryServer.Configuration;
using MemoryServer.Models;
using MemoryServer.Tools;
using MemoryServer.Infrastructure;
using MemoryServer.Services;
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
    await RunSseServerAsync(commandLineArgs, memoryOptions, configuration);
}
else
{
    await RunStdioServerAsync(commandLineArgs, memoryOptions, configuration);
}

static async Task RunSseServerAsync(string[] args, MemoryServerOptions options, IConfiguration configuration)
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure logging for development
    builder.Logging.AddConsole();

    // Add routing services (required for UseRouting)
    builder.Services.AddRouting();

    // Add core memory server services
    builder.Services.AddMemoryServerCore(configuration, builder.Environment);

    // Add MCP services for SSE transport
    builder.Services.AddMcpServices(TransportMode.SSE);

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
    
    // Initialize database
    await DatabaseInitializer.InitializeAsync(app.Services);

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

static async Task RunStdioServerAsync(string[] args, MemoryServerOptions options, IConfiguration configuration)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Configure logging for production use
    builder.Logging.AddConsole(consoleLogOptions =>
    {
        // Configure all logs to go to stderr for STDIO transport
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    // Add core memory server services
    builder.Services.AddMemoryServerCore(configuration, builder.Environment);

    // Add MCP services for STDIO transport
    builder.Services.AddMcpServices(TransportMode.STDIO);

    var app = builder.Build();

    // Initialize database
    await DatabaseInitializer.InitializeAsync(app.Services);

    // Initialize STDIO session context from environment variables
    await SessionContextInitializer.InitializeStdioSessionAsync(app.Services);

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
    private readonly IConfiguration _configuration;

    public Startup()
    {
        // Create a minimal configuration for testing
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MemoryServer:Database:ConnectionString"] = "Data Source=:memory:",
            ["MemoryServer:Database:EnableWAL"] = "false",
            ["MemoryServer:Transport:Mode"] = "SSE",
            ["MemoryServer:Transport:Port"] = "0",
            ["MemoryServer:Transport:Host"] = "localhost",
            ["MemoryServer:Transport:EnableCors"] = "true"
        });
        _configuration = builder.Build();
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // Add routing services (required for UseRouting)
        services.AddRouting();

        // Use our extension methods for core services
        services.AddMemoryServerCore(_configuration);

        // Override with test-specific LLM services
        services.AddTestLlmServices();

        // Add MCP services for SSE transport
        services.AddMcpServices(TransportMode.SSE);

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
        // Initialize database using our extension method to avoid deadlock
        app.ApplicationServices.InitializeDatabaseSync();

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