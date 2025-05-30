# LLM Provider Architecture - Enhanced with Database Session Pattern

## Overview

The LLM Provider Architecture provides a unified interface for interacting with different Large Language Model providers. This system supports OpenAI and Anthropic providers with structured output capabilities for fact extraction and memory decision making. Enhanced with Database Session Pattern integration, it ensures reliable resource management and session-scoped operations.

**ARCHITECTURE ENHANCEMENT**: This design has been updated to integrate with the Database Session Pattern, providing session-aware LLM operations and reliable resource management for memory-related AI tasks.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                LLM Provider Layer (Enhanced)                │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   LLMBase   │  │ LLMFactory  │  │   LLMConfig         │  │
│  │ (Abstract)  │  │             │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Session Integration Layer                    │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ Session     │  │ Context     │  │   Memory            │  │
│  │ Aware LLM   │  │ Resolver    │  │  Integration        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Provider Implementations                 │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   OpenAI    │  │  Anthropic  │  │   Structured        │  │
│  │  Provider   │  │  Provider   │  │    Output           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Utility Components                       │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Retry     │  │   Rate      │  │     Response        │  │
│  │  Handler    │  │  Limiter    │  │    Validator        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Core Components

### 1. LLMBase (Abstract Interface) with Session Support

**Purpose**: Defines the contract for all LLM provider implementations, ensuring consistent behavior across different providers with session-aware operations.

**Key Methods**:
- `generate_response()`: Generate text responses from conversation messages
- `generate_structured_response()`: Generate JSON responses with schema validation
- `validate_connection()`: Test provider connectivity and authentication
- `generate_with_session_context()`: Generate responses with session-scoped context
- `extract_facts_with_session()`: Extract facts with session isolation

**Session-Enhanced Interface**:
```csharp
public interface ILlmProvider
{
    Task<string> GenerateResponseAsync(
        IEnumerable<Message> messages, 
        LlmConfiguration config,
        CancellationToken cancellationToken = default);
    
    Task<T> GenerateStructuredResponseAsync<T>(
        IEnumerable<Message> messages, 
        LlmConfiguration config,
        CancellationToken cancellationToken = default) where T : class;
    
    Task<T> GenerateWithSessionContextAsync<T>(
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        LlmConfiguration config,
        CancellationToken cancellationToken = default) where T : class;
    
    Task<FactExtractionResult> ExtractFactsWithSessionAsync(
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default);
    
    Task<MemoryOperations> DecideMemoryOperationsAsync(
        IEnumerable<string> facts,
        IEnumerable<ExistingMemory> existingMemories,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default);
}
```

**Design Principles**:
- Provider-agnostic message format normalization
- Session-aware context management
- Consistent error handling across providers
- Standardized response formats
- Built-in retry and rate limiting support
- Integration with Database Session Pattern

**Message Format Standardization**:
- Unified message structure with role, content, and optional name fields
- Automatic conversion between provider-specific formats
- Support for system, user, and assistant roles
- Preservation of conversation context and metadata
- Session context injection for memory operations

### 2. Session-Aware LLM Operations

**Session Context Integration**:
```csharp
public class SessionAwareLlmProvider : ILlmProvider
{
    private readonly ILlmProvider _baseProvider;
    private readonly ISessionContextResolver _sessionResolver;
    private readonly ILogger<SessionAwareLlmProvider> _logger;

    public async Task<T> GenerateWithSessionContextAsync<T>(
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        LlmConfiguration config,
        CancellationToken cancellationToken = default) where T : class
    {
        // Enhance messages with session context
        var enhancedMessages = await EnhanceMessagesWithSessionContextAsync(messages, sessionContext);
        
        // Add session-specific system prompts
        var sessionPrompts = GenerateSessionPrompts(sessionContext);
        var allMessages = sessionPrompts.Concat(enhancedMessages);
        
        // Generate response with session awareness
        var response = await _baseProvider.GenerateStructuredResponseAsync<T>(
            allMessages, config, cancellationToken);
        
        _logger.LogDebug("Generated session-aware response for user {UserId}, agent {AgentId}, run {RunId}",
            sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);
        
        return response;
    }

    private async Task<IEnumerable<Message>> EnhanceMessagesWithSessionContextAsync(
        IEnumerable<Message> messages, 
        MemoryContext sessionContext)
    {
        var enhancedMessages = new List<Message>();
        
        // Add session context as system message
        if (!string.IsNullOrEmpty(sessionContext.UserId))
        {
            enhancedMessages.Add(new Message
            {
                Role = "system",
                Content = $"Session Context: User ID: {sessionContext.UserId}" +
                         (sessionContext.AgentId != null ? $", Agent ID: {sessionContext.AgentId}" : "") +
                         (sessionContext.RunId != null ? $", Run ID: {sessionContext.RunId}" : "")
            });
        }
        
        enhancedMessages.AddRange(messages);
        return enhancedMessages;
    }
}
```

### 3. OpenAI Provider Implementation with Session Support

**Authentication Strategy**:
- API key-based authentication with optional organization support
- Support for custom base URLs (Azure OpenAI, local deployments)
- Secure credential management through environment variables
- Connection validation during initialization
- Session-scoped request tracking

#### OpenAI Provider Flow with Session Context

```csharp
public class OpenAIProvider : ILlmProvider
{
    private readonly OpenAIClient _client;
    private readonly ILogger<OpenAIProvider> _logger;

    public async Task<FactExtractionResult> ExtractFactsWithSessionAsync(
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        // Build session-aware fact extraction prompt
        var systemPrompt = BuildFactExtractionPrompt(sessionContext);
        var allMessages = new[] { systemPrompt }.Concat(messages);

        // Configure for structured output
        var config = new LlmConfiguration
        {
            Model = "gpt-4",
            Temperature = 0.0,
            MaxTokens = 1000,
            ResponseFormat = "json_object"
        };

        // Execute with session context
        var response = await GenerateStructuredResponseAsync<FactExtractionResult>(
            allMessages, config, cancellationToken);

        // Log session-specific metrics
        _logger.LogDebug("Extracted {FactCount} facts for session {UserId}/{AgentId}/{RunId}",
            response.Facts.Count, sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);

        return response;
    }

    public async Task<MemoryOperations> DecideMemoryOperationsAsync(
        IEnumerable<string> facts,
        IEnumerable<ExistingMemory> existingMemories,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        // Create integer mapping for LLM clarity
        var idMapping = CreateIntegerMapping(existingMemories);
        
        // Build session-aware decision prompt
        var prompt = BuildMemoryDecisionPrompt(facts, idMapping, sessionContext);
        
        var messages = new[]
        {
            new Message { Role = "system", Content = prompt }
        };

        // Configure for structured output
        var config = new LlmConfiguration
        {
            Model = "gpt-4",
            Temperature = 0.0,
            MaxTokens = 2000,
            ResponseFormat = "json_object"
        };

        // Execute decision making
        var operations = await GenerateStructuredResponseAsync<MemoryOperations>(
            messages, config, cancellationToken);

        // Map integer IDs back to actual memory IDs
        var mappedOperations = MapIntegersToMemoryIds(operations, idMapping);

        _logger.LogDebug("Generated {OperationCount} memory operations for session {UserId}/{AgentId}/{RunId}",
            mappedOperations.Operations.Count, sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);

        return mappedOperations;
    }

    private Message BuildFactExtractionPrompt(MemoryContext sessionContext)
    {
        var prompt = @"
You are extracting facts from conversation messages for a memory system.

Session Context:
- User ID: " + (sessionContext.UserId ?? "unknown") + @"
- Agent ID: " + (sessionContext.AgentId ?? "unknown") + @"
- Run ID: " + (sessionContext.RunId ?? "unknown") + @"

Extract factual information that should be remembered about this specific user/session.
Focus on:
1. User preferences and characteristics
2. Important personal information
3. Goals and objectives
4. Relationships and connections
5. Significant events or decisions

Return a JSON object with this structure:
{
  ""facts"": [""fact1"", ""fact2"", ...],
  ""extraction_metadata"": {
    ""confidence_score"": 0.95,
    ""session_context_used"": true,
    ""extraction_time"": ""2024-01-15T10:30:00Z""
  }
}";

        return new Message { Role = "system", Content = prompt };
    }

    private string BuildMemoryDecisionPrompt(
        IEnumerable<string> facts, 
        Dictionary<int, int> idMapping, 
        MemoryContext sessionContext)
    {
        var existingMemoriesText = string.Join("\n", 
            idMapping.Select(kvp => $"{kvp.Key}. {GetMemoryContent(kvp.Value)}"));

        return $@"
You are deciding what memory operations to perform based on new facts.

Session Context:
- User ID: {sessionContext.UserId ?? "unknown"}
- Agent ID: {sessionContext.AgentId ?? "unknown"}
- Run ID: {sessionContext.RunId ?? "unknown"}

New facts to process:
{string.Join("\n", facts.Select((f, i) => $"- {f}"))}

Existing memories for this session:
{existingMemoriesText}

Decide what operations to perform. Use simple numbers (1, 2, 3, etc.) to reference existing memories.

Return a JSON object with this structure:
{{
  ""operations"": [
    {{
      ""id"": ""1"",
      ""event"": ""UPDATE"",
      ""text"": ""updated memory content"",
      ""old_memory"": ""previous content"",
      ""confidence"": 0.95,
      ""reasoning"": ""explanation""
    }}
  ],
  ""processing_metadata"": {{
    ""total_operations"": 1,
    ""session_context_used"": true,
    ""decision_time"": ""{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}""
  }}
}}";
    }
}
```

**Request Handling with Session Context**:
- Native support for OpenAI's chat completions API
- Session-aware structured output using response_format parameter
- Token usage tracking per session
- Proper handling of OpenAI-specific parameters with session context

**Error Management**:
- OpenAI-specific error code handling with session context logging
- Rate limit detection and backoff with session tracking
- Token limit management per session
- Network timeout and retry logic with session awareness

### 4. Anthropic Provider Implementation with Session Support

**Authentication Strategy**:
- API key-based authentication
- Support for different model families (Claude 3, Claude 2)
- Proper handling of Anthropic's message format requirements
- Connection validation and health checks
- Session-scoped request management

#### Anthropic Provider Flow with Session Context

```csharp
public class AnthropicProvider : ILlmProvider
{
    private readonly AnthropicClient _client;
    private readonly ILogger<AnthropicProvider> _logger;

    public async Task<FactExtractionResult> ExtractFactsWithSessionAsync(
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        // Separate system message and conversation
        var systemMessage = BuildFactExtractionSystemPrompt(sessionContext);
        var conversationMessages = FormatMessagesForAnthropic(messages);

        // Configure request
        var request = new MessageRequest
        {
            Model = "claude-3-sonnet-20240229",
            System = systemMessage,
            Messages = conversationMessages,
            MaxTokens = 1000,
            Temperature = 0.0
        };

        // Execute request
        var response = await _client.Messages.CreateAsync(request, cancellationToken);
        
        // Extract structured data from response
        var factResult = ExtractJsonFromContent<FactExtractionResult>(
            response.Content.FirstOrDefault()?.Text ?? "",
            "fact extraction");

        _logger.LogDebug("Extracted {FactCount} facts for session {UserId}/{AgentId}/{RunId}",
            factResult.Facts.Count, sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);

        return factResult;
    }

    public async Task<MemoryOperations> DecideMemoryOperationsAsync(
        IEnumerable<string> facts,
        IEnumerable<ExistingMemory> existingMemories,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        // Create integer mapping for LLM clarity
        var idMapping = CreateIntegerMapping(existingMemories);
        
        // Build decision system prompt
        var systemPrompt = BuildMemoryDecisionSystemPrompt(facts, idMapping, sessionContext);
        
        // Configure request
        var request = new MessageRequest
        {
            Model = "claude-3-sonnet-20240229",
            System = systemPrompt,
            Messages = new[]
            {
                new Message { Role = "user", Content = "Please analyze the facts and decide on memory operations." }
            },
            MaxTokens = 2000,
            Temperature = 0.0
        };

        // Execute request
        var response = await _client.Messages.CreateAsync(request, cancellationToken);
        
        // Extract and map operations
        var operations = ExtractJsonFromContent<MemoryOperations>(
            response.Content.FirstOrDefault()?.Text ?? "",
            "memory operations");
        
        var mappedOperations = MapIntegersToMemoryIds(operations, idMapping);

        _logger.LogDebug("Generated {OperationCount} memory operations for session {UserId}/{AgentId}/{RunId}",
            mappedOperations.Operations.Count, sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);

        return mappedOperations;
    }

    private string BuildFactExtractionSystemPrompt(MemoryContext sessionContext)
    {
        return $@"
You are an AI assistant specialized in extracting factual information from conversations for a memory system.

Session Context:
- User ID: {sessionContext.UserId ?? "unknown"}
- Agent ID: {sessionContext.AgentId ?? "unknown"}  
- Run ID: {sessionContext.RunId ?? "unknown"}

Your task is to extract facts that should be remembered about this specific user/session. Focus on:

1. User preferences, characteristics, and personal information
2. Goals, objectives, and intentions
3. Relationships and connections mentioned
4. Significant events, decisions, or changes
5. Context-specific information relevant to this session

Extract only factual, memorable information. Avoid extracting:
- Temporary states or emotions
- Procedural conversation elements
- Information already well-established

Respond with a JSON object in this exact format:
{{
  ""facts"": [""fact1"", ""fact2"", ""fact3""],
  ""extraction_metadata"": {{
    ""confidence_score"": 0.95,
    ""session_context_used"": true,
    ""extraction_time"": ""{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}""
  }}
}}";
    }

    private T ExtractJsonFromContent<T>(string content, string operationType) where T : class
    {
        try
        {
            // Try to find JSON in the response
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                return JsonSerializer.Deserialize<T>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new InvalidOperationException($"Deserialized {operationType} result is null");
            }
            
            throw new InvalidOperationException($"No valid JSON found in {operationType} response");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JSON from {OperationType} response: {Content}", 
                operationType, content);
            throw new InvalidOperationException($"Failed to parse {operationType} JSON response", ex);
        }
    }
}
```

## Session Integration Patterns

### 1. Memory System Integration

**Repository Pattern with Session Context**:
```csharp
public class MemoryService
{
    private readonly ILlmProvider _llmProvider;
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly IMemoryRepository _memoryRepository;

    public async Task<MemoryAddResult> AddMemoryWithSessionAsync(
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        using var dbSession = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        // Extract facts with session context
        var factResult = await _llmProvider.ExtractFactsWithSessionAsync(
            messages, sessionContext, cancellationToken);
        
        // Get existing memories for session
        var existingMemories = await _memoryRepository.GetMemoriesForSessionAsync(
            dbSession, sessionContext, cancellationToken);
        
        // Decide operations with session context
        var operations = await _llmProvider.DecideMemoryOperationsAsync(
            factResult.Facts, existingMemories, sessionContext, cancellationToken);
        
        // Execute operations within session
        var results = await ExecuteMemoryOperationsAsync(
            dbSession, operations, sessionContext, cancellationToken);
        
        return new MemoryAddResult
        {
            Success = true,
            CreatedMemories = results.CreatedMemories,
            UpdatedMemories = results.UpdatedMemories,
            SessionContext = sessionContext
        };
    }
}
```

### 2. Service Layer Integration

**Dependency Injection with Session Support**:
```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmProviders(this IServiceCollection services, IConfiguration configuration)
    {
        // Register base providers
        services.AddSingleton<ILlmProvider, OpenAIProvider>(provider =>
        {
            var config = configuration.GetSection("LlmProviders:OpenAI").Get<OpenAIConfiguration>();
            var logger = provider.GetRequiredService<ILogger<OpenAIProvider>>();
            return new OpenAIProvider(config, logger);
        });

        services.AddSingleton<ILlmProvider, AnthropicProvider>(provider =>
        {
            var config = configuration.GetSection("LlmProviders:Anthropic").Get<AnthropicConfiguration>();
            var logger = provider.GetRequiredService<ILogger<AnthropicProvider>>();
            return new AnthropicProvider(config, logger);
        });

        // Register session-aware wrapper
        services.AddScoped<ILlmProvider, SessionAwareLlmProvider>();
        
        // Register factory
        services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();
        
        // Register session context resolver
        services.AddScoped<ISessionContextResolver, SessionContextResolver>();

        return services;
    }
}
```

## Testing Strategy with Session Pattern

### 1. Unit Testing with Session Context

```csharp
[TestClass]
public class LlmProviderTests
{
    private ILlmProvider _llmProvider;
    private Mock<ISessionContextResolver> _mockSessionResolver;

    [TestInitialize]
    public void Setup()
    {
        _mockSessionResolver = new Mock<ISessionContextResolver>();
        var mockLogger = new Mock<ILogger<SessionAwareLlmProvider>>();
        
        _llmProvider = new SessionAwareLlmProvider(
            new MockLlmProvider(), 
            _mockSessionResolver.Object, 
            mockLogger.Object);
    }

    [TestMethod]
    public async Task ExtractFactsWithSessionAsync_ShouldIncludeSessionContext()
    {
        // Arrange
        var messages = new[]
        {
            new Message { Role = "user", Content = "I love Italian food" }
        };
        
        var sessionContext = new MemoryContext
        {
            UserId = "user123",
            AgentId = "agent456",
            RunId = "run789"
        };

        // Act
        var result = await _llmProvider.ExtractFactsWithSessionAsync(
            messages, sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Facts.Any());
        Assert.IsTrue(result.ExtractionMetadata.SessionContextUsed);
    }

    [TestMethod]
    public async Task DecideMemoryOperationsAsync_ShouldUseSessionContext()
    {
        // Arrange
        var facts = new[] { "User loves Italian food" };
        var existingMemories = new[]
        {
            new ExistingMemory { Id = 1, Content = "User likes food" }
        };
        
        var sessionContext = new MemoryContext
        {
            UserId = "user123",
            AgentId = "agent456"
        };

        // Act
        var result = await _llmProvider.DecideMemoryOperationsAsync(
            facts, existingMemories, sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Operations.Any());
        Assert.IsTrue(result.ProcessingMetadata.SessionContextUsed);
    }
}
```

### 2. Integration Testing

```csharp
[TestClass]
public class LlmProviderIntegrationTests
{
    [TestMethod]
    public async Task EndToEndMemoryWorkflow_ShouldWorkWithSessionContext()
    {
        // Test complete workflow from fact extraction to memory operations
        // with session context preservation throughout
    }

    [TestMethod]
    public async Task SessionIsolation_ShouldMaintainSeparateContexts()
    {
        // Test that different sessions maintain separate contexts
        // and don't interfere with each other
    }
}
```

## Performance Optimization

### 1. Session-Aware Caching

```csharp
public class CachedLlmProvider : ILlmProvider
{
    private readonly ILlmProvider _baseProvider;
    private readonly IMemoryCache _cache;

    public async Task<FactExtractionResult> ExtractFactsWithSessionAsync(
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        // Create session-aware cache key
        var cacheKey = GenerateSessionCacheKey("facts", messages, sessionContext);
        
        if (_cache.TryGetValue(cacheKey, out FactExtractionResult? cachedResult))
        {
            return cachedResult!;
        }

        var result = await _baseProvider.ExtractFactsWithSessionAsync(
            messages, sessionContext, cancellationToken);
        
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(30));
        return result;
    }

    private string GenerateSessionCacheKey(string operation, IEnumerable<Message> messages, MemoryContext sessionContext)
    {
        var messageHash = ComputeHash(string.Join("", messages.Select(m => m.Content)));
        var sessionKey = $"{sessionContext.UserId}:{sessionContext.AgentId}:{sessionContext.RunId}";
        return $"llm:{operation}:{sessionKey}:{messageHash}";
    }
}
```

### 2. Batch Processing with Session Context

```csharp
public class BatchLlmProcessor
{
    public async Task<List<FactExtractionResult>> ProcessBatchWithSessionAsync(
        IEnumerable<(IEnumerable<Message> messages, MemoryContext context)> batch,
        CancellationToken cancellationToken = default)
    {
        // Group by session context for efficient processing
        var groupedBySession = batch.GroupBy(item => 
            $"{item.context.UserId}:{item.context.AgentId}:{item.context.RunId}");

        var results = new List<FactExtractionResult>();
        
        foreach (var sessionGroup in groupedBySession)
        {
            // Process all items for a session together
            var sessionResults = await ProcessSessionBatchAsync(
                sessionGroup, cancellationToken);
            results.AddRange(sessionResults);
        }

        return results;
    }
}
```

## Conclusion

The enhanced LLM Provider Architecture with Database Session Pattern integration provides a robust, reliable, and performant foundation for AI-powered memory operations. Key benefits include:

- **Session-Aware Operations**: All LLM operations properly scoped within session contexts
- **Reliable Resource Management**: Integration with Database Session Pattern for proper cleanup
- **Provider Abstraction**: Consistent interface across OpenAI and Anthropic providers
- **Structured Output**: Reliable JSON generation with schema validation
- **Performance Optimization**: Session-aware caching and batch processing
- **Production Ready**: Comprehensive error handling, retry logic, and monitoring

This architecture ensures that the Memory MCP Server can reliably handle AI-powered operations while maintaining session isolation and optimal performance in both development and production environments. 