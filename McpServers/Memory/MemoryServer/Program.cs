using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging for production use
builder.Logging.AddConsole();

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
    // Use test session factory for development and testing
    builder.Services.AddSingleton<ISqliteSessionFactory, TestSqliteSessionFactory>();
}
else
{
    // Use production session factory for production
    builder.Services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();
}

// Register core infrastructure
builder.Services.AddSingleton<MemoryIdGenerator>();

// Register session management
builder.Services.AddScoped<ISessionManager, SessionManager>();
builder.Services.AddScoped<ISessionContextResolver, SessionContextResolver>();

// Register memory services
builder.Services.AddScoped<IMemoryRepository, MemoryRepository>();
builder.Services.AddScoped<IMemoryService, MemoryService>();

// Register graph database services
builder.Services.AddScoped<IGraphRepository, GraphRepository>();
builder.Services.AddScoped<IGraphExtractionService, GraphExtractionService>();
builder.Services.AddScoped<IGraphDecisionEngine, GraphDecisionEngine>();
builder.Services.AddScoped<IGraphMemoryService, GraphMemoryService>();

// Register MCP tools
builder.Services.AddScoped<MemoryMcpTools>();
builder.Services.AddScoped<SessionMcpTools>();

// TODO: Register LLM providers (will be configured based on settings)
// For now, we'll focus on basic memory and graph operations without LLM integration

// Add MCP Server with STDIO transport (reverting from SSE due to method not available)
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

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

try
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("🚀 Memory MCP Server starting with STDIO transport");
    
    await app.RunAsync();
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Server failed to start: {Message}", ex.Message);
    throw;
} 