using System.Text.Json;
using MemoryServer.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Prompts;

namespace MemoryServer.Services;

/// <summary>
/// Service for extracting entities and relationships from conversation content using LLM providers.
/// Implements the Strategy pattern for different extraction approaches.
/// </summary>
public class GraphExtractionService : IGraphExtractionService
{
  private readonly IAgent _agent;
  private readonly IPromptReader _promptReader;
  private readonly ILogger<GraphExtractionService> _logger;
  private readonly JsonSerializerOptions _jsonOptions;

  public GraphExtractionService(
    IAgent agent,
    IPromptReader promptReader,
    ILogger<GraphExtractionService> logger)
  {
    _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    _promptReader = promptReader ?? throw new ArgumentNullException(nameof(promptReader));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    
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
      var response = await _agent.GenerateReplyAsync(messages, cancellationToken: cancellationToken);
      
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
      
      var response = await _agent.GenerateReplyAsync(messages, cancellationToken: cancellationToken);
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
      var response = await _agent.GenerateReplyAsync(messages, cancellationToken: cancellationToken);
      
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
      
      var response = await _agent.GenerateReplyAsync(messages, cancellationToken: cancellationToken);
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
      var response = await _agent.GenerateReplyAsync(messages, cancellationToken: cancellationToken);
      
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
      var response = await _agent.GenerateReplyAsync(messages, cancellationToken: cancellationToken);
      
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