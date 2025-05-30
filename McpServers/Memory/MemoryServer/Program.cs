using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddLogging();
builder.Services.AddMemoryCache();

// Configure options from appsettings
builder.Services.Configure<DatabaseOptions>(
    builder.Configuration.GetSection("MemoryServer:Database"));
builder.Services.Configure<MemoryServerOptions>(
    builder.Configuration.GetSection("MemoryServer"));

// Register core infrastructure
builder.Services.AddSingleton<SqliteManager>();
builder.Services.AddSingleton<MemoryIdGenerator>();

// Register session management
builder.Services.AddScoped<ISessionManager, SessionManager>();
builder.Services.AddScoped<ISessionContextResolver, SessionContextResolver>();

// Register memory services
builder.Services.AddScoped<IMemoryRepository, MemoryRepository>();
builder.Services.AddScoped<IMemoryService, MemoryService>();

// TODO: Register LLM providers from workspace (will be implemented in Phase 3)
// For now, we'll focus on basic memory operations without LLM integration

// TODO: Register MCP infrastructure (will be implemented in Phase 2)
// builder.Services.AddMcpMiddleware();

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseCors();

// Initialize database with better error handling
try
{
    var sqliteManager = app.Services.GetRequiredService<SqliteManager>();
    await sqliteManager.InitializeDatabaseAsync();
    Console.WriteLine("✅ Database initialized successfully");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Database initialization failed: {ex.Message}");
    Console.WriteLine($"   Stack trace: {ex.StackTrace}");
    throw;
}

// Health check endpoint
app.MapGet("/health", async (SqliteManager db) =>
{
    try
    {
        var isHealthy = await db.HealthCheckAsync();
        return isHealthy ? Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }) 
                         : Results.Problem("Database health check failed");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Health check error: {ex.Message}");
    }
});

// MCP server endpoint (will be implemented in Phase 2)
app.MapPost("/mcp", async (HttpContext context) =>
{
    // TODO: Implement MCP protocol handling
    return Results.Ok(new { message = "MCP endpoint - coming soon" });
});

// Test endpoint for memory operations
app.MapPost("/test/memory", async (
    IMemoryService memoryService,
    ISessionContextResolver sessionResolver,
    HttpContext context) =>
{
    try
    {
        // Get connection ID from headers or generate one
        var connectionId = context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        
        // Resolve session context
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        Console.WriteLine($"🔍 Testing memory operations for session: {sessionContext}");
        
        // Test adding a memory
        var testContent = $"Test memory added at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
        Console.WriteLine($"➕ Adding memory: {testContent}");
        var memory = await memoryService.AddMemoryAsync(testContent, sessionContext);
        Console.WriteLine($"✅ Added memory with ID: {memory.Id}");
        
        // Test searching
        Console.WriteLine("🔍 Searching for 'test' memories...");
        var searchResults = await memoryService.SearchMemoriesAsync("test", sessionContext, 5);
        Console.WriteLine($"✅ Found {searchResults.Count} search results");
        
        // Test getting all memories
        Console.WriteLine("📋 Getting all memories...");
        var allMemories = await memoryService.GetAllMemoriesAsync(sessionContext, 10);
        Console.WriteLine($"✅ Retrieved {allMemories.Count} total memories");
        
        // Test getting stats
        Console.WriteLine("📊 Getting memory statistics...");
        var stats = await memoryService.GetMemoryStatsAsync(sessionContext);
        Console.WriteLine($"✅ Stats: {stats.TotalMemories} memories, {stats.TotalContentSize} chars");
        
        return Results.Ok(new
        {
            success = true,
            addedMemory = new
            {
                id = memory.Id,
                content = memory.Content,
                sessionContext = memory.GetSessionContext().ToString()
            },
            searchResults = searchResults.Select(m => new { id = m.Id, content = m.Content, score = m.Score }),
            allMemories = allMemories.Select(m => new { id = m.Id, content = m.Content }),
            stats = new
            {
                totalMemories = stats.TotalMemories,
                totalContentSize = stats.TotalContentSize,
                averageContentLength = stats.AverageContentLength
            }
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error testing memory operations: {ex.Message}");
        Console.WriteLine($"   Stack trace: {ex.StackTrace}");
        return Results.Problem($"Error testing memory operations: {ex.Message}");
    }
});

// Debug endpoint for database inspection
app.MapGet("/debug/database", async (SqliteManager sqliteManager) =>
{
    try
    {
        using var connection = await sqliteManager.GetConnectionAsync();
        var debugInfo = new
        {
            tables = new List<object>(),
            ftsTableInfo = new List<object>(),
            ftsContent = new List<object>(),
            memoriesContent = new List<object>(),
            ftsTest = new List<object>()
        };

        // Get all tables
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') ORDER BY name";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                debugInfo.tables.Add(new { name = reader.GetString(0), type = reader.GetString(1) });
            }
        }

        // Get FTS table info
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(memory_fts)";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                debugInfo.ftsTableInfo.Add(new 
                { 
                    cid = reader.GetInt32(0),
                    name = reader.GetString(1), 
                    type = reader.GetString(2),
                    notnull = reader.GetInt32(3),
                    dflt_value = reader.IsDBNull(4) ? null : reader.GetString(4),
                    pk = reader.GetInt32(5)
                });
            }
        }
        catch (Exception ex)
        {
            debugInfo.ftsTableInfo.Add(new { error = ex.Message });
        }

        // Get FTS content
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT memory_id, content FROM memory_fts LIMIT 5";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                debugInfo.ftsContent.Add(new 
                { 
                    memory_id = reader.GetInt32(0),
                    content = reader.GetString(1)
                });
            }
        }
        catch (Exception ex)
        {
            debugInfo.ftsContent.Add(new { error = ex.Message });
        }

        // Get memories content
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, content, user_id FROM memories LIMIT 5";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                debugInfo.memoriesContent.Add(new 
                { 
                    id = reader.GetInt32(0),
                    content = reader.GetString(1),
                    user_id = reader.GetString(2)
                });
            }
        }
        catch (Exception ex)
        {
            debugInfo.memoriesContent.Add(new { error = ex.Message });
        }

        // Test simple FTS query
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT memory_id FROM memory_fts WHERE memory_fts MATCH 'test' LIMIT 3";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                debugInfo.ftsTest.Add(new { memory_id = reader.GetInt32(0) });
            }
        }
        catch (Exception ex)
        {
            debugInfo.ftsTest.Add(new { error = ex.Message });
        }

        return Results.Ok(debugInfo);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Debug error: {ex.Message}");
    }
});

// Server info endpoint
app.MapGet("/info", (IOptions<MemoryServerOptions> options) =>
{
    return Results.Ok(new
    {
        name = "Memory MCP Server",
        version = "1.0.0",
        description = "Intelligent memory management with integer IDs for better LLM integration",
        features = new[]
        {
            "Integer-based memory IDs",
            "Session isolation (User/Agent/Run)",
            "HTTP header session defaults",
            "Vector and full-text search",
            "SQLite with FTS5 support",
            "Basic memory operations (Phase 2)"
        },
        configuration = new
        {
            database = options.Value.Database.ConnectionString,
            defaultProvider = options.Value.LLM.DefaultProvider
        }
    });
});

var port = builder.Configuration.GetValue<int>("MemoryServer:Server:Port", 8080);
var host = builder.Configuration.GetValue<string>("MemoryServer:Server:Host", "localhost");

app.Urls.Add($"http://{host}:{port}");

Console.WriteLine($"🚀 Memory MCP Server starting on http://{host}:{port}");
Console.WriteLine("📋 Available endpoints:");
Console.WriteLine($"  - Health: http://{host}:{port}/health");
Console.WriteLine($"  - Info: http://{host}:{port}/info");
Console.WriteLine($"  - Test Memory: http://{host}:{port}/test/memory");
Console.WriteLine($"  - MCP: http://{host}:{port}/mcp");
Console.WriteLine($"  - Debug Database: http://{host}:{port}/debug/database");
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