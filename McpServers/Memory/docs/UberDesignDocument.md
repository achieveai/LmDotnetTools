# Memory MCP Server - Uber Design Document

## Executive Summary

This document presents the comprehensive technical design for a Memory MCP (Model Context Protocol) server implemented in C# (.NET 9.0). The system leverages SQLite as the primary storage solution with sqlite-vec extension for vector operations and FTS5 for full-text search, providing sophisticated memory management capabilities through the MCP protocol while integrating with existing workspace LLM providers. The system uses integer IDs for optimal LLM integration and supports flexible session default management.

## 1. System Architecture Overview

### 1.1 High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Memory MCP Server                        │
├─────────────────────────────────────────────────────────────┤
│                    MCP Protocol Layer                       │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ SSE Server  │  │ Tool Router │  │   Tool Registry     │  │
│  │ Transport   │  │             │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Memory Core Layer                        │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Memory    │  │ AsyncMemory │  │   Session           │  │
│  │   Manager   │  │   Manager   │  │  Management         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Intelligence Layer                           │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │    Fact     │  │   Memory    │  │      LLM            │  │
│  │ Extraction  │  │  Decision   │  │   Provider          │  │
│  │   Engine    │  │   Engine    │  │   Factory           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Storage Layer                                │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   SQLite    │  │ sqlite-vec  │  │      FTS5           │  │
│  │   Manager   │  │  Vector     │  │   Full-Text         │  │
│  │             │  │  Storage    │  │    Search           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Infrastructure Layer                         │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │Configuration│  │  Logging &  │  │     Error           │  │
│  │  Manager    │  │ Monitoring  │  │   Handling          │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### 1.2 Core Design Principles

**SQLite-First Architecture**: Leverage SQLite's advanced capabilities including vector search (sqlite-vec), full-text indexing (FTS5), and graph traversals (recursive CTEs) for all storage needs.

**MCP Protocol Compliance**: Full implementation of MCP server specification with SSE transport, proper tool discovery, and standardized error handling.

**Workspace Integration**: Seamless integration with existing OpenAI and Anthropic providers in the workspace, avoiding duplication of LLM infrastructure.

**Session Isolation**: Strict multi-tenant data separation using SQL-based session boundaries and access control.

**Production Readiness**: Comprehensive error handling, monitoring, performance optimization, and operational considerations.

**LLM-Optimized Design**: Use integer IDs instead of UUIDs for better LLM comprehension, token efficiency, and easier reference in prompts.

**Flexible Session Management**: Support for session defaults via HTTP headers and session initialization to reduce parameter repetition.

## 2. Component Design

### 2.1 MCP Protocol Layer

#### 2.1.1 SSE Server Transport
**Purpose**: Implements MCP server-sent events transport protocol with session default support

**Key Components**:
- `McpSseServer`: Main server class implementing MCP SSE protocol
- `ConnectionManager`: Manages client connections and disconnections
- `MessageHandler`: Processes incoming MCP messages and routes to appropriate handlers
- `HeaderProcessor`: Extracts and validates session defaults from HTTP headers

**Implementation Pattern**:
```csharp
public class McpSseServer : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<McpSseServer> _logger;
    private readonly ConcurrentDictionary<string, ClientConnection> _connections;
    private readonly IHeaderProcessor _headerProcessor;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Initialize SSE server
        // Register MCP message handlers
        // Start listening for connections
    }

    public async Task HandleConnectionAsync(HttpContext context)
    {
        // Extract session defaults from HTTP headers
        var sessionDefaults = await _headerProcessor.ProcessHeadersAsync(context.Request.Headers);
        
        // Establish SSE connection with session context
        var connection = new ClientConnection(context, sessionDefaults);
        
        // Handle MCP handshake
        // Process incoming messages
    }
}
```

**HTTP Header Processing**:
```csharp
public class HeaderProcessor : IHeaderProcessor
{
    private readonly ILogger<HeaderProcessor> _logger;

    public async Task<SessionDefaults> ProcessHeadersAsync(IHeaderDictionary headers)
    {
        var defaults = new SessionDefaults();

        if (headers.TryGetValue("X-Memory-User-ID", out var userId))
        {
            defaults.UserId = ValidateIdentifier(userId.ToString());
        }

        if (headers.TryGetValue("X-Memory-Agent-ID", out var agentId))
        {
            defaults.AgentId = ValidateIdentifier(agentId.ToString());
        }

        if (headers.TryGetValue("X-Memory-Run-ID", out var runId))
        {
            defaults.RunId = ValidateIdentifier(runId.ToString());
        }

        if (headers.TryGetValue("X-Memory-Session-Metadata", out var metadata))
        {
            defaults.Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadata.ToString());
        }

        return defaults;
    }
}
```

#### 2.1.2 Tool Registry and Router
**Purpose**: Manages MCP tool registration and request routing with session context resolution

**Key Components**:
- `ToolRegistry`: Maintains registry of available MCP tools
- `ToolRouter`: Routes tool invocation requests to appropriate handlers
- `ToolDescriptor`: Metadata for tool discovery and validation
- `SessionContextResolver`: Resolves session parameters using precedence rules

**Tool Registration Pattern**:
```csharp
[McpTool("memory_add")]
public class AddMemoryTool : IMcpTool
{
    private readonly ISessionContextResolver _sessionResolver;
    
    public string Name => "memory_add";
    public string Description => "Adds new memories from conversation messages";
    
    public async Task<object> ExecuteAsync(
        JsonElement parameters, 
        CancellationToken cancellationToken)
    {
        // Resolve session context using precedence rules
        var sessionContext = await _sessionResolver.ResolveSessionAsync(parameters);
        
        // Tool implementation with resolved session
    }
}
```

**Session Context Resolution**:
```csharp
public class SessionContextResolver : ISessionContextResolver
{
    public async Task<SessionContext> ResolveSessionAsync(
        JsonElement parameters, 
        SessionDefaults? connectionDefaults = null,
        SessionDefaults? sessionDefaults = null)
    {
        // Precedence: Explicit Parameters > HTTP Headers > Session Init > System Defaults
        var context = new SessionContext();

        // 1. Start with system defaults
        context.UserId = _systemDefaults.DefaultUserId;
        context.AgentId = _systemDefaults.DefaultAgentId;
        context.RunId = _systemDefaults.DefaultRunId;

        // 2. Apply session initialization defaults
        if (sessionDefaults != null)
        {
            context.UserId = sessionDefaults.UserId ?? context.UserId;
            context.AgentId = sessionDefaults.AgentId ?? context.AgentId;
            context.RunId = sessionDefaults.RunId ?? context.RunId;
        }

        // 3. Apply HTTP header defaults
        if (connectionDefaults != null)
        {
            context.UserId = connectionDefaults.UserId ?? context.UserId;
            context.AgentId = connectionDefaults.AgentId ?? context.AgentId;
            context.RunId = connectionDefaults.RunId ?? context.RunId;
        }

        // 4. Apply explicit parameters (highest precedence)
        if (parameters.TryGetProperty("user_id", out var userIdParam))
        {
            context.UserId = userIdParam.GetString();
        }
        if (parameters.TryGetProperty("agent_id", out var agentIdParam))
        {
            context.AgentId = agentIdParam.GetString();
        }
        if (parameters.TryGetProperty("run_id", out var runIdParam))
        {
            context.RunId = runIdParam.GetString();
        }

        return context;
    }
}
```

### 2.2 Memory Core Layer

#### 2.2.1 Memory Manager
**Purpose**: Primary orchestration layer for memory operations with integer ID management

**Key Responsibilities**:
- Coordinate between fact extraction, decision making, and storage
- Enforce session isolation and access control
- Handle both synchronous and asynchronous operations
- Manage caching and performance optimization
- Generate and manage integer IDs for memories

**Core Interface**:
```csharp
public interface IMemoryManager
{
    Task<MemoryAddResult> AddMemoryAsync(
        AddMemoryRequest request, 
        CancellationToken cancellationToken = default);
    
    Task<MemorySearchResult> SearchMemoryAsync(
        SearchMemoryRequest request, 
        CancellationToken cancellationToken = default);
    
    Task<MemoryUpdateResult> UpdateMemoryAsync(
        UpdateMemoryRequest request, 
        CancellationToken cancellationToken = default);
    
    Task<MemoryDeleteResult> DeleteMemoryAsync(
        DeleteMemoryRequest request, 
        CancellationToken cancellationToken = default);
        
    Task<SessionInitResult> InitializeSessionAsync(
        SessionInitRequest request,
        CancellationToken cancellationToken = default);
}
```

**Integer ID Management**:
```csharp
public class MemoryIdGenerator
{
    private readonly SqliteManager _sqliteManager;

    public async Task<int> GenerateNextIdAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        
        command.CommandText = @"
            INSERT INTO memory_id_sequence DEFAULT VALUES;
            SELECT last_insert_rowid();";
        
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }
}
```

#### 2.2.2 Session Management
**Purpose**: Handles multi-tenant session isolation and default management

**Key Features**:
- Session context validation and enforcement
- SQL-based access control using WHERE clauses
- Session metadata tracking and audit trail
- Support for user, agent, and run-based sessions
- Session default storage and retrieval

**Session Context Model**:
```csharp
public record SessionContext
{
    public string? UserId { get; init; }
    public string? AgentId { get; init; }
    public string? RunId { get; init; }
    public SessionType Type { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

public record SessionDefaults
{
    public string? UserId { get; set; }
    public string? AgentId { get; set; }
    public string? RunId { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum SessionType
{
    User,
    Agent,
    Run
}
```

**Session Default Storage**:
```csharp
public class SessionManager : ISessionManager
{
    private readonly ConcurrentDictionary<string, SessionDefaults> _sessionDefaults = new();

    public async Task<SessionInitResult> InitializeSessionAsync(
        string connectionId,
        SessionInitRequest request,
        CancellationToken cancellationToken = default)
    {
        var defaults = new SessionDefaults
        {
            UserId = request.UserId,
            AgentId = request.AgentId,
            RunId = request.RunId,
            Metadata = request.Metadata
        };

        _sessionDefaults[connectionId] = defaults;

        return new SessionInitResult
        {
            Success = true,
            ActiveDefaults = defaults
        };
    }

    public SessionDefaults? GetSessionDefaults(string connectionId)
    {
        return _sessionDefaults.TryGetValue(connectionId, out var defaults) ? defaults : null;
    }
}
```

### 2.3 Intelligence Layer

#### 2.3.1 Memory Decision Engine
**Purpose**: AI-powered decision making for memory operations with integer ID mapping

**Decision Types**:
- ADD: Create new memory
- UPDATE: Modify existing memory
- DELETE: Remove outdated memory
- NONE: No action needed

**Implementation Pattern with Integer IDs**:
```csharp
public class MemoryDecisionEngine
{
    public async Task<MemoryOperations> DecideOperationsAsync(
        IEnumerable<string> facts,
        IEnumerable<ExistingMemory> existingMemories,
        SessionContext session,
        CancellationToken cancellationToken = default)
    {
        // Use simple integer mapping instead of UUID mapping for LLM clarity
        var idMapping = CreateIntegerMapping(existingMemories);
        
        // Generate decision prompt with simple integer references
        var prompt = BuildDecisionPrompt(facts, idMapping);
        
        // Get decision from LLM provider
        var provider = await _llmProviderFactory.GetProviderAsync("anthropic");
        var decisions = await provider.GenerateStructuredAsync<MemoryOperations>(
            prompt, cancellationToken);
        
        // Map simple integers back to actual memory IDs
        return MapIntegersToMemoryIds(decisions, idMapping);
    }

    private Dictionary<int, int> CreateIntegerMapping(IEnumerable<ExistingMemory> memories)
    {
        // Create simple 1-based mapping for LLM clarity
        var mapping = new Dictionary<int, int>();
        var index = 1;
        
        foreach (var memory in memories)
        {
            mapping[index] = memory.Id;
            index++;
        }
        
        return mapping;
    }

    private string BuildDecisionPrompt(IEnumerable<string> facts, Dictionary<int, int> mapping)
    {
        var existingMemoriesText = string.Join("\n", 
            mapping.Select(kvp => $"{kvp.Key}. {GetMemoryContent(kvp.Value)}"));

        return $@"
Given these new facts:
{string.Join("\n", facts.Select((f, i) => $"- {f}"))}

And these existing memories:
{existingMemoriesText}

Decide what operations to perform. Use simple numbers (1, 2, 3, etc.) to reference existing memories.
Return operations in JSON format with integer IDs.";
    }
}
```

### 2.4 Storage Layer

#### 2.4.1 Vector Storage (sqlite-vec) with Integer IDs
**Purpose**: Handles vector embeddings storage and similarity search using integer primary keys

**Database Schema**:
```sql
-- ID sequence table for generating unique integers
CREATE TABLE memory_id_sequence (
    id INTEGER PRIMARY KEY AUTOINCREMENT
);

-- Main memories table with integer primary key
CREATE TABLE memories (
    id INTEGER PRIMARY KEY,
    content TEXT NOT NULL,
    user_id TEXT,
    agent_id TEXT,
    run_id TEXT,
    metadata TEXT, -- JSON
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    version INTEGER DEFAULT 1
);

-- Vector embeddings using sqlite-vec with integer foreign key
CREATE VIRTUAL TABLE memory_embeddings USING vec0(
    memory_id INTEGER PRIMARY KEY,
    embedding BLOB
);

-- Session defaults storage
CREATE TABLE session_defaults (
    connection_id TEXT PRIMARY KEY,
    user_id TEXT,
    agent_id TEXT,
    run_id TEXT,
    metadata TEXT, -- JSON
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

-- Indexes for session isolation and performance
CREATE INDEX idx_memories_session ON memories(user_id, agent_id, run_id);
CREATE INDEX idx_memories_created ON memories(created_at DESC);
CREATE INDEX idx_session_defaults_created ON session_defaults(created_at DESC);
```

**Vector Search Implementation with Integer IDs**:
```csharp
public async Task<IEnumerable<MemorySearchResult>> SearchVectorAsync(
    float[] queryEmbedding,
    SessionContext session,
    int limit = 10,
    CancellationToken cancellationToken = default)
{
    using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
    using var command = connection.CreateCommand();
    
    command.CommandText = @"
        SELECT m.id, m.content, m.metadata, m.created_at,
               ve.distance
        FROM memory_embeddings ve
        JOIN memories m ON ve.memory_id = m.id
        WHERE ($userId IS NULL OR m.user_id = $userId)
          AND ($agentId IS NULL OR m.agent_id = $agentId)
          AND ($runId IS NULL OR m.run_id = $runId)
        ORDER BY ve.embedding <-> $queryVector
        LIMIT $limit";
    
    command.Parameters.AddWithValue("$queryVector", SerializeEmbedding(queryEmbedding));
    command.Parameters.AddWithValue("$userId", session.UserId);
    command.Parameters.AddWithValue("$agentId", session.AgentId);
    command.Parameters.AddWithValue("$runId", session.RunId);
    command.Parameters.AddWithValue("$limit", limit);
    
    // Execute and map results with integer IDs
    return await ExecuteSearchQueryAsync(command, cancellationToken);
}
```

#### 2.4.2 Full-Text Search (FTS5) with Integer References
**Purpose**: Provides full-text search capabilities for memory content with integer ID references

**FTS5 Schema**:
```sql
-- FTS5 virtual table for full-text search with integer memory_id
CREATE VIRTUAL TABLE memory_fts USING fts5(
    memory_id UNINDEXED,
    content,
    metadata,
    content='memories',
    content_rowid='id'
);

-- Triggers to keep FTS5 in sync with integer IDs
CREATE TRIGGER memories_fts_insert AFTER INSERT ON memories
BEGIN
    INSERT INTO memory_fts(memory_id, content, metadata)
    VALUES (NEW.id, NEW.content, NEW.metadata);
END;

CREATE TRIGGER memories_fts_update AFTER UPDATE ON memories
BEGIN
    UPDATE memory_fts SET content = NEW.content, metadata = NEW.metadata
    WHERE memory_id = NEW.id;
END;

CREATE TRIGGER memories_fts_delete AFTER DELETE ON memories
BEGIN
    DELETE FROM memory_fts WHERE memory_id = OLD.id;
END;
```

## 3. Data Flow Architecture

### 3.1 Session Initialization Flow

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ MCP Client  │───▶│ HTTP Headers│───▶│ Header      │
│ Connection  │    │ Processing  │    │ Validation  │
└─────────────┘    └─────────────┘    └─────────────┘
        │                   │                   │
        ▼                   ▼                   ▼
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ Session     │◀───│ Connection  │◀───│ Session     │
│ Defaults    │    │ Established │    │ Storage     │
│ Cache       │    │             │    │             │
└─────────────┘    └─────────────┘    └─────────────┘
```

### 3.2 Memory Addition Flow with Integer IDs

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ MCP Client  │───▶│ SSE Server  │───▶│ Tool Router │
└─────────────┘    └─────────────┘    └─────────────┘
                           │                   │
                           ▼                   ▼
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Session   │───▶│ AddMemory   │◀───│ Session     │
│ Resolution  │    │    Tool     │    │ Context     │
└─────────────┘    └─────────────┘    └─────────────┘
        │                   │
        ▼                   ▼
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Memory    │───▶│    Fact     │───▶│   Memory    │
│  Manager    │    │ Extraction  │    │  Decision   │
└─────────────┘    └─────────────┘    └─────────────┘
        │                   │                   │
        ▼                   ▼                   ▼
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ Integer ID  │───▶│ Embedding   │───▶│   SQLite    │
│ Generation  │    │ Generation  │    │  Storage    │
└─────────────┘    └─────────────┘    └─────────────┘
```

## 4. Configuration and Deployment

### 4.1 Configuration Schema

```json
{
  "MemoryServer": {
    "Database": {
      "ConnectionString": "Data Source=memory.db",
      "EnableWAL": true,
      "CacheSize": 10000,
      "BusyTimeout": 30000,
      "Extensions": {
        "SqliteVec": {
          "Path": "vec0",
          "Enabled": true
        }
      }
    },
    "LlmProviders": {
      "OpenAI": {
        "ApiKey": "${OPENAI_API_KEY}",
        "Model": "gpt-4",
        "EmbeddingModel": "text-embedding-3-small"
      },
      "Anthropic": {
        "ApiKey": "${ANTHROPIC_API_KEY}",
        "Model": "claude-3-sonnet-20240229"
      }
    },
    "SessionDefaults": {
      "DefaultUserId": null,
      "DefaultAgentId": null,
      "DefaultRunId": null,
      "EnableHttpHeaders": true,
      "EnableSessionInit": true,
      "CacheTimeout": "01:00:00"
    },
    "Performance": {
      "MaxConcurrentOperations": 100,
      "EmbeddingCacheSize": 1000,
      "ConnectionPoolSize": 10,
      "IntegerIdCacheSize": 10000
    },
    "Monitoring": {
      "LogLevel": "Information",
      "EnableMetrics": true,
      "MetricsPort": 9090
    }
  }
}
```

### 4.2 Dependency Injection Setup

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryMcpServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Core services
        services.AddSingleton<SqliteManager>();
        services.AddSingleton<IMemoryManager, MemoryManager>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<MemoryIdGenerator>();
        
        // Session management
        services.AddSingleton<IHeaderProcessor, HeaderProcessor>();
        services.AddSingleton<ISessionContextResolver, SessionContextResolver>();
        
        // Intelligence layer
        services.AddSingleton<FactExtractionEngine>();
        services.AddSingleton<MemoryDecisionEngine>();
        
        // LLM provider integration
        services.AddSingleton<ILlmProviderFactory, WorkspaceLlmProviderFactory>();
        
        // MCP protocol
        services.AddSingleton<McpSseServer>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ToolRouter>();
        
        // Register MCP tools
        services.AddMcpTools();
        
        return services;
    }
    
    private static IServiceCollection AddMcpTools(this IServiceCollection services)
    {
        services.AddTransient<AddMemoryTool>();
        services.AddTransient<SearchMemoryTool>();
        services.AddTransient<UpdateMemoryTool>();
        services.AddTransient<DeleteMemoryTool>();
        services.AddTransient<GetAllMemoriesTool>();
        services.AddTransient<GetHistoryTool>();
        services.AddTransient<GetStatsTool>();
        services.AddTransient<InitSessionTool>();
        
        return services;
    }
}
```

## 5. Testing Strategy

### 5.1 Unit Testing with Integer IDs

**Test Structure**:
```csharp
[TestClass]
public class MemoryManagerTests
{
    private readonly ITestOutputHelper _output;
    private readonly MemoryManager _memoryManager;
    private readonly Mock<ISqliteManager> _mockSqliteManager;
    private readonly Mock<ILlmProviderFactory> _mockLlmProviderFactory;

    [TestMethod]
    [DataRow("user_123", null, null, DisplayName = "User session")]
    [DataRow("user_123", "agent_456", null, DisplayName = "Agent session")]
    [DataRow("user_123", "agent_456", "run_789", DisplayName = "Run session")]
    public async Task AddMemoryAsync_WithValidSession_ShouldCreateMemoryWithIntegerId(
        string userId, string? agentId, string? runId)
    {
        // Arrange
        var session = new SessionContext 
        { 
            UserId = userId, 
            AgentId = agentId, 
            RunId = runId 
        };
        var request = new AddMemoryRequest
        {
            Messages = new[] { new Message { Role = "user", Content = "I love pizza" } },
            Session = session
        };

        // Mock integer ID generation
        _mockSqliteManager
            .Setup(x => x.GenerateNextIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        // Act
        var result = await _memoryManager.AddMemoryAsync(request);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.CreatedMemories);
        Assert.AreEqual(1, result.CreatedMemories.Count);
        
        // Verify integer ID is used
        var memory = result.CreatedMemories.First();
        Assert.AreEqual(42, memory.Id);
        Assert.IsTrue(memory.Id > 0, "Memory ID should be a positive integer");
    }

    [TestMethod]
    public async Task SessionDefaults_ShouldBeAppliedCorrectly()
    {
        // Arrange
        var sessionDefaults = new SessionDefaults
        {
            UserId = "default_user",
            AgentId = "default_agent"
        };

        var request = new AddMemoryRequest
        {
            Messages = new[] { new Message { Role = "user", Content = "Test message" } },
            // No explicit session parameters - should use defaults
        };

        // Mock session resolver to return defaults
        _mockSessionResolver
            .Setup(x => x.ResolveSessionAsync(It.IsAny<JsonElement>(), It.IsAny<SessionDefaults>(), sessionDefaults))
            .ReturnsAsync(new SessionContext 
            { 
                UserId = sessionDefaults.UserId, 
                AgentId = sessionDefaults.AgentId 
            });

        // Act
        var result = await _memoryManager.AddMemoryAsync(request);

        // Assert
        Assert.IsTrue(result.Success);
        var memory = result.CreatedMemories.First();
        Assert.AreEqual("default_user", memory.Metadata.UserId);
        Assert.AreEqual("default_agent", memory.Metadata.AgentId);
    }
}
```

### 5.2 Integration Testing for Session Defaults

**HTTP Header Testing**:
```csharp
[TestClass]
public class SessionDefaultsIntegrationTests
{
    [TestMethod]
    public async Task HttpHeaders_ShouldSetSessionDefaults()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Memory-User-ID", "header_user");
        client.DefaultRequestHeaders.Add("X-Memory-Agent-ID", "header_agent");

        // Act - Establish MCP connection
        var response = await client.GetAsync("/mcp/connect");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        
        // Verify session defaults were stored
        var sessionManager = _factory.Services.GetRequiredService<ISessionManager>();
        var defaults = sessionManager.GetSessionDefaults("connection_id");
        
        Assert.IsNotNull(defaults);
        Assert.AreEqual("header_user", defaults.UserId);
        Assert.AreEqual("header_agent", defaults.AgentId);
    }
}
```

## 6. Security Considerations

### 6.1 Integer ID Security

**ID Generation Security**:
```csharp
public class SecureMemoryIdGenerator : MemoryIdGenerator
{
    public async Task<int> GenerateNextIdAsync(CancellationToken cancellationToken = default)
    {
        // Use database sequence for guaranteed uniqueness
        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            
            command.CommandText = @"
                INSERT INTO memory_id_sequence DEFAULT VALUES;
                SELECT last_insert_rowid();";
            
            var result = await command.ExecuteScalarAsync(cancellationToken);
            var id = Convert.ToInt32(result);
            
            // Validate ID is within acceptable range
            if (id <= 0 || id > int.MaxValue - 1000)
            {
                throw new InvalidOperationException("Generated ID is out of acceptable range");
            }
            
            transaction.Commit();
            return id;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
```

### 6.2 Session Default Security

**Header Validation**:
```csharp
public static class SessionValidator
{
    public static void ValidateSessionDefaults(SessionDefaults defaults)
    {
        if (defaults.UserId != null)
        {
            ValidateIdentifier(defaults.UserId, "UserId");
        }
        
        if (defaults.AgentId != null)
        {
            ValidateIdentifier(defaults.AgentId, "AgentId");
        }
        
        if (defaults.RunId != null)
        {
            ValidateIdentifier(defaults.RunId, "RunId");
        }
    }
    
    private static void ValidateIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException($"{paramName} cannot be empty");
            
        if (identifier.Length > 100)
            throw new ArgumentException($"{paramName} too long (max 100 characters)");
            
        if (!Regex.IsMatch(identifier, @"^[a-zA-Z0-9_-]+$"))
            throw new ArgumentException($"Invalid {paramName} format");
    }
}
```

This comprehensive design document provides the technical foundation for implementing a sophisticated Memory MCP server using SQLite as the primary storage solution with integer IDs for optimal LLM integration and flexible session default management through HTTP headers and session initialization. 