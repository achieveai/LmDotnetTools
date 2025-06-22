using System.Text.Json;
using MemoryServer.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

namespace MemoryServer.Services;

/// <summary>
/// Service for extracting entities and relationships from conversation content using LLM providers.
/// Implements the Strategy pattern for different extraction approaches with JSON schema support.
/// </summary>
public class GraphExtractionService : IGraphExtractionService
{
  private readonly IPromptReader _promptReader;
  private readonly ILogger<GraphExtractionService> _logger;
  private readonly MemoryServerOptions _options;
  private readonly ILmConfigService _lmConfigService;
  private readonly JsonSerializerOptions _jsonOptions;

  public GraphExtractionService(
    IPromptReader promptReader,
    ILogger<GraphExtractionService> logger,
    IOptions<MemoryServerOptions> options,
    ILmConfigService lmConfigService)
  {
    _promptReader = promptReader ?? throw new ArgumentNullException(nameof(promptReader));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _lmConfigService = lmConfigService ?? throw new ArgumentNullException(nameof(lmConfigService));
    
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

      var agent = await _lmConfigService.CreateAgentAsync("small-model;json_schema", cancellationToken);
      var promptChain = _promptReader.GetPromptChain("entity_extraction");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> { ["content"] = content });
      var generateOptions = await CreateGenerateReplyOptionsAsync("entity_extraction", cancellationToken);
      var response = await agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      
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

      var agent = await _lmConfigService.CreateAgentAsync("small-model;json_schema", cancellationToken);
      var promptChain = _promptReader.GetPromptChain("combined_extraction");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> { ["content"] = content });
      var generateOptions = await CreateGenerateReplyOptionsAsync("combined_extraction", cancellationToken);
      var response = await agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      
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
        RelationshipType = r.ActualRelationshipType,
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

      var agent = await _lmConfigService.CreateAgentAsync("small-model;json_schema", cancellationToken);
      var promptChain = _promptReader.GetPromptChain("graph_update_analysis");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> 
      { 
        ["content"] = content,
        ["existing_entities_json"] = entitiesJson,
        ["existing_relationships_json"] = relationshipsJson
      });
      
      var generateOptions = await CreateGenerateReplyOptionsAsync("graph_update_analysis", cancellationToken);
      var response = await agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
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

      var agent = await _lmConfigService.CreateAgentAsync("small-model;json_schema", cancellationToken);
      var promptChain = _promptReader.GetPromptChain("entity_validation");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> { ["entities_json"] = entitiesJson });
      var generateOptions = await CreateGenerateReplyOptionsAsync("entity_validation", cancellationToken);
      var response = await agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      
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

      var agent = await _lmConfigService.CreateAgentAsync("small-model;json_schema", cancellationToken);
      var promptChain = _promptReader.GetPromptChain("relationship_validation");
      var messages = promptChain.PromptMessages(new Dictionary<string, object> { ["relationships_json"] = relationshipsJson });
      var generateOptions = await CreateGenerateReplyOptionsAsync("relationship_validation", cancellationToken);
      var response = await agent.GenerateReplyAsync(messages, generateOptions, cancellationToken);
      
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
          RelationshipType = r.ActualRelationshipType,
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
      // Use LmConfig for optimal model selection based on capability
      // Request small, cost-effective models with JSON schema support for graph extraction
      var modelConfig = await _lmConfigService.GetOptimalModelAsync("small-model;json_schema", cancellationToken);
      if (modelConfig != null)
      {
        var options = new GenerateReplyOptions
        {
          ModelId = modelConfig.Id, // UnifiedAgent will translate this to the effective model name
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

      // Fallback to basic configuration if no model found
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
    var schema = SchemaHelper.CreateJsonSchemaFromType(typeof(EntityExtractionWrapper));
    return ResponseFormat.CreateWithSchema("entity_extraction", schema, strictValidation: true);
  }

  /// <summary>
  /// Creates JSON schema for relationship extraction.
  /// </summary>
  private static ResponseFormat CreateRelationshipExtractionSchema()
  {
    var schema = SchemaHelper.CreateJsonSchemaFromType(typeof(RelationshipExtractionWrapper));
    return ResponseFormat.CreateWithSchema("relationship_extraction", schema, strictValidation: true);
  }

  /// <summary>
  /// Creates JSON schema for combined entity and relationship extraction.
  /// </summary>
  private static ResponseFormat CreateCombinedExtractionSchema()
  {
    var schema = SchemaHelper.CreateJsonSchemaFromType(typeof(CombinedExtractionResult));
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
      
      // First try to parse as direct array (legacy format)
      try
      {
        return JsonSerializer.Deserialize<List<ExtractedEntity>>(jsonContent, _jsonOptions) ?? new List<ExtractedEntity>();
      }
      catch
      {
        // If that fails, try to parse as wrapped object with "entities" property
        var wrappedResult = JsonSerializer.Deserialize<EntityExtractionWrapper>(jsonContent, _jsonOptions);
        return wrappedResult?.Entities ?? new List<ExtractedEntity>();
      }
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
      
      // First try to parse as direct array (legacy format)
      try
      {
        return JsonSerializer.Deserialize<List<ExtractedRelationship>>(jsonContent, _jsonOptions) ?? new List<ExtractedRelationship>();
      }
      catch
      {
        // If that fails, try to parse as wrapped object with "relationships" property
        var wrappedResult = JsonSerializer.Deserialize<RelationshipExtractionWrapper>(jsonContent, _jsonOptions);
        return wrappedResult?.Relationships ?? new List<ExtractedRelationship>();
      }
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
      _logger.LogDebug("EXTRACTED JSON CONTENT: {JsonContent}", jsonContent);
      
      var result = JsonSerializer.Deserialize<CombinedExtractionResult>(jsonContent, _jsonOptions) ?? new CombinedExtractionResult();
      
      _logger.LogDebug("PARSED RESULT: {EntityCount} entities, {RelationshipCount} relationships", 
        result.Entities.Count, result.Relationships.Count);
      
      // Log relationship details for debugging
      foreach (var rel in result.Relationships.Take(3))
      {
        _logger.LogDebug("PARSED RELATIONSHIP: Source='{Source}', Type='{Type}', Target='{Target}', Confidence={Confidence}", 
          rel.Source, rel.RelationshipType, rel.Target, rel.Confidence);
      }
      
      return result;
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
    if (string.IsNullOrWhiteSpace(response))
      return string.Empty;

    // First, try to find JSON within markdown code blocks
    var lines = response.Split('\n');
    var jsonLines = new List<string>();
    var inJsonBlock = false;
    
    foreach (var line in lines)
    {
      var trimmedLine = line.Trim();
      if (trimmedLine.StartsWith("```json") || (trimmedLine == "```" && inJsonBlock))
      {
        inJsonBlock = !inJsonBlock;
        continue;
      }
      
      if (inJsonBlock)
      {
        jsonLines.Add(line);
      }
    }
    
    if (jsonLines.Count > 0)
    {
      var jsonContent = string.Join('\n', jsonLines).Trim();
      if (!string.IsNullOrEmpty(jsonContent))
        return jsonContent;
    }
    
    // If no markdown blocks found, try to extract JSON directly
    // Look for the first '{' or '[' and find the matching closing brace/bracket
    var startIndex = -1;
    var isObject = false;
    
    for (int i = 0; i < response.Length; i++)
    {
      if (response[i] == '{')
      {
        startIndex = i;
        isObject = true;
        break;
      }
      else if (response[i] == '[')
      {
        startIndex = i;
        isObject = false;
        break;
      }
    }
    
    if (startIndex >= 0)
    {
      var braceCount = 0;
      var bracketCount = 0;
      var inString = false;
      var escaped = false;
      
      for (int i = startIndex; i < response.Length; i++)
      {
        var c = response[i];
        
        if (escaped)
        {
          escaped = false;
          continue;
        }
        
        if (c == '\\' && inString)
        {
          escaped = true;
          continue;
        }
        
        if (c == '"')
        {
          inString = !inString;
          continue;
        }
        
        if (!inString)
        {
          if (c == '{') braceCount++;
          else if (c == '}') braceCount--;
          else if (c == '[') bracketCount++;
          else if (c == ']') bracketCount--;
          
          // Check if we've closed all braces/brackets
          if (isObject && braceCount == 0 && i > startIndex)
          {
            return response.Substring(startIndex, i - startIndex + 1);
          }
          else if (!isObject && bracketCount == 0 && i > startIndex)
          {
            return response.Substring(startIndex, i - startIndex + 1);
          }
        }
      }
    }
    
    return string.Empty;
  }

  #endregion

  #region Data Transfer Objects

  private class ExtractedEntity
  {
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }
    
    [JsonPropertyName("confidence")]
    public float Confidence { get; set; } = 1.0f;
    
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
  }

  private class ExtractedRelationship
  {
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;
    
    [JsonPropertyName("relationship_type")]
    public string RelationshipType { get; set; } = string.Empty;
    
    // Fallback property in case LLM uses "type" instead of "relationship_type"
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
    
    [JsonPropertyName("confidence")]
    public float Confidence { get; set; } = 1.0f;
    
    [JsonPropertyName("temporal_context")]
    public string? TemporalContext { get; set; }
    
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
    
    // Property to get the actual relationship type, with fallback logic
    [JsonIgnore]
    public string ActualRelationshipType => 
      !string.IsNullOrWhiteSpace(RelationshipType) ? RelationshipType :
      !string.IsNullOrWhiteSpace(Type) ? Type :
      "related_to"; // Default fallback
  }

  private class CombinedExtractionResult
  {
    [JsonPropertyName("entities")]
    public List<ExtractedEntity> Entities { get; set; } = new();
    
    [JsonPropertyName("relationships")]
    public List<ExtractedRelationship> Relationships { get; set; } = new();
  }

  private class EntityExtractionWrapper
  {
    [JsonPropertyName("entities")]
    public List<ExtractedEntity> Entities { get; set; } = new();
  }

  private class RelationshipExtractionWrapper
  {
    [JsonPropertyName("relationships")]
    public List<ExtractedRelationship> Relationships { get; set; } = new();
  }

  #endregion
} 