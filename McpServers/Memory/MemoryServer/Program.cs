using System.CommandLine;
using System.Text;
using MemoryServer.Configuration;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;
using MemoryServer.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

// Load environment variables from .env file early in startup
EnvironmentHelper.LoadEnvIfNeeded();

var commandLineArgs = Environment.GetCommandLineArgs();

// Check if this is a CLI command
if (commandLineArgs.Length > 1 && commandLineArgs[1] == "generate-token")
{
    // Handle CLI commands
    var rootCommand = new RootCommand("Memory MCP Server");
    rootCommand.AddCommand(TokenGeneratorCommand.CreateCommand());

    return await rootCommand.InvokeAsync([.. commandLineArgs.Skip(1)]);
}

// Parse command line arguments for transport mode
var transportMode = TransportMode.SSE; // Default to SSE
if (commandLineArgs.Contains("--stdio"))
{
    transportMode = TransportMode.STDIO;
}

// Load configuration
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", true)
    .AddJsonFile(
        $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json",
        true
    )
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

return 0;

static async Task RunSseServerAsync(string[] args, MemoryServerOptions options, IConfiguration configuration)
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure logging for development
    _ = builder.Logging.AddConsole();

    // Add routing services (required for UseRouting)
    _ = builder.Services.AddRouting();

    // Add HTTP context accessor for JWT claims
    _ = builder.Services.AddHttpContextAccessor();

    // Add core memory server services
    _ = builder.Services.AddMemoryServerCore(configuration, builder.Environment);

    // Configure JWT authentication for SSE transport only
    _ = builder.Services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
    _ = builder.Services.AddScoped<ITokenService, TokenService>();

    var jwtOptions = new JwtOptions();
    configuration.GetSection("Jwt").Bind(jwtOptions);

    if (jwtOptions.IsValid())
    {
        _ = builder
            .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                }
            );

        _ = builder.Services.AddAuthorization();
    }

    // Add MCP services for SSE transport
    _ = builder.Services.AddMcpServices(TransportMode.SSE);

    // Configure CORS for testing
    _ = builder.Services.AddCors(corsOptions =>
        corsOptions.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader())
    );

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
    _ = app.UseCors();

    // Add routing first
    _ = app.UseRouting();

    // Add authentication and authorization middleware (after UseRouting)
    _ = app.UseAuthentication();
    _ = app.UseAuthorization();

    // Add middleware to extract URL parameters and headers for session context
    _ = app.Use(
        async (context, next) =>
        {
            try
            {
                // Extract URL parameters
                var queryParameters = context.Request.Query.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

                // Extract HTTP headers
                var headers = context
                    .Request.Headers.Where(h => h.Key.StartsWith("X-Memory-"))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

                // Initialize SSE session context if we have relevant parameters or headers
                if (queryParameters.Any(kvp => kvp.Key.EndsWith("_id")) || headers.Count != 0)
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
        }
    );

    // Add basic health check endpoint for testing
    _ = app.MapGet("/health", () => "OK");

    // Map MCP endpoints (this creates the /sse endpoint)
    _ = app.MapMcp();

    appLogger.LogInformation("🌐 Memory MCP Server configured for SSE transport");
}

static async Task RunStdioServerAsync(string[] args, MemoryServerOptions options, IConfiguration configuration)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Configure logging for production use
    _ = builder.Logging.AddConsole(consoleLogOptions =>
        // Configure all logs to go to stderr for STDIO transport
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace
    );

    // Add core memory server services
    _ = builder.Services.AddMemoryServerCore(configuration, builder.Environment);

    // Configure JWT services for STDIO (but no authentication middleware)
    _ = builder.Services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
    _ = builder.Services.AddScoped<ITokenService, TokenService>();

    // Add HTTP context accessor for consistency (even though not used in STDIO)
    _ = builder.Services.AddHttpContextAccessor();

    // Add MCP services for STDIO transport
    _ = builder.Services.AddMcpServices(TransportMode.STDIO);

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
        _ = builder.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MemoryServer:Database:ConnectionString"] = "Data Source=:memory:",
                ["MemoryServer:Database:EnableWAL"] = "false",
                ["MemoryServer:Transport:Mode"] = "SSE",
                ["MemoryServer:Transport:Port"] = "0",
                ["MemoryServer:Transport:Host"] = "localhost",
                ["MemoryServer:Transport:EnableCors"] = "true",
                ["Jwt:Secret"] = "test-secret-key-that-is-at-least-256-bits-long-for-hmac-sha256-algorithm-testing",
                ["Jwt:Issuer"] = "MemoryServer",
                ["Jwt:Audience"] = "MemoryServer",
                ["Jwt:ExpirationMinutes"] = "60",
            }
        );
        _configuration = builder.Build();
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // Add routing services (required for UseRouting)
        _ = services.AddRouting();

        // Add HTTP context accessor for JWT claims
        _ = services.AddHttpContextAccessor();

        // Use our extension methods for core services
        _ = services.AddMemoryServerCore(_configuration);

        // Configure JWT services for testing (but no authentication middleware for tests)
        _ = services.Configure<JwtOptions>(_configuration.GetSection("Jwt"));
        _ = services.AddScoped<ITokenService, TokenService>();

        // Override with test-specific LLM services
        _ = services.AddTestLlmServices();

        // Add MCP services for SSE transport
        _ = services.AddMcpServices(TransportMode.SSE);

        // Configure CORS for testing
        _ = services.AddCors(corsOptions =>
            corsOptions.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader())
        );
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Initialize database using our extension method to avoid deadlock
        app.ApplicationServices.InitializeDatabaseSync();

        // Configure CORS
        _ = app.UseCors();

        // Map MCP endpoints (this creates the /sse endpoint)
        _ = app.UseRouting();

        _ = app.UseEndpoints(endpoints =>
        {
            // Add basic health check endpoint for testing
            _ = endpoints.MapGet("/health", () => "OK");

            _ = endpoints.MapMcp();
        });
    }
}
