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

// Register graph database services
builder.Services.AddScoped<IGraphRepository, GraphRepository>();
builder.Services.AddScoped<IGraphExtractionService, GraphExtractionService>();
builder.Services.AddScoped<IGraphDecisionEngine, GraphDecisionEngine>();
builder.Services.AddScoped<IGraphMemoryService, GraphMemoryService>();

// TODO: Register LLM providers (will be configured based on settings)
// For now, we'll focus on basic memory and graph operations without LLM integration

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

// Memory API endpoints
app.MapPost("/api/memory/add", async (
    IMemoryService memoryService,
    ISessionContextResolver sessionResolver,
    HttpContext context) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<AddMemoryRequest>();
        if (request == null || string.IsNullOrEmpty(request.Content))
        {
            return Results.BadRequest("Content is required");
        }

        var connectionId = request.ConnectionId ?? context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var memory = await memoryService.AddMemoryAsync(request.Content, sessionContext);
        
        return Results.Ok(new { success = true, memory_id = memory.Id, content = memory.Content });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error adding memory: {ex.Message}");
    }
});

app.MapPost("/api/memory/search", async (
    IMemoryService memoryService,
    ISessionContextResolver sessionResolver,
    HttpContext context) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<SearchMemoryRequest>();
        if (request == null || string.IsNullOrEmpty(request.Query))
        {
            return Results.BadRequest("Query is required");
        }

        var connectionId = request.ConnectionId ?? context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var results = await memoryService.SearchMemoriesAsync(request.Query, sessionContext, request.Limit ?? 10);
        
        return Results.Ok(new { 
            success = true, 
            results = results.Select(m => new { id = m.Id, content = m.Content, score = m.Score }) 
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error searching memories: {ex.Message}");
    }
});

// Graph API endpoints
app.MapGet("/api/graph/entities", async (
    IGraphRepository graphRepository,
    ISessionContextResolver sessionResolver,
    HttpContext context,
    int limit = 20,
    int offset = 0) =>
{
    try
    {
        var connectionId = context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var entities = await graphRepository.GetEntitiesAsync(sessionContext, limit, offset);
        
        return Results.Ok(new { 
            success = true, 
            entities = entities.Select(e => new { 
                id = e.Id, 
                name = e.Name, 
                type = e.Type, 
                confidence = e.Confidence,
                aliases = e.Aliases,
                source_memory_ids = e.SourceMemoryIds
            }) 
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error getting entities: {ex.Message}");
    }
});

app.MapGet("/api/graph/relationships", async (
    IGraphRepository graphRepository,
    ISessionContextResolver sessionResolver,
    HttpContext context,
    int limit = 20,
    int offset = 0) =>
{
    try
    {
        var connectionId = context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var relationships = await graphRepository.GetRelationshipsAsync(sessionContext, limit, offset);
        
        return Results.Ok(new { 
            success = true, 
            relationships = relationships.Select(r => new { 
                id = r.Id, 
                source = r.Source, 
                relationship_type = r.RelationshipType, 
                target = r.Target, 
                confidence = r.Confidence,
                temporal_context = r.TemporalContext,
                source_memory_id = r.SourceMemoryId
            }) 
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error getting relationships: {ex.Message}");
    }
});

app.MapPost("/api/graph/traverse", async (
    IGraphMemoryService graphMemoryService,
    ISessionContextResolver sessionResolver,
    HttpContext context) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<TraverseGraphRequest>();
        if (request == null || string.IsNullOrEmpty(request.EntityName))
        {
            return Results.BadRequest("Entity name is required");
        }

        var connectionId = request.ConnectionId ?? context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var result = await graphMemoryService.GetRelatedEntitiesAsync(
            request.EntityName, 
            sessionContext, 
            request.MaxDepth ?? 3, 
            request.RelationshipTypes);
        
        return Results.Ok(new { 
            success = true, 
            start_entity = result.StartEntity != null ? new { 
                id = result.StartEntity.Id, 
                name = result.StartEntity.Name, 
                type = result.StartEntity.Type 
            } : null,
            entities = result.AllEntities.Select(e => new { 
                id = e.Id, 
                name = e.Name, 
                type = e.Type, 
                confidence = e.Confidence 
            }),
            relationships = result.AllRelationships.Select(r => new { 
                id = r.Id, 
                source = r.Source, 
                relationship_type = r.RelationshipType, 
                target = r.Target, 
                confidence = r.Confidence 
            }),
            max_depth_reached = result.MaxDepthReached,
            traversal_time_ms = result.TraversalTimeMs
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error traversing graph: {ex.Message}");
    }
});

app.MapPost("/api/graph/search", async (
    IGraphMemoryService graphMemoryService,
    ISessionContextResolver sessionResolver,
    HttpContext context) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<HybridSearchRequest>();
        if (request == null || string.IsNullOrEmpty(request.Query))
        {
            return Results.BadRequest("Query is required");
        }

        var connectionId = request.ConnectionId ?? context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var results = await graphMemoryService.SearchMemoriesAsync(
            request.Query, 
            sessionContext, 
            request.UseGraphTraversal ?? true, 
            request.MaxResults ?? 20);
        
        return Results.Ok(new { 
            success = true, 
            traditional_results = results.TraditionalResults.Select(m => new { 
                id = m.Id, 
                content = m.Content 
            }),
            graph_results = results.GraphResults.Select(m => new { 
                id = m.Id, 
                content = m.Content 
            }),
            combined_results = results.CombinedResults.Select(r => new { 
                memory = new { id = r.Memory.Id, content = r.Memory.Content },
                traditional_score = r.TraditionalScore,
                graph_score = r.GraphScore,
                combined_score = r.CombinedScore,
                source = r.Source.ToString(),
                matching_entities = r.MatchingEntities,
                matching_relationships = r.MatchingRelationships
            }),
            relevant_entities = results.RelevantEntities.Select(e => new { 
                id = e.Id, 
                name = e.Name, 
                type = e.Type 
            }),
            relevant_relationships = results.RelevantRelationships.Select(r => new { 
                id = r.Id, 
                source = r.Source, 
                relationship_type = r.RelationshipType, 
                target = r.Target 
            }),
            search_time_ms = results.SearchTimeMs
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error performing hybrid search: {ex.Message}");
    }
});

app.MapGet("/api/graph/statistics", async (
    IGraphMemoryService graphMemoryService,
    ISessionContextResolver sessionResolver,
    HttpContext context) =>
{
    try
    {
        var connectionId = context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var stats = await graphMemoryService.GetGraphStatisticsAsync(sessionContext);
        
        return Results.Ok(new { 
            success = true, 
            entity_count = stats.EntityCount,
            relationship_count = stats.RelationshipCount,
            unique_relationship_types = stats.UniqueRelationshipTypes,
            top_relationship_types = stats.TopRelationshipTypes,
            top_connected_entities = stats.TopConnectedEntities
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error getting graph statistics: {ex.Message}");
    }
});

app.MapPost("/api/graph/rebuild", async (
    IGraphMemoryService graphMemoryService,
    ISessionContextResolver sessionResolver,
    HttpContext context) =>
{
    try
    {
        var connectionId = context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var summary = await graphMemoryService.RebuildGraphAsync(sessionContext);
        
        return Results.Ok(new { 
            success = true, 
            memories_processed = summary.MemoriesProcessed,
            entities_created = summary.EntitiesCreated,
            relationships_created = summary.RelationshipsCreated,
            entities_merged = summary.EntitiesMerged,
            relationships_merged = summary.RelationshipsMerged,
            rebuild_time_ms = summary.RebuildTimeMs,
            errors = summary.Errors,
            warnings = summary.Warnings,
            started_at = summary.StartedAt,
            completed_at = summary.CompletedAt
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error rebuilding graph: {ex.Message}");
    }
});

app.MapGet("/api/graph/validate", async (
    IGraphMemoryService graphMemoryService,
    ISessionContextResolver sessionResolver,
    HttpContext context) =>
{
    try
    {
        var connectionId = context.Request.Headers["X-Connection-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        var sessionContext = await sessionResolver.ResolveSessionContextAsync(connectionId);
        
        var result = await graphMemoryService.ValidateGraphIntegrityAsync(sessionContext);
        
        return Results.Ok(new { 
            success = true, 
            is_valid = result.IsValid,
            entities_validated = result.EntitiesValidated,
            relationships_validated = result.RelationshipsValidated,
            errors = result.Errors.Select(e => new {
                type = e.Type.ToString(),
                description = e.Description,
                severity = e.Severity.ToString(),
                entity_id = e.EntityId,
                relationship_id = e.RelationshipId
            }),
            warnings = result.Warnings.Select(w => new {
                type = w.Type.ToString(),
                description = w.Description,
                entity_id = w.EntityId,
                relationship_id = w.RelationshipId
            }),
            orphaned_entities = result.OrphanedEntities.Select(e => new { 
                id = e.Id, 
                name = e.Name, 
                type = e.Type 
            }),
            broken_relationships = result.BrokenRelationships.Select(r => new { 
                id = r.Id, 
                source = r.Source, 
                relationship_type = r.RelationshipType, 
                target = r.Target 
            }),
            validation_time_ms = result.ValidationTimeMs
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error validating graph: {ex.Message}");
    }
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
        name = "Memory MCP Server with Graph Database",
        version = "2.0.0",
        description = "Intelligent memory management with knowledge graph and integer IDs for better LLM integration",
        features = new[]
        {
            "Integer-based memory IDs",
            "Session isolation (User/Agent/Run)",
            "HTTP header session defaults",
            "Vector and full-text search",
            "SQLite with FTS5 support",
            "Knowledge graph with entities and relationships",
            "LLM-powered entity and relationship extraction",
            "Graph traversal and hybrid search",
            "Graph decision engine for intelligent updates",
            "Graph validation and integrity checking",
            "Complete REST API for memory and graph operations"
        },
        api_endpoints = new
        {
            memory = new[]
            {
                "POST /api/memory/add - Add a new memory",
                "POST /api/memory/search - Search memories"
            },
            graph = new[]
            {
                "GET /api/graph/entities - Get entities from knowledge graph",
                "GET /api/graph/relationships - Get relationships from knowledge graph",
                "POST /api/graph/traverse - Traverse graph starting from an entity",
                "POST /api/graph/search - Hybrid search using traditional and graph methods",
                "GET /api/graph/statistics - Get graph statistics",
                "POST /api/graph/rebuild - Rebuild graph from existing memories",
                "GET /api/graph/validate - Validate graph integrity"
            },
            utility = new[]
            {
                "GET /health - Health check",
                "GET /info - Server information",
                "POST /test/memory - Test memory operations",
                "GET /debug/database - Database inspection"
            }
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
            api_endpoints = "✅ Complete",
            llm_integration = "⚠️ Pending configuration"
        }
    });
});

var port = builder.Configuration.GetValue<int>("MemoryServer:Server:Port", 8080);
var host = builder.Configuration.GetValue<string>("MemoryServer:Server:Host", "localhost");

app.Urls.Add($"http://{host}:{port}");

Console.WriteLine($"🚀 Memory MCP Server with Graph Database starting on http://{host}:{port}");
Console.WriteLine("📋 Available endpoints:");
Console.WriteLine($"  - Health: http://{host}:{port}/health");
Console.WriteLine($"  - Info: http://{host}:{port}/info");
Console.WriteLine($"  - Test Memory: http://{host}:{port}/test/memory");
Console.WriteLine($"  - Debug Database: http://{host}:{port}/debug/database");
Console.WriteLine();
Console.WriteLine("🧠 Memory API:");
Console.WriteLine($"  - Add Memory: POST http://{host}:{port}/api/memory/add");
Console.WriteLine($"  - Search Memories: POST http://{host}:{port}/api/memory/search");
Console.WriteLine();
Console.WriteLine("🕸️ Graph API:");
Console.WriteLine($"  - Get Entities: GET http://{host}:{port}/api/graph/entities");
Console.WriteLine($"  - Get Relationships: GET http://{host}:{port}/api/graph/relationships");
Console.WriteLine($"  - Traverse Graph: POST http://{host}:{port}/api/graph/traverse");
Console.WriteLine($"  - Hybrid Search: POST http://{host}:{port}/api/graph/search");
Console.WriteLine($"  - Graph Statistics: GET http://{host}:{port}/api/graph/statistics");
Console.WriteLine($"  - Rebuild Graph: POST http://{host}:{port}/api/graph/rebuild");
Console.WriteLine($"  - Validate Graph: GET http://{host}:{port}/api/graph/validate");
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