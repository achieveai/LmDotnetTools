using System.Text.Json;
using MemoryServer.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
/// Service for extracting entities and relationships from conversation content using LLM providers.
/// Implements the Strategy pattern for different extraction approaches with JSON schema support.
/// </summary>
public class GraphExtractionService : IGraphExtractionService
{
  private readonly IAgent _agent;
  private readonly IPromptReader _promptReader;
  private readonly ILogger<GraphExtractionService> _logger;
  private readonly MemoryServerOptions _options;
  private readonly ILmConfigService? _lmConfigService;
  private readonly JsonSerializerOptions _jsonOptions;

  public GraphExtractionService(
    IAgent agent,
    IPromptReader promptReader,
    ILogger<GraphExtractionService> logger,
    IOptions<MemoryServerOptions> options,
    ILmConfigService? lmConfigService = null)
  {
    _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    _promptReader = promptReader ?? throw new ArgumentNullException(nameof(promptReader));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _lmConfigService = lmConfigService;
    
    _jsonOptions = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
      WriteIndented = false,
      PropertyNameCaseInsensitive = true
    };
  }

  public async Task<IEnumerable<Entity>> ExtractEntitiesAsync(
    string content, 
    SessionContext sessionContext, 
    int memoryId, 
    CancellationToken cancellationToken = default)
  {
    try
    {
      _logger.LogInformation("Extracting entities from content for memory {MemoryId} in session {SessionContext}", 
        memoryId, sessionContext);

      var promptChain = _promptReader.GetPromptChain("entity_extraction");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> { ["content"] = content });
      var generateOptions = await CreateGenerateReplyOptionsAsync("small_model", cancellationToken);
      var response = await _agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      
      var responseText = ExtractTextFromResponse(response);
      var extractedEntities = ParseEntitiesFromJson(responseText);
      
      // Convert to Entity objects with session context
      var entities = extractedEntities.Select(e => new Entity
      {
        Name = e.Name,
        Type = e.Type,
        Aliases = e.Aliases?.ToList(),
        UserId = sessionContext.UserId,
        AgentId = sessionContext.AgentId,
        RunId = sessionContext.RunId,
        Confidence = e.Confidence,
        SourceMemoryIds = new List<int> { memoryId },
        Metadata = new Dictionary<string, object>
        {
          ["extraction_reasoning"] = e.Reasoning ?? "",
          ["extraction_timestamp"] = DateTime.UtcNow,
          ["source_memory_id"] = memoryId
        }
      }).ToList();

      _logger.LogInformation("Extracted {EntityCount} entities from memory {MemoryId}", 
        entities.Count, memoryId);
      
      return entities;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to extract entities from memory {MemoryId}", memoryId);
      return Enumerable.Empty<Entity>();
    }
  }

  public async Task<IEnumerable<Relationship>> ExtractRelationshipsAsync(
    string content, 
    SessionContext sessionContext, 
    int memoryId, 
    CancellationToken cancellationToken = default)
  {
    try
    {
      _logger.LogInformation("Extracting relationships from content for memory {MemoryId} in session {SessionContext}", 
        memoryId, sessionContext);

      // First extract entities to provide context for relationship extraction
      var entities = await ExtractEntitiesAsync(content, sessionContext, memoryId, cancellationToken);
      var entitiesJson = JsonSerializer.Serialize(entities.Select(e => new { e.Name, e.Type }), _jsonOptions);

      var promptChain = _promptReader.GetPromptChain("relationship_extraction");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> 
      { 
        ["content"] = content, 
        ["entities_json"] = entitiesJson 
      });
      
      var generateOptions = await CreateGenerateReplyOptionsAsync("small_model", cancellationToken);
      var response = await _agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      var responseText = ExtractTextFromResponse(response);
      var extractedRelationships = ParseRelationshipsFromJson(responseText);
      
      // Convert to Relationship objects with session context
      var relationships = extractedRelationships.Select(r => new Relationship
      {
        Source = r.Source,
        RelationshipType = r.RelationshipType,
        Target = r.Target,
        UserId = sessionContext.UserId,
        AgentId = sessionContext.AgentId,
        RunId = sessionContext.RunId,
        Confidence = r.Confidence,
        SourceMemoryId = memoryId,
        TemporalContext = r.TemporalContext,
        Metadata = new Dictionary<string, object>
        {
          ["extraction_reasoning"] = r.Reasoning ?? "",
          ["extraction_timestamp"] = DateTime.UtcNow,
          ["source_memory_id"] = memoryId
        }
      }).ToList();

      _logger.LogInformation("Extracted {RelationshipCount} relationships from memory {MemoryId}", 
        relationships.Count, memoryId);
      
      return relationships;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to extract relationships from memory {MemoryId}", memoryId);
      return Enumerable.Empty<Relationship>();
    }
  }

  public async Task<(IEnumerable<Entity> Entities, IEnumerable<Relationship> Relationships)> ExtractGraphDataAsync(
    string content, 
    SessionContext sessionContext, 
    int memoryId, 
    CancellationToken cancellationToken = default)
  {
    try
    {
      _logger.LogInformation("Extracting combined graph data from content for memory {MemoryId} in session {SessionContext}", 
        memoryId, sessionContext);

      var promptChain = _promptReader.GetPromptChain("combined_extraction");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> { ["content"] = content });
      var generateOptions = await CreateGenerateReplyOptionsAsync("small_model", cancellationToken);
      var response = await _agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      
      var responseText = ExtractTextFromResponse(response);
      var extractedData = ParseCombinedExtractionFromJson(responseText);
      
      // Convert entities
      var entities = extractedData.Entities.Select(e => new Entity
      {
        Name = e.Name,
        Type = e.Type,
        Aliases = e.Aliases?.ToList(),
        UserId = sessionContext.UserId,
        AgentId = sessionContext.AgentId,
        RunId = sessionContext.RunId,
        Confidence = e.Confidence,
        SourceMemoryIds = new List<int> { memoryId },
        Metadata = new Dictionary<string, object>
        {
          ["extraction_reasoning"] = e.Reasoning ?? "",
          ["extraction_timestamp"] = DateTime.UtcNow,
          ["source_memory_id"] = memoryId
        }
      }).ToList();

      // Convert relationships
      var relationships = extractedData.Relationships.Select(r => new Relationship
      {
        Source = r.Source,
        RelationshipType = r.RelationshipType,
        Target = r.Target,
        UserId = sessionContext.UserId,
        AgentId = sessionContext.AgentId,
        RunId = sessionContext.RunId,
        Confidence = r.Confidence,
        SourceMemoryId = memoryId,
        TemporalContext = r.TemporalContext,
        Metadata = new Dictionary<string, object>
        {
          ["extraction_reasoning"] = r.Reasoning ?? "",
          ["extraction_timestamp"] = DateTime.UtcNow,
          ["source_memory_id"] = memoryId
        }
      }).ToList();

      _logger.LogInformation("Extracted {EntityCount} entities and {RelationshipCount} relationships from memory {MemoryId}", 
        entities.Count, relationships.Count, memoryId);
      
      return (entities, relationships);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to extract combined graph data from memory {MemoryId}", memoryId);
      return (Enumerable.Empty<Entity>(), Enumerable.Empty<Relationship>());
    }
  }

  public async Task<GraphUpdateInstructions> AnalyzeGraphUpdatesAsync(
    string content,
    IEnumerable<Entity> existingEntities,
    IEnumerable<Relationship> existingRelationships,
    SessionContext sessionContext,
    int memoryId,
    CancellationToken cancellationToken = default)
  {
    try
    {
      _logger.LogInformation("Analyzing graph updates for memory {MemoryId} in session {SessionContext}", 
        memoryId, sessionContext);

      var entitiesJson = JsonSerializer.Serialize(existingEntities.Select(e => new 
      { 
        e.Name, 
        e.Type, 
        e.Confidence 
      }), _jsonOptions);
      
      var relationshipsJson = JsonSerializer.Serialize(existingRelationships.Select(r => new 
      { 
        r.Source, 
        r.RelationshipType, 
        r.Target, 
        r.Confidence, 
        r.TemporalContext 
      }), _jsonOptions);

      var promptChain = _promptReader.GetPromptChain("graph_update_analysis");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> 
      { 
        ["content"] = content,
        ["existing_entities_json"] = entitiesJson,
        ["existing_relationships_json"] = relationshipsJson
      });
      
      var generateOptions = await CreateGenerateReplyOptionsAsync("small_model", cancellationToken);
      var response = await _agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      var responseText = ExtractTextFromResponse(response);
      var updateInstructions = ParseUpdateInstructionsFromJson(responseText);

      _logger.LogInformation("Generated {UpdateCount} graph update instructions for memory {MemoryId}", 
        updateInstructions.Updates.Count, memoryId);
      
      return updateInstructions;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to analyze graph updates for memory {MemoryId}", memoryId);
      return new GraphUpdateInstructions();
    }
  }

  public async Task<IEnumerable<Entity>> ValidateAndCleanEntitiesAsync(
    IEnumerable<Entity> entities,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default)
  {
    try
    {
      if (!entities.Any())
        return entities;

      _logger.LogInformation("Validating and cleaning {EntityCount} entities for session {SessionContext}", 
        entities.Count(), sessionContext);

      var entitiesJson = JsonSerializer.Serialize(entities.Select(e => new 
      { 
        e.Name, 
        e.Type, 
        e.Aliases, 
        e.Confidence 
      }), _jsonOptions);

      var promptChain = _promptReader.GetPromptChain("entity_validation");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> { ["entities_json"] = entitiesJson });
      var generateOptions = await CreateGenerateReplyOptionsAsync("small_model", cancellationToken);
      var response = await _agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      
      var responseText = ExtractTextFromResponse(response);
      var cleanedEntities = ParseEntitiesFromJson(responseText);
      
      // Convert back to Entity objects, preserving session context
      var validatedEntities = cleanedEntities.Select(e =>
      {
        var originalEntity = entities.FirstOrDefault(orig => orig.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
        return new Entity
        {
          Name = e.Name,
          Type = e.Type,
          Aliases = e.Aliases?.ToList(),
          UserId = sessionContext.UserId,
          AgentId = sessionContext.AgentId,
          RunId = sessionContext.RunId,
          Confidence = e.Confidence,
          SourceMemoryIds = originalEntity?.SourceMemoryIds ?? new List<int>(),
          Metadata = originalEntity?.Metadata ?? new Dictionary<string, object>()
        };
      }).ToList();

      _logger.LogInformation("Validated entities: {OriginalCount} -> {CleanedCount} for session {SessionContext}", 
        entities.Count(), validatedEntities.Count, sessionContext);
      
      return validatedEntities;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to validate entities for session {SessionContext}", sessionContext);
      return entities; // Return original entities if validation fails
    }
  }

  public async Task<IEnumerable<Relationship>> ValidateAndCleanRelationshipsAsync(
    IEnumerable<Relationship> relationships,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default)
  {
    try
    {
      if (!relationships.Any())
        return relationships;

      _logger.LogInformation("Validating and cleaning {RelationshipCount} relationships for session {SessionContext}", 
        relationships.Count(), sessionContext);

      var relationshipsJson = JsonSerializer.Serialize(relationships.Select(r => new 
      { 
        r.Source, 
        r.RelationshipType, 
        r.Target, 
        r.Confidence, 
        r.TemporalContext 
      }), _jsonOptions);

      var promptChain = _promptReader.GetPromptChain("relationship_validation");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> { ["relationships_json"] = relationshipsJson });
      var generateOptions = await CreateGenerateReplyOptionsAsync("small_model", cancellationToken);
      var response = await _agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      
      var responseText = ExtractTextFromResponse(response);
      var cleanedRelationships = ParseRelationshipsFromJson(responseText);
      
      // Convert back to Relationship objects, preserving session context
      var validatedRelationships = cleanedRelationships.Select(r =>
      {
        var originalRelationship = relationships.FirstOrDefault(orig => 
          orig.Source.Equals(r.Source, StringComparison.OrdinalIgnoreCase) &&
          orig.Target.Equals(r.Target, StringComparison.OrdinalIgnoreCase));
          
        return new Relationship
        {
          Source = r.Source,
          RelationshipType = r.RelationshipType,
          Target = r.Target,
          UserId = sessionContext.UserId,
          AgentId = sessionContext.AgentId,
          RunId = sessionContext.RunId,
          Confidence = r.Confidence,
          SourceMemoryId = originalRelationship?.SourceMemoryId,
          TemporalContext = r.TemporalContext,
          Metadata = originalRelationship?.Metadata ?? new Dictionary<string, object>()
        };
      }).ToList();

      _logger.LogInformation("Validated relationships: {OriginalCount} -> {CleanedCount} for session {SessionContext}", 
        relationships.Count(), validatedRelationships.Count, sessionContext);
      
      return validatedRelationships;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to validate relationships for session {SessionContext}", sessionContext);
      return relationships; // Return original relationships if validation fails
    }
  }

  #region Private Helper Methods

  /// <summary>
  /// Creates GenerateReplyOptions with LmConfig integration and JSON schema support.
  /// </summary>
  private async Task<GenerateReplyOptions> CreateGenerateReplyOptionsAsync(
    string capability, 
    CancellationToken cancellationToken = default)
  {
    try
    {
      // Try to use LmConfig for optimal model selection
      if (_lmConfigService != null)
      {
        var modelConfig = await _lmConfigService.GetOptimalModelAsync("small-model;json_mode", cancellationToken);
        if (modelConfig != null)
        {
          var options = new GenerateReplyOptions
          {
            ModelId = modelConfig.Id,
            Temperature = 0.0f, // Use low temperature for consistent extraction
            MaxToken = 2000 // Reasonable limit for graph extraction
          };

          // Add JSON schema if model supports structured output
          if (modelConfig.HasCapability("structured_output") || modelConfig.HasCapability("json_schema"))
          {
            options = options with { ResponseFormat = CreateJsonSchemaForCapability(capability) };
            _logger.LogDebug("Using structured output with JSON schema for capability {Capability} with model {ModelId}", 
              capability, modelConfig.Id);
          }
          else if (modelConfig.HasCapability("json_mode"))
          {
            options = options with { ResponseFormat = ResponseFormat.JSON };
            _logger.LogDebug("Using JSON mode for capability {Capability} with model {ModelId}", 
              capability, modelConfig.Id);
          }

          return options;
        }
      }

      // Fallback to basic configuration
      return CreateBasicGenerateReplyOptions(capability);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to create optimal GenerateReplyOptions for capability {Capability}, using fallback", capability);
      return CreateBasicGenerateReplyOptions(capability);
    }
  }

  /// <summary>
  /// Creates basic GenerateReplyOptions from configuration.
  /// </summary>
  private GenerateReplyOptions CreateBasicGenerateReplyOptions(string capability)
  {
    var provider = _options.LLM.DefaultProvider.ToLower();
    
    var options = provider switch
    {
      "anthropic" => new GenerateReplyOptions
      {
        ModelId = _options.LLM.Anthropic.Model,
        Temperature = _options.LLM.Anthropic.Temperature,
        MaxToken = _options.LLM.Anthropic.MaxTokens
      },
      "openai" => new GenerateReplyOptions
      {
        ModelId = _options.LLM.OpenAI.Model,
        Temperature = _options.LLM.OpenAI.Temperature,
        MaxToken = _options.LLM.OpenAI.MaxTokens
      },
      _ => new GenerateReplyOptions
      {
        ModelId = "gpt-4.1-nano",
        Temperature = 0.0f,
        MaxToken = 1000
      }
    };

    // Add basic JSON mode if available
    if (provider == "openai"
        && (_options.LLM.OpenAI.Model.Contains("gpt-4")
            || _options.LLM.OpenAI.Model.Contains("gpt-3.5")))
    {
      options = options with { ResponseFormat = ResponseFormat.JSON };
    }

    return options;
  }

  /// <summary>
  /// Creates JSON schema for specific graph extraction capabilities.
  /// </summary>
  private static ResponseFormat CreateJsonSchemaForCapability(string capability)
  {
    return capability switch
    {
      "entity_extraction" => CreateEntityExtractionSchema(),
      "relationship_extraction" => CreateRelationshipExtractionSchema(),
      "combined_extraction" => CreateCombinedExtractionSchema(),
      "graph_update_analysis" => CreateGraphUpdateSchema(),
      "entity_validation" => CreateEntityExtractionSchema(),
      "relationship_validation" => CreateRelationshipExtractionSchema(),
      _ => ResponseFormat.JSON
    };
  }

  /// <summary>
  /// Creates JSON schema for entity extraction.
  /// </summary>
  private static ResponseFormat CreateEntityExtractionSchema()
  {
    var entitySchema = JsonSchemaObject.Create("object")
      .WithProperty("name", JsonSchemaProperty.String("The name or identifier of the entity"), required: true)
      .WithProperty("type", JsonSchemaProperty.String("The category or type of the entity (e.g., person, organization, location)"))
      .WithProperty("aliases", JsonSchemaProperty.StringArray("Alternative names or aliases for the entity"))
      .WithProperty("confidence", JsonSchemaProperty.Number("Confidence score between 0.0 and 1.0"))
      .WithProperty("reasoning", JsonSchemaProperty.String("Explanation for why this entity was extracted"))
      .WithDescription("An entity extracted from the content")
      .Build();

    var schema = JsonSchemaObject.Create("array")
      .WithProperty("items", new JsonSchemaProperty { Type = "object", Properties = entitySchema.Properties })
      .WithDescription("List of entities extracted from the content")
      .Build();

    return ResponseFormat.CreateWithSchema("entity_extraction", schema, strictValidation: true);
  }

  /// <summary>
  /// Creates JSON schema for relationship extraction.
  /// </summary>
  private static ResponseFormat CreateRelationshipExtractionSchema()
  {
    var relationshipSchema = JsonSchemaObject.Create("object")
      .WithProperty("source", JsonSchemaProperty.String("The source entity in the relationship"), required: true)
      .WithProperty("relationship_type", JsonSchemaProperty.String("The type of relationship (e.g., works_for, located_in, knows)"), required: true)
      .WithProperty("target", JsonSchemaProperty.String("The target entity in the relationship"), required: true)
      .WithProperty("confidence", JsonSchemaProperty.Number("Confidence score between 0.0 and 1.0"))
      .WithProperty("temporal_context", JsonSchemaProperty.String("Time-related context for the relationship"))
      .WithProperty("reasoning", JsonSchemaProperty.String("Explanation for why this relationship was extracted"))
      .WithDescription("A relationship between two entities")
      .Build();

    var schema = JsonSchemaObject.Create("array")
      .WithProperty("items", new JsonSchemaProperty { Type = "object", Properties = relationshipSchema.Properties })
      .WithDescription("List of relationships extracted from the content")
      .Build();

    return ResponseFormat.CreateWithSchema("relationship_extraction", schema, strictValidation: true);
  }

  /// <summary>
  /// Creates JSON schema for combined entity and relationship extraction.
  /// </summary>
  private static ResponseFormat CreateCombinedExtractionSchema()
  {
    var entitySchema = JsonSchemaObject.Create("object")
      .WithProperty("name", JsonSchemaProperty.String("The name or identifier of the entity"), required: true)
      .WithProperty("type", JsonSchemaProperty.String("The category or type of the entity"))
      .WithProperty("aliases", JsonSchemaProperty.StringArray("Alternative names for the entity"))
      .WithProperty("confidence", JsonSchemaProperty.Number("Confidence score between 0.0 and 1.0"))
      .WithProperty("reasoning", JsonSchemaProperty.String("Explanation for extraction"))
      .Build();

    var relationshipSchema = JsonSchemaObject.Create("object")
      .WithProperty("source", JsonSchemaProperty.String("Source entity"), required: true)
      .WithProperty("relationship_type", JsonSchemaProperty.String("Type of relationship"), required: true)
      .WithProperty("target", JsonSchemaProperty.String("Target entity"), required: true)
      .WithProperty("confidence", JsonSchemaProperty.Number("Confidence score"))
      .WithProperty("temporal_context", JsonSchemaProperty.String("Time context"))
      .WithProperty("reasoning", JsonSchemaProperty.String("Explanation for extraction"))
      .Build();

    var schema = JsonSchemaObject.Create("object")
      .WithProperty("entities", JsonSchemaProperty.Array(JsonSchemaObject.Array(entitySchema), "Extracted entities"), required: true)
      .WithProperty("relationships", JsonSchemaProperty.Array(JsonSchemaObject.Array(relationshipSchema), "Extracted relationships"), required: true)
      .WithDescription("Combined extraction of entities and relationships")
      .Build();

    return ResponseFormat.CreateWithSchema("combined_extraction", schema, strictValidation: true);
  }

  /// <summary>
  /// Creates JSON schema for graph update analysis.
  /// </summary>
  private static ResponseFormat CreateGraphUpdateSchema()
  {
    var updateSchema = JsonSchemaObject.Create("object")
      .WithProperty("operation", JsonSchemaProperty.String("Type of operation: CREATE, UPDATE, DELETE"), required: true)
      .WithProperty("entity_type", JsonSchemaProperty.String("Type of entity being updated"))
      .WithProperty("entity_name", JsonSchemaProperty.String("Name of entity being updated"))
      .WithProperty("reasoning", JsonSchemaProperty.String("Explanation for the update"))
      .WithProperty("confidence", JsonSchemaProperty.Number("Confidence in the update decision"))
      .Build();

    var schema = JsonSchemaObject.Create("object")
      .WithProperty("updates", JsonSchemaProperty.Array(JsonSchemaObject.Array(updateSchema), "List of graph updates"), required: true)
      .WithProperty("summary", JsonSchemaProperty.String("Summary of all updates"))
      .WithDescription("Analysis of required graph updates")
      .Build();

    return ResponseFormat.CreateWithSchema("graph_update_analysis", schema, strictValidation: true);
  }

  private static string ExtractTextFromResponse(IEnumerable<IMessage> response)
  {
    var textMessage = response.OfType<TextMessage>().FirstOrDefault();
    return textMessage?.Text ?? string.Empty;
  }

  private List<ExtractedEntity> ParseEntitiesFromJson(string jsonResponse)
  {
    try
    {
      // Try to extract JSON from response (may be wrapped in markdown)
      var jsonContent = ExtractJsonFromResponse(jsonResponse);
      return JsonSerializer.Deserialize<List<ExtractedEntity>>(jsonContent, _jsonOptions) ?? new List<ExtractedEntity>();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to parse entities from JSON response: {Response}", jsonResponse);
      return new List<ExtractedEntity>();
    }
  }

  private List<ExtractedRelationship> ParseRelationshipsFromJson(string jsonResponse)
  {
    try
    {
      var jsonContent = ExtractJsonFromResponse(jsonResponse);
      return JsonSerializer.Deserialize<List<ExtractedRelationship>>(jsonContent, _jsonOptions) ?? new List<ExtractedRelationship>();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to parse relationships from JSON response: {Response}", jsonResponse);
      return new List<ExtractedRelationship>();
    }
  }

  private CombinedExtractionResult ParseCombinedExtractionFromJson(string jsonResponse)
  {
    try
    {
      var jsonContent = ExtractJsonFromResponse(jsonResponse);
      return JsonSerializer.Deserialize<CombinedExtractionResult>(jsonContent, _jsonOptions) ?? new CombinedExtractionResult();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to parse combined extraction from JSON response: {Response}", jsonResponse);
      return new CombinedExtractionResult();
    }
  }

  private GraphUpdateInstructions ParseUpdateInstructionsFromJson(string jsonResponse)
  {
    try
    {
      var jsonContent = ExtractJsonFromResponse(jsonResponse);
      return JsonSerializer.Deserialize<GraphUpdateInstructions>(jsonContent, _jsonOptions) ?? new GraphUpdateInstructions();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to parse update instructions from JSON response: {Response}", jsonResponse);
      return new GraphUpdateInstructions();
    }
  }

  private static string ExtractJsonFromResponse(string response)
  {
    // Remove markdown code blocks if present
    var lines = response.Split('\n');
    var jsonLines = new List<string>();
    var inJsonBlock = false;
    
    foreach (var line in lines)
    {
      if (line.Trim().StartsWith("```json") || line.Trim().StartsWith("```"))
      {
        inJsonBlock = !inJsonBlock;
        continue;
      }
      
      if (inJsonBlock || (!line.Trim().StartsWith("```") && (line.Trim().StartsWith("{") || line.Trim().StartsWith("["))))
      {
        jsonLines.Add(line);
      }
    }
    
    var jsonContent = string.Join('\n', jsonLines).Trim();
    
    // If no JSON block found, try to find JSON in the response
    if (string.IsNullOrEmpty(jsonContent))
    {
      var startIndex = response.IndexOf('{');
      var startArrayIndex = response.IndexOf('[');
      
      if (startIndex >= 0 && (startArrayIndex < 0 || startIndex < startArrayIndex))
      {
        var endIndex = response.LastIndexOf('}');
        if (endIndex > startIndex)
        {
          jsonContent = response.Substring(startIndex, endIndex - startIndex + 1);
        }
      }
      else if (startArrayIndex >= 0)
      {
        var endIndex = response.LastIndexOf(']');
        if (endIndex > startArrayIndex)
        {
          jsonContent = response.Substring(startArrayIndex, endIndex - startArrayIndex + 1);
        }
      }
    }
    
    return jsonContent;
  }

  #endregion

  #region Data Transfer Objects

  private class ExtractedEntity
  {
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public List<string>? Aliases { get; set; }
    public float Confidence { get; set; } = 1.0f;
    public string? Reasoning { get; set; }
  }

  private class ExtractedRelationship
  {
    public string Source { get; set; } = string.Empty;
    public string RelationshipType { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public float Confidence { get; set; } = 1.0f;
    public string? TemporalContext { get; set; }
    public string? Reasoning { get; set; }
  }

  private class CombinedExtractionResult
  {
    public List<ExtractedEntity> Entities { get; set; } = new();
    public List<ExtractedRelationship> Relationships { get; set; } = new();
  }

  #endregion
} 