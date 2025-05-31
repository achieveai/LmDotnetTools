using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddLogging();
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

// Add MCP Server with tools
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Initialize database with better error handling using session pattern
try
{
    var sessionFactory = app.Services.GetRequiredService<ISqliteSessionFactory>();
    await sessionFactory.InitializeDatabaseAsync();
    Console.WriteLine("✅ Database initialized successfully with session pattern");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Database initialization failed: {ex.Message}");
    Console.WriteLine($"   Stack trace: {ex.StackTrace}");
    throw;
}

// Health check endpoint (keeping for debugging)
app.MapGet("/health", async (ISqliteSessionFactory sessionFactory) =>
{
    try
    {
        var isHealthy = await sessionFactory.HealthCheckAsync();
        return isHealthy ? Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }) 
                         : Results.Problem("Database health check failed");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Health check error: {ex.Message}");
    }
});

// Server info endpoint (keeping for debugging)
app.MapGet("/info", (IOptions<MemoryServerOptions> options) =>
{
    return Results.Ok(new
    {
        name = "Memory MCP Server with Graph Database",
        version = "2.0.0",
        description = "Intelligent memory management with knowledge graph and integer IDs for better LLM integration",
        protocol = "MCP (Model Context Protocol)",
        features = new[]
        {
            "Integer-based memory IDs",
            "Session isolation (User/Agent/Run)",
            "Session defaults via MCP tools",
            "Vector and full-text search",
            "SQLite with FTS5 support",
            "Knowledge graph with entities and relationships",
            "LLM-powered entity and relationship extraction",
            "Graph traversal and hybrid search",
            "Graph decision engine for intelligent updates",
            "Graph validation and integrity checking",
            "Complete MCP protocol implementation"
        },
        mcp_tools = new[]
        {
            "memory_add - Add new memories from conversation messages or direct content",
            "memory_search - Search for relevant memories using semantic similarity and full-text search",
            "memory_get_all - Retrieve all memories for a specific session",
            "memory_update - Update an existing memory by ID",
            "memory_delete - Delete a memory by ID",
            "memory_delete_all - Delete all memories for a session",
            "memory_get_history - Get memory history for a specific memory ID",
            "memory_get_stats - Provide memory usage statistics and analytics",
            "memory_init_session - Initialize session defaults for the MCP connection lifetime",
            "memory_get_session - Get current session defaults for a connection",
            "memory_update_session - Update session defaults for an existing connection",
            "memory_clear_session - Remove session defaults for a connection",
            "memory_resolve_session - Resolve the effective session context for the current request"
        },
        configuration = new
        {
            database = options.Value.Database.ConnectionString,
            defaultProvider = options.Value.LLM.DefaultProvider
        },
        implementation_status = new
        {
            phase_1_data_models = "✅ Complete",
            phase_2_repository_layer = "✅ Complete", 
            phase_3_extraction_services = "✅ Complete",
            phase_4_decision_engine = "✅ Complete",
            phase_5_integration = "✅ Complete",
            mcp_protocol = "✅ Complete",
            llm_integration = "⚠️ Pending configuration"
        }
    });
});

Console.WriteLine($"🚀 Memory MCP Server starting with MCP protocol support");
Console.WriteLine("📋 Available MCP Tools:");
Console.WriteLine("  Memory Operations:");
Console.WriteLine("    - memory_add: Add new memories from conversation messages or direct content");
Console.WriteLine("    - memory_search: Search for relevant memories using semantic similarity and full-text search");
Console.WriteLine("    - memory_get_all: Retrieve all memories for a specific session");
Console.WriteLine("    - memory_update: Update an existing memory by ID");
Console.WriteLine("    - memory_delete: Delete a memory by ID");
Console.WriteLine("    - memory_delete_all: Delete all memories for a session");
Console.WriteLine("    - memory_get_history: Get memory history for a specific memory ID");
Console.WriteLine("    - memory_get_stats: Provide memory usage statistics and analytics");
Console.WriteLine();
Console.WriteLine("  Session Management:");
Console.WriteLine("    - memory_init_session: Initialize session defaults for the MCP connection lifetime");
Console.WriteLine("    - memory_get_session: Get current session defaults for a connection");
Console.WriteLine("    - memory_update_session: Update session defaults for an existing connection");
Console.WriteLine("    - memory_clear_session: Remove session defaults for a connection");
Console.WriteLine("    - memory_resolve_session: Resolve the effective session context for the current request");
Console.WriteLine();
Console.WriteLine("🔗 Protocol: Model Context Protocol (MCP) via STDIO transport");
Console.WriteLine("💾 Database: SQLite with Database Session Pattern for reliable connection management");
Console.WriteLine("🧠 Features: Integer IDs, Session isolation, Graph database, Full-text search");
Console.WriteLine();

try
{
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Server failed to start: {ex.Message}");
    Console.WriteLine($"   Stack trace: {ex.StackTrace}");
    throw;
} 