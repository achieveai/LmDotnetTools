# Document Segmentation Feature - Functional Specification

## Executive Summary

This document specifies the intelligent document segmentation feature for the LmDotnetTools Memory MCP Server. The feature enables automatic segmentation of large documents into semantically coherent smaller segments using Large Language Model (LLM) analysis, specifically targeting efficient models like GPT-4.1-nano or Flash-lite for cost-effective processing.

### Objectives

- **Intelligent Segmentation**: Use LLM analysis to identify natural break points and semantic boundaries in large documents
- **Cost-Effective Processing**: Leverage fast, efficient models (GPT-4.1-nano, Flash-lite) for segmentation decisions
- **Seamless Integration**: Integrate with existing memory pipeline as a preprocessing step before fact extraction
- **Quality Preservation**: Maintain document context and semantic coherence across segments
- **Performance Optimization**: Enable parallel processing of large documents through intelligent chunking

## System Context

### Current Architecture Integration

The document segmentation feature integrates into the existing LmDotnetTools Memory MCP Server architecture as a preprocessing component in the memory addition pipeline:

```
┌─────────────────────────────────────────────────────────────┐
│               Memory Addition Pipeline (Enhanced)           │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Input     │  │  Document   │  │    Existing         │  │
│  │ Validation  │  │Segmentation │  │    Pipeline         │  │
│  │             │  │  (NEW)      │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │    Fact     │  │   Memory    │  │     Vector          │  │
│  │ Extraction  │  │   Storage   │  │   Embedding         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### Integration Points

1. **Pre-Processing Stage**: Document segmentation occurs before fact extraction
2. **Database Session Pattern**: Leverages existing session management for reliable resource handling
3. **LLM Provider Integration**: Uses existing OpenAI/Anthropic provider abstractions
4. **Session Isolation**: Maintains existing session-based data separation
5. **Memory Storage**: Each segment becomes a separate memory item with relationship metadata

## Functional Requirements

### FR-1: Document Size Detection and Routing

**Requirement**: The system shall automatically detect when input content exceeds configurable size thresholds and route it through the segmentation pipeline.

**Acceptance Criteria**:

- Configurable size thresholds (character count, word count, token estimate)
- Automatic detection without user intervention
- Graceful handling of borderline cases
- Logging of routing decisions for monitoring

**Default Thresholds**:

- Character count: 8,000 characters
- Word count: 1,500 words  
- Token estimate: 2,000 tokens (approximate)

### FR-2: LLM-Based Semantic Segmentation

**Requirement**: The system shall use LLM analysis to identify optimal segmentation points based on semantic boundaries, topic changes, and logical document structure.

**Acceptance Criteria**:

- Support for multiple LLM providers (OpenAI GPT-4.1-nano, Anthropic Flash-lite)
- Intelligent identification of natural break points
- Preservation of semantic coherence within segments
- Handling of various document types (articles, research papers, transcripts, reports)
- Configurable segmentation strategies per document type

**Segmentation Strategies**:

- **Topic-Based**: Segment by topic or subject changes
- **Structure-Based**: Segment by document structure (sections, chapters, headings)
- **Narrative-Based**: Segment by narrative flow or logical progression
- **Hybrid**: Combine multiple strategies based on document characteristics

### FR-3: Segment Size Management

**Requirement**: The system shall ensure that generated segments fall within optimal size ranges for downstream processing while maintaining semantic integrity.

**Acceptance Criteria**:

- Target segment sizes configurable per use case
- Minimum segment size enforcement to prevent over-fragmentation
- Maximum segment size limits to ensure processability
- Overlap management between adjacent segments for context preservation

**Size Targets**:

- Target segment size: 500-1,500 words
- Minimum segment size: 100 words
- Maximum segment size: 2,000 words
- Context overlap: 50-100 words between adjacent segments

### FR-4: Segment Relationship Management

**Requirement**: The system shall maintain and store relationships between document segments to preserve document coherence and enable reconstruction.

**Acceptance Criteria**:

- Sequential relationship tracking (segment order)
- Parent document reference for all segments
- Cross-reference support for segments that reference each other
- Metadata preservation from original document to segments

**Relationship Types**:

- **Sequential**: Order of segments within the document
- **Hierarchical**: Parent-child relationships for nested content
- **Referential**: Cross-references between segments
- **Topical**: Segments covering related topics

### FR-5: Segment Quality Validation

**Requirement**: The system shall validate segment quality and coherence before storing in the memory system.

**Acceptance Criteria**:

- Semantic coherence validation using LLM analysis
- Completeness checks to ensure no content loss
- Quality scoring for segments
- Automatic retry with different segmentation strategies for low-quality results

**Quality Metrics**:

- Semantic coherence score (0.0-1.0)
- Completeness verification (no missing content)
- Segment independence score (ability to stand alone)
- Topic consistency within segments

## Technical Requirements

### TR-1: Component Architecture

**Document Segmentation Service Interface**:

```csharp
namespace LmDotnetTools.McpServers.Memory.DocumentSegmentation;

public interface IDocumentSegmentationService
{
  Task<DocumentSegmentationResult> SegmentDocumentAsync(
    string content,
    DocumentSegmentationRequest request,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);
  
  Task<bool> ShouldSegmentAsync(
    string content,
    DocumentType documentType,
    CancellationToken cancellationToken = default);
  
  Task<SegmentationStrategy> DetermineOptimalStrategyAsync(
    string content,
    DocumentType documentType,
    CancellationToken cancellationToken = default);
}

public class DocumentSegmentationRequest
{
  public DocumentType DocumentType { get; set; } = DocumentType.Generic;
  public SegmentationStrategy Strategy { get; set; } = SegmentationStrategy.Hybrid;
  public int TargetSegmentSize { get; set; } = 1000; // words
  public int MaxSegmentSize { get; set; } = 2000; // words
  public int MinSegmentSize { get; set; } = 100; // words
  public int ContextOverlap { get; set; } = 75; // words
  public Dictionary<string, object>? Metadata { get; set; }
}

public class DocumentSegmentationResult
{
  public List<DocumentSegment> Segments { get; set; } = new();
  public SegmentationMetadata Metadata { get; set; } = new();
  public List<SegmentRelationship> Relationships { get; set; } = new();
  public bool IsComplete { get; set; }
  public List<string> Warnings { get; set; } = new();
}

public class DocumentSegment
{
  public string Id { get; set; } = string.Empty; // Unique segment identifier
  public string Content { get; set; } = string.Empty;
  public int SequenceNumber { get; set; }
  public string Title { get; set; } = string.Empty; // LLM-generated segment title
  public string Summary { get; set; } = string.Empty; // LLM-generated summary
  public SegmentQuality Quality { get; set; } = new();
  public Dictionary<string, object> Metadata { get; set; } = new();
}

public class SegmentQuality
{
  public double CoherenceScore { get; set; } // 0.0-1.0
  public double IndependenceScore { get; set; } // 0.0-1.0
  public double TopicConsistencyScore { get; set; } // 0.0-1.0
  public bool PassesQualityThreshold { get; set; }
  public List<string> QualityIssues { get; set; } = new();
}

public enum DocumentType
{
  Generic,
  ResearchPaper,
  Article,
  Transcript,
  Report,
  Documentation,
  Email,
  Chat,
  Legal,
  Technical
}

public enum SegmentationStrategy
{
  TopicBased,
  StructureBased,
  NarrativeBased,
  Hybrid,
  Custom
}
```

### TR-2: LLM Integration Specifications

**YAML-Based Prompt Configuration System**:

**Prompt Configuration Structure** (`prompts.yml`):

```yaml
# Document Segmentation Prompts Configuration
segmentation_prompts:
  version: "1.0"
  default_language: "en"
  
  # Strategy determination prompt
  strategy_determination:
    system_prompt: |
      You are a document analysis expert. Analyze the following document to determine the optimal segmentation strategy.
    user_prompt: |
      Document Type: {DocumentType}
      Document Length: {DocumentLength} words

      Document Content:
      {DocumentContent}

      Analyze the document structure and content to determine which segmentation strategy would work best:

      1. **Topic-Based**: If the document has clear topic shifts and thematic boundaries
      2. **Structure-Based**: If the document has clear headings, sections, or hierarchical organization
      3. **Narrative-Based**: If the document follows a logical sequence, story, or process flow
      4. **Hybrid**: If the document would benefit from combining multiple approaches

      Return your analysis in JSON format:
      {{
        "recommended_strategy": "topic_based",
        "confidence": 0.85,
        "reasoning": "Document shows clear topic shifts with distinct thematic sections",
        "document_characteristics": {{
          "has_clear_headings": true,
          "has_topic_shifts": true,
          "has_narrative_flow": false,
          "complexity_level": "medium"
        }}
      }}
    expected_format: "json"
    max_tokens: 800

  # Topic-based segmentation
  topic_based:
    system_prompt: |
      You are a topic analysis expert. Segment this document based on thematic and topical boundaries.
    user_prompt: |
      Document Type: {DocumentType}
      Target Segment Size: {TargetSegmentSize} words

      Document Content:
      {DocumentContent}

      Identify segmentation points where topics or themes change. Look for:
      - Shifts in subject matter or focus
      - Introduction of new concepts or ideas
      - Transitions between different aspects of the main topic
      - Natural thematic boundaries

      Each segment should focus on a cohesive topic or theme.

      Return segmentation points in JSON format:
      {{
        "segmentation_points": [
          {{
            "position": 150,
            "reason": "Topic shift from background to methodology",
            "confidence": 0.85,
            "segment_title": "Background and Context",
            "topic_summary": "Introduction to the problem domain"
          }}
        ],
        "strategy_used": "topic_based",
        "quality_assessment": {{
          "topic_coherence": 0.9,
          "thematic_consistency": 0.85
        }}
      }}
    expected_format: "json"
    max_tokens: 1200

  # Structure-based segmentation
  structure_based:
    system_prompt: |
      You are a document structure expert. Segment this document based on its organizational structure and formatting cues.
    user_prompt: |
      Document Type: {DocumentType}
      Target Segment Size: {TargetSegmentSize} words

      Document Content:
      {DocumentContent}

      Identify segmentation points based on structural elements:
      - Headings and subheadings (# ## ### etc.)
      - Section breaks and chapter divisions
      - Numbered or bulleted lists that represent major sections
      - Clear formatting indicators of document organization

      Respect the author's intended structural divisions while ensuring segments are appropriately sized.

      Return segmentation points in JSON format:
      {{
        "segmentation_points": [
          {{
            "position": 200,
            "reason": "Section heading: 'Methodology'",
            "confidence": 0.95,
            "segment_title": "Introduction",
            "structural_element": "heading_level_2"
          }}
        ],
        "strategy_used": "structure_based",
        "quality_assessment": {{
          "structural_clarity": 0.9,
          "hierarchical_consistency": 0.85
        }}
      }}
    expected_format: "json"
    max_tokens: 1200

  # Narrative-based segmentation
  narrative_based:
    system_prompt: |
      You are a narrative flow expert. Segment this document based on logical progression, story flow, or process sequences.
    user_prompt: |
      Document Type: {DocumentType}
      Target Segment Size: {TargetSegmentSize} words

      Document Content:
      {DocumentContent}

      Identify segmentation points based on narrative progression:
      - Logical sequence breaks (problem → analysis → solution)
      - Temporal progression points
      - Cause-and-effect relationships
      - Process step transitions
      - Story beats or narrative arcs

      Each segment should represent a complete logical unit in the overall flow.

      Return segmentation points in JSON format:
      {{
        "segmentation_points": [
          {{
            "position": 180,
            "reason": "Transition from problem statement to analysis phase",
            "confidence": 0.88,
            "segment_title": "Problem Definition",
            "narrative_function": "setup_to_analysis"
          }}
        ],
        "strategy_used": "narrative_based",
        "quality_assessment": {{
          "logical_flow": 0.9,
          "progression_clarity": 0.85
        }}
      }}
    expected_format: "json"
    max_tokens: 1200

  # Hybrid segmentation
  hybrid:
    system_prompt: |
      You are a comprehensive document analysis expert. Use a hybrid approach combining multiple segmentation strategies.
    user_prompt: |
      Document Type: {DocumentType}
      Target Segment Size: {TargetSegmentSize} words

      Document Content:
      {DocumentContent}

      Apply the most appropriate combination of strategies:
      - Use structural cues where they exist (headings, sections)
      - Identify topic shifts within structural sections
      - Maintain narrative flow and logical progression
      - Ensure each segment is coherent and self-contained

      Prioritize the strategy that works best for each part of the document.

      Return segmentation points in JSON format:
      {{
        "segmentation_points": [
          {{
            "position": 175,
            "reason": "Section heading combined with topic shift",
            "confidence": 0.90,
            "segment_title": "Literature Review",
            "strategies_applied": ["structure_based", "topic_based"],
            "primary_strategy": "structure_based"
          }}
        ],
        "strategy_used": "hybrid",
        "quality_assessment": {{
          "overall_coherence": 0.88,
          "strategy_effectiveness": 0.85
        }}
      }}
    expected_format: "json"
    max_tokens: 1500

  # Quality validation
  quality_validation:
    system_prompt: |
      You are a document quality assessment expert. Evaluate the quality and coherence of document segments.
    user_prompt: |
      Segment Content:
      {SegmentContent}

      Context (Previous segment ending):
      {PreviousContext}

      Context (Next segment beginning):
      {NextContext}

      Assess the segment on:
      1. Semantic coherence (does it make sense as a standalone unit?)
      2. Completeness (does it cover a complete thought or topic?)
      3. Independence (can it be understood without adjacent segments?)
      4. Topic consistency (does it maintain focus on a single topic/theme?)

      Return assessment in JSON format:
      {{
        "coherence_score": 0.85,
        "independence_score": 0.90,
        "topic_consistency_score": 0.88,
        "passes_quality_threshold": true,
        "suggested_title": "Document Analysis and Methodology",
        "brief_summary": "Overview of analytical approach and methods used",
        "quality_issues": []
      }}
    expected_format: "json"
    max_tokens: 600

# Domain-specific prompt configurations
domain_prompts:
  legal:
    custom_instructions: |
      - Segment by legal concepts (definitions, statutes, case law, analysis)
      - Maintain legal argument structure and precedent relationships
      - Ensure each segment contains complete legal thoughts
      - Preserve citation context and legal references
      - Follow legal document conventions (preamble, body, conclusions)
    
  technical:
    custom_instructions: |
      - Segment by technical components or system layers
      - Group related procedures and implementation details
      - Maintain dependency relationships between technical concepts
      - Ensure code examples and configurations stay with explanations
      - Preserve troubleshooting steps and diagnostic information
    
  research:
    custom_instructions: |
      - Follow academic structure (Abstract, Introduction, Literature Review, Methodology, Results, Discussion, Conclusion)
      - Preserve research methodology integrity
      - Keep related hypotheses and findings together
      - Maintain statistical analysis context
      - Ensure proper citation and reference grouping

  medical:
    custom_instructions: |
      - Segment by medical concepts (symptoms, diagnosis, treatment, prognosis)
      - Maintain clinical reasoning flow
      - Preserve patient safety information integrity
      - Group related diagnostic and treatment information
      - Follow medical documentation standards

# Multi-language support
languages:
  en:
    strategy_determination: "strategy_determination"
    topic_based: "topic_based"
    structure_based: "structure_based"
    narrative_based: "narrative_based"
    hybrid: "hybrid"
    quality_validation: "quality_validation"
  
  # Future language support can be added here
  # es:
  #   strategy_determination: "strategy_determination_es"
  #   ...
```

**Prompt Loading and Management Interface**:

```csharp
public interface ISegmentationPromptManager
{
  Task<PromptTemplate> GetPromptAsync(SegmentationStrategy strategy, string language = "en");
  Task<PromptTemplate> GetQualityValidationPromptAsync(string language = "en");
  Task<string> GetDomainInstructionsAsync(DocumentType documentType, string language = "en");
  Task ReloadPromptsAsync();
  Task<bool> ValidatePromptConfigurationAsync();
}

public class PromptTemplate
{
  public string SystemPrompt { get; set; } = string.Empty;
  public string UserPrompt { get; set; } = string.Empty;
  public string ExpectedFormat { get; set; } = "json";
  public int MaxTokens { get; set; } = 1000;
  public Dictionary<string, object> Metadata { get; set; } = new();
}

public class SegmentationPromptManager : ISegmentationPromptManager
{
  private readonly IConfiguration _configuration;
  private readonly ILogger<SegmentationPromptManager> _logger;
  private readonly string _promptsFilePath;
  private PromptConfiguration? _prompts;

  public SegmentationPromptManager(
    IConfiguration configuration,
    ILogger<SegmentationPromptManager> logger)
  {
    _configuration = configuration;
    _logger = logger;
    _promptsFilePath = configuration.GetValue<string>("SegmentationPrompts:FilePath") 
                       ?? "prompts.yml";
  }

  public async Task<PromptTemplate> GetPromptAsync(SegmentationStrategy strategy, string language = "en")
  {
    await EnsurePromptsLoadedAsync();
    
    var promptKey = strategy switch
    {
      SegmentationStrategy.TopicBased => "topic_based",
      SegmentationStrategy.StructureBased => "structure_based", 
      SegmentationStrategy.NarrativeBased => "narrative_based",
      SegmentationStrategy.Hybrid => "hybrid",
      SegmentationStrategy.Custom => "custom",
      _ => "hybrid"
    };

    if (_prompts?.SegmentationPrompts?.TryGetValue(promptKey, out var prompt) == true)
    {
      return prompt;
    }

    _logger.LogWarning("Prompt not found for strategy {Strategy}, using default hybrid", strategy);
    return _prompts?.SegmentationPrompts?["hybrid"] ?? throw new InvalidOperationException("No prompts available");
  }

  private async Task EnsurePromptsLoadedAsync()
  {
    if (_prompts == null)
    {
      await LoadPromptsAsync();
    }
  }

  private async Task LoadPromptsAsync()
  {
    try
    {
      var yamlContent = await File.ReadAllTextAsync(_promptsFilePath);
      var deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();
        
      _prompts = deserializer.Deserialize<PromptConfiguration>(yamlContent);
      _logger.LogInformation("Successfully loaded segmentation prompts from {FilePath}", _promptsFilePath);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to load segmentation prompts from {FilePath}", _promptsFilePath);
      throw;
    }
  }
}
```

### TR-3: Database Session Pattern Integration

**Repository Interface for Segment Storage**:

```csharp
public interface IDocumentSegmentRepository
{
  Task<List<int>> StoreSegmentsAsync(
    ISqliteSession session,
    List<DocumentSegment> segments,
    string parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);
  
  Task<List<DocumentSegment>> GetDocumentSegmentsAsync(
    ISqliteSession session,
    string parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);
  
  Task StoreSegmentRelationshipsAsync(
    ISqliteSession session,
    List<SegmentRelationship> relationships,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);
}
```

### TR-4: Performance Requirements

**Response Time Requirements**:

- Document analysis and segmentation: < 30 seconds for documents up to 10,000 words
- Quality validation per segment: < 5 seconds
- Total processing time: < 60 seconds for documents up to 20,000 words

**Throughput Requirements**:

- Support concurrent segmentation of up to 10 documents
- Process segments in parallel where possible
- Efficient LLM API usage through batching and caching

**Resource Usage**:

- Memory usage: < 100MB for processing documents up to 50,000 words
- Database sessions: Proper cleanup and resource management
- LLM API efficiency: Minimize API calls through intelligent prompting

### TR-5: Configuration Management

**Configuration Structure**:

```csharp
public class DocumentSegmentationOptions
{
  public SegmentationThresholds Thresholds { get; set; } = new();
  public LlmSegmentationOptions LlmOptions { get; set; } = new();
  public QualityOptions Quality { get; set; } = new();
  public PerformanceOptions Performance { get; set; } = new();
  public PromptOptions Prompts { get; set; } = new();
}

public class PromptOptions
{
  public string FilePath { get; set; } = "prompts.yml";
  public string DefaultLanguage { get; set; } = "en";
  public bool EnableHotReload { get; set; } = true;
  public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromMinutes(30);
  public Dictionary<string, string> CustomPromptPaths { get; set; } = new();
}

public class SegmentationThresholds
{
  public int CharacterThreshold { get; set; } = 8000;
  public int WordThreshold { get; set; } = 1500;
  public int TokenThreshold { get; set; } = 2000;
}

public class LlmSegmentationOptions
{
  public string PreferredProvider { get; set; } = "openai";
  public string Model { get; set; } = "gpt-4.1-nano";
  public string FallbackModel { get; set; } = "gpt-4o-mini";
  public double Temperature { get; set; } = 0.1;
  public int MaxTokens { get; set; } = 1000;
  public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public class QualityOptions
{
  public double MinCoherenceScore { get; set; } = 0.7;
  public double MinIndependenceScore { get; set; } = 0.6;
  public double MinTopicConsistencyScore { get; set; } = 0.7;
  public bool EnableQualityValidation { get; set; } = true;
  public int MaxRetryAttempts { get; set; } = 2;
}
```

**Configuration File Example** (`appsettings.json`):

```json
{
  "DocumentSegmentation": {
    "Thresholds": {
      "CharacterThreshold": 8000,
      "WordThreshold": 1500,
      "TokenThreshold": 2000
    },
    "LlmOptions": {
      "PreferredProvider": "openai",
      "Model": "gpt-4.1-nano",
      "FallbackModel": "gpt-4o-mini",
      "Temperature": 0.1,
      "MaxTokens": 1000,
      "Timeout": "00:00:30"
    },
    "Quality": {
      "MinCoherenceScore": 0.7,
      "MinIndependenceScore": 0.6,
      "MinTopicConsistencyScore": 0.7,
      "EnableQualityValidation": true,
      "MaxRetryAttempts": 2
    },
    "Prompts": {
      "FilePath": "Configuration/Prompts/segmentation-prompts.yml",
      "DefaultLanguage": "en",
      "EnableHotReload": true,
      "CacheExpiration": "00:30:00",
      "CustomPromptPaths": {
        "legal": "Configuration/Prompts/legal-segmentation.yml",
        "medical": "Configuration/Prompts/medical-segmentation.yml"
      }
    }
  }
}
```

**Benefits of YAML-Based Prompt Management**:

1. **Maintainability**: Easy to update prompts without code changes
2. **Version Control**: Track prompt changes and A/B test different versions
3. **Localization**: Support multiple languages through separate prompt files
4. **Domain Specialization**: Custom prompt files for specific domains
5. **Hot Reload**: Update prompts in production without service restart
6. **Collaboration**: Non-developers can modify prompts for optimization
7. **Testing**: Easy to create test-specific prompt variations

## LLM Integration Specifications

### LIS-1: Model Selection Strategy

**Primary Models**:

- **GPT-4.1-nano**: Primary choice for cost-effective, high-quality segmentation
- **GPT-3.5-turbo**: Fallback option for broader availability
- **Claude-3-haiku**: Alternative fast model for Anthropic users

**Model Selection Logic**:
```csharp
public class SegmentationModelSelector
{
  public async Task<string> SelectOptimalModelAsync(
    int documentLength,
    DocumentType documentType,
    PerformanceRequirements requirements)
  {
    // Logic for selecting the best model based on:
    // - Document complexity
    // - Required processing speed
    // - Cost constraints
    // - Quality requirements
  }
}
```

### LIS-2: Strategy-Specific Prompt Engineering

**Multi-Step Prompting Strategy**:

1. **Strategy Determination**: Use general analysis to determine optimal strategy
2. **Strategy-Specific Segmentation**: Apply specialized prompts based on chosen strategy
3. **Quality Validation**: Validate segment quality using strategy-aware criteria
4. **Title and Summary Generation**: Generate metadata using strategy context

**Prompt Selection Logic**:

```csharp
public class StrategyPromptSelector
{
  public string GetSegmentationPrompt(SegmentationStrategy strategy, DocumentType documentType)
  {
    return strategy switch
    {
      SegmentationStrategy.TopicBased => SegmentationPromptTemplate.TOPIC_BASED_SEGMENTATION,
      SegmentationStrategy.StructureBased => SegmentationPromptTemplate.STRUCTURE_BASED_SEGMENTATION,
      SegmentationStrategy.NarrativeBased => SegmentationPromptTemplate.NARRATIVE_BASED_SEGMENTATION,
      SegmentationStrategy.Hybrid => SegmentationPromptTemplate.HYBRID_SEGMENTATION,
      SegmentationStrategy.Custom => GetCustomPrompt(documentType),
      _ => SegmentationPromptTemplate.HYBRID_SEGMENTATION
    };
  }
  
  private string GetCustomPrompt(DocumentType documentType)
  {
    var domainInstructions = documentType switch
    {
      DocumentType.Legal => GetLegalSegmentationInstructions(),
      DocumentType.Technical => GetTechnicalSegmentationInstructions(),
      DocumentType.ResearchPaper => GetResearchPaperInstructions(),
      DocumentType.Medical => GetMedicalSegmentationInstructions(),
      _ => GetGenericInstructions()
    };
    
    return SegmentationPromptTemplate.CUSTOM_SEGMENTATION_TEMPLATE
      .Replace("{DomainSpecificInstructions}", domainInstructions);
  }
}
```

**Domain-Specific Instruction Examples**:

```csharp
private string GetLegalSegmentationInstructions()
{
  return @"
- Segment by legal concepts (definitions, statutes, case law, analysis)
- Maintain legal argument structure and precedent relationships
- Ensure each segment contains complete legal thoughts
- Preserve citation context and legal references
- Follow legal document conventions (preamble, body, conclusions)";
}

private string GetTechnicalSegmentationInstructions()
{
  return @"
- Segment by technical components or system layers
- Group related procedures and implementation details
- Maintain dependency relationships between technical concepts
- Ensure code examples and configurations stay with explanations
- Preserve troubleshooting steps and diagnostic information";
}

private string GetResearchPaperInstructions()
{
  return @"
- Follow academic structure (Abstract, Introduction, Literature Review, Methodology, Results, Discussion, Conclusion)
- Preserve research methodology integrity
- Keep related hypotheses and findings together
- Maintain statistical analysis context
- Ensure proper citation and reference grouping";
}
```

**Strategy-Specific Quality Validation**:

Each strategy has specific quality criteria:

- **Topic-Based**: Focus on thematic coherence and topic consistency
- **Structure-Based**: Emphasize structural integrity and hierarchical relationships  
- **Narrative-Based**: Prioritize logical flow and causal relationships
- **Hybrid**: Balance multiple quality dimensions based on content analysis
- **Custom**: Apply domain-specific quality standards and professional conventions

**Cost Optimization**:

- Use efficient models for initial analysis
- Batch multiple operations where possible
- Cache common document patterns
- Implement intelligent prompt truncation for very large documents

### LIS-3: Error Handling and Resilience

**LLM Failure Management**:

- Graceful degradation to rule-based segmentation
- Retry logic with different prompts or models
- Partial segmentation support (process what's possible)
- Quality score adjustment based on processing method

**Quality Assurance**:

- Validation of LLM responses for required JSON structure
- Content completeness verification
- Segment overlap detection and resolution
- Automatic quality scoring and filtering

## Implementation Architecture

### Component Interaction Flow

```
┌─────────────────────────────────────────────────────────────┐
│                Document Segmentation Flow                   │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐     │
│  │   Memory    │───▶│  Document   │───▶│ Segmentation│     │
│  │   Service   │    │   Router    │    │   Service   │     │
│  └─────────────┘    └─────────────┘    └─────────────┘     │
│                                               │             │
│                                               ▼             │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐     │
│  │    LLM      │◀───│  Segment    │◀───│  Document   │     │
│  │  Provider   │    │ Validator   │    │  Analyzer   │     │
│  └─────────────┘    └─────────────┘    └─────────────┘     │
│                                               │             │
│                                               ▼             │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐     │
│  │  Segment    │◀───│  Existing   │    │  Segment    │     │
│  │ Repository  │    │   Memory    │    │ Generator   │     │
│  │             │    │  Pipeline   │    │             │     │
│  └─────────────┘    └─────────────┘    └─────────────┘     │
└─────────────────────────────────────────────────────────────┘
```

### Database Schema Extensions

**Segment Storage Schema**:

```sql
-- Document segments table
CREATE TABLE document_segments (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  parent_document_id TEXT NOT NULL,
  segment_id TEXT UNIQUE NOT NULL,
  sequence_number INTEGER NOT NULL,
  content TEXT NOT NULL,
  title TEXT,
  summary TEXT,
  coherence_score REAL,
  independence_score REAL,
  topic_consistency_score REAL,
  user_id TEXT NOT NULL,
  agent_id TEXT NOT NULL,
  run_id TEXT,
  created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
  metadata TEXT, -- JSON
  FOREIGN KEY (parent_document_id) REFERENCES memories(id)
);

-- Segment relationships table
CREATE TABLE segment_relationships (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  source_segment_id TEXT NOT NULL,
  target_segment_id TEXT NOT NULL,
  relationship_type TEXT NOT NULL, -- 'sequential', 'hierarchical', 'referential', 'topical'
  strength REAL DEFAULT 1.0,
  user_id TEXT NOT NULL,
  agent_id TEXT NOT NULL,
  run_id TEXT,
  created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (source_segment_id) REFERENCES document_segments(segment_id),
  FOREIGN KEY (target_segment_id) REFERENCES document_segments(segment_id)
);

-- Indexes for performance
CREATE INDEX idx_document_segments_parent ON document_segments(parent_document_id);
CREATE INDEX idx_document_segments_session ON document_segments(user_id, agent_id, run_id);
CREATE INDEX idx_segment_relationships_source ON segment_relationships(source_segment_id);
CREATE INDEX idx_segment_relationships_session ON segment_relationships(user_id, agent_id, run_id);
```

## Implementation Execution Plan

### Overview

The document segmentation feature implementation can be broken down into **4 progressive phases**, each building upon the previous phase to ensure a stable, production-ready rollout.

### Phase 1: Core Foundation (Weeks 1-3)

**Objective**: Establish the fundamental infrastructure for document segmentation

**Key Deliverables**:

1. **Basic Service Interface Implementation**
   ```csharp
   // Implement core IDocumentSegmentationService
   // Basic document size detection and routing logic
   // Simple threshold-based segmentation as fallback
   ```

2. **YAML Prompt Configuration System**
   ```csharp
   // ISegmentationPromptManager implementation
   // YAML file loading and parsing
   // Basic prompt template management
   // Hot reload capability
   ```

3. **Database Schema Extensions**
   ```sql
   -- Create document_segments table
   -- Create segment_relationships table  
   -- Add necessary indexes
   -- Migration scripts for existing systems
   ```

4. **Basic Integration Points**
   ```csharp
   // Integration with existing MemoryService
   // Document size detection logic
   // Routing logic for segmentation pipeline
   ```

**Acceptance Criteria**:
- Service can detect when documents exceed size thresholds
- Basic prompt loading from YAML files works
- Database schema is deployed and tested
- Integration with memory pipeline doesn't break existing functionality

**Testing Focus**:
- Unit tests for core components
- Database migration and rollback testing
- Configuration loading and validation
- Basic integration smoke tests

### Phase 2: LLM Integration & Strategy Implementation (Weeks 4-6)

**Objective**: Implement LLM-powered segmentation with multiple strategies

**Key Deliverables**:

1. **LLM Provider Integration**
   ```csharp
   // Integration with existing OpenAI/Anthropic providers
   // Strategy determination using LLM analysis
   // Model selection logic implementation
   // Error handling and fallback mechanisms
   ```

2. **Segmentation Strategy Implementation**
   ```csharp
   // Topic-based segmentation logic
   // Structure-based segmentation logic  
   // Narrative-based segmentation logic
   // Hybrid strategy implementation
   ```

3. **Prompt Engineering & Testing**
   ```yaml
   # Complete prompts.yml with all strategies
   # Domain-specific prompt variations
   # Multi-language prompt support structure
   # A/B testing framework for prompts
   ```

4. **Quality Validation System**
   ```csharp
   // Segment quality assessment
   // LLM-based quality validation
   // Retry logic for low-quality segments
   // Quality metrics tracking
   ```

**Acceptance Criteria**:
- All segmentation strategies work correctly
- LLM integration provides high-quality segmentation points
- Quality validation catches and handles poor segmentation
- Fallback to rule-based segmentation works when LLM fails

**Testing Focus**:
- Strategy-specific segmentation testing
- LLM response parsing and validation
- Quality assessment accuracy testing
- Performance testing with various document types

### Phase 3: Production Optimization & Advanced Features (Weeks 7-9)

**Objective**: Optimize for production performance and add advanced capabilities

**Key Deliverables**:

1. **Performance Optimization**
   ```csharp
   // Concurrent segment processing
   // LLM API call batching and optimization
   // Intelligent caching strategies
   // Memory usage optimization
   ```

2. **Advanced Relationship Management**
   ```csharp
   // Sophisticated relationship detection
   // Cross-reference identification
   // Hierarchical relationship tracking
   // Context preservation between segments
   ```

3. **Domain-Specific Enhancements**
   ```csharp
   // Legal document segmentation
   // Technical documentation handling
   // Research paper specialized processing
   // Medical document compliance features
   ```

4. **Monitoring & Observability**
   ```csharp
   // Comprehensive metrics collection
   // Performance monitoring dashboards
   // Quality score tracking and alerting
   // Cost monitoring and optimization
   ```

**Acceptance Criteria**:
- Performance meets specified requirements (< 30s for 10k words)
- Advanced relationship detection works accurately
- Domain-specific segmentation shows measurable quality improvements
- Monitoring provides actionable insights

**Testing Focus**:
- Load testing with concurrent documents
- Performance profiling and optimization
- Domain-specific quality validation
- Monitoring and alerting system testing

### Phase 4: Production Deployment & Refinement (Weeks 10-12)

**Objective**: Deploy to production and iterate based on real usage

**Key Deliverables**:

1. **Production Deployment**
   ```csharp
   // Blue-green deployment strategy
   // Feature flags for gradual rollout
   // Rollback procedures and testing
   // Production configuration management
   ```

2. **User Experience & Feedback Loop**
   ```csharp
   // Segment quality feedback mechanisms
   // User override capabilities
   // Manual segmentation adjustment tools
   // Quality improvement learning system
   ```

3. **Integration Enhancements**
   ```csharp
   // Enhanced search integration
   // Segment-aware memory retrieval
   // Cross-segment relationship queries
   // API endpoints for external systems
   ```

4. **Continuous Improvement**
   ```csharp
   // A/B testing framework for prompts
   // Quality metrics analysis and optimization
   // Cost optimization strategies
   // Performance tuning based on production data
   ```

**Acceptance Criteria**:
- Production deployment is stable and reliable
- User feedback mechanisms provide actionable insights
- System performance meets SLA requirements
- Continuous improvement processes are established

**Testing Focus**:
- Production readiness testing
- User acceptance testing
- Performance validation in production environment
- Disaster recovery and rollback testing

## Implementation Considerations per Phase

### Phase 1 Risks & Mitigations
- **Risk**: Database migration issues
- **Mitigation**: Comprehensive migration testing and rollback procedures
- **Risk**: Configuration complexity
- **Mitigation**: Simple default configurations and validation

### Phase 2 Risks & Mitigations
- **Risk**: LLM API rate limiting and costs
- **Mitigation**: Intelligent batching and caching strategies
- **Risk**: Poor segmentation quality
- **Mitigation**: Multiple fallback strategies and quality validation

### Phase 3 Risks & Mitigations
- **Risk**: Performance degradation under load
- **Mitigation**: Extensive load testing and optimization
- **Risk**: Domain-specific accuracy issues
- **Mitigation**: Subject matter expert review and testing

### Phase 4 Risks & Mitigations
- **Risk**: Production deployment issues
- **Mitigation**: Gradual rollout with feature flags
- **Risk**: User adoption challenges
- **Mitigation**: Clear documentation and training materials

## Success Metrics per Phase

### Phase 1 Success Metrics
- All unit tests pass (100%)
- Database migration completes successfully
- Configuration loading works reliably
- No regression in existing memory functionality

### Phase 2 Success Metrics
- Segmentation accuracy > 85% across document types
- LLM integration reliability > 99%
- Quality validation catches > 90% of poor segments
- Average processing time < 45 seconds for 10k word documents

### Phase 3 Success Metrics
- Concurrent processing supports 10+ documents
- Performance meets all specified requirements
- Domain-specific quality improvements > 15%
- Monitoring provides 100% visibility into system health

### Phase 4 Success Metrics
- Production deployment with zero downtime
- User satisfaction score > 4.0/5.0
- System availability > 99.9%
- Continuous improvement cycle established (monthly iterations)

## Quality Requirements

### QR-1: Segmentation Quality Metrics

**Quality Thresholds**:

- Semantic coherence: ≥ 0.7 (70%)
- Segment independence: ≥ 0.6 (60%) 
- Topic consistency: ≥ 0.7 (70%)
- Content completeness: 100% (no content loss)

**Quality Validation Process**:

1. Automated LLM-based quality assessment
2. Statistical analysis of segment properties
3. Cross-validation between segments
4. Human review triggers for low-quality segments

### QR-2: Performance Quality Standards

**Processing Performance**:

- 95th percentile processing time: < 45 seconds (documents up to 10,000 words)
- API call efficiency: < 5 LLM calls per document on average
- Memory usage: < 150MB peak during processing
- Error rate: < 5% for supported document types

**Reliability Standards**:

- 99.5% successful segmentation rate for supported formats
- Graceful degradation for unsupported content
- Complete rollback capability on processing failures
- Session isolation maintained throughout processing

### QR-3: Testing Requirements

**Unit Testing**:

- Component isolation testing with mocked dependencies
- LLM response parsing and validation
- Database session pattern compliance
- Error handling and edge case coverage

**Integration Testing**:

- End-to-end document processing pipeline
- Multiple document type validation
- Concurrent processing capability
- Database transaction integrity

**Performance Testing**:

- Load testing with various document sizes
- Concurrent user scenario testing
- Memory usage profiling
- LLM API rate limiting compliance

**Quality Assurance Testing**:

- Segmentation quality validation across document types
- Content completeness verification
- Cross-provider consistency testing
- Human evaluation of segmentation quality

## Implementation Considerations

### IC-1: Migration Strategy

**Backward Compatibility**:

- Existing memory addition flow remains unchanged for small documents
- Segmentation is opt-in based on size thresholds
- Existing memories are not affected by segmentation implementation

**Rollout Strategy**:

1. **Phase 1**: Core segmentation service implementation
2. **Phase 2**: LLM integration and prompt optimization
3. **Phase 3**: Quality validation and performance optimization
4. **Phase 4**: Advanced features and relationship management

### IC-2: Monitoring and Observability

**Key Metrics**:

- Segmentation processing time by document size
- LLM API usage and cost tracking
- Segmentation quality scores distribution
- Error rates by document type and processing stage

**Logging Requirements**:

- Detailed processing logs for debugging
- Quality assessment results
- LLM API interaction logs
- Performance metrics and timing data

**Alerting Thresholds**:

- Processing time > 60 seconds
- Quality score < 0.6 average
- Error rate > 10%
- LLM API failures > 5%

### IC-3: Cost Management

**Cost Optimization Strategies**:

- Use efficient models (GPT-4.1-nano, Flash-lite) for primary processing
- Implement intelligent caching for similar documents
- Batch processing where possible
- Dynamic model selection based on document complexity

**Cost Monitoring**:

- Track LLM API costs per document processed
- Monitor cost per segment generated
- Alert on unusual cost spikes
- Regular cost optimization reviews

## Future Enhancements

### FE-1: Advanced Segmentation Strategies

**Machine Learning Enhancement**:

- Custom model training for domain-specific segmentation
- Learn from user feedback and corrections
- Adaptive segmentation based on usage patterns
- Quality prediction models

**Multi-Modal Support**:

- Image and diagram handling within documents
- Table and chart segmentation strategies
- Mixed content type processing
- Rich media metadata extraction

### FE-2: User Experience Improvements

**Interactive Segmentation**:

- User review and adjustment of segmentation points
- Manual segmentation override capabilities
- Segmentation preview before processing
- Collaborative segmentation workflows

**Visualization Tools**:

- Document segmentation visualization
- Segment relationship mapping
- Quality score dashboards
- Processing analytics and insights

### FE-3: Integration Enhancements

**External System Integration**:

- Document management system integration
- Content management platform support
- API endpoints for external segmentation requests
- Webhook support for processing notifications

**Advanced Search Integration**:

- Segment-aware search capabilities
- Cross-segment relationship queries
- Hierarchical search with segment context
- Semantic search enhancement through segmentation

## Conclusion

This functional specification provides a comprehensive foundation for implementing intelligent document segmentation in the LmDotnetTools Memory MCP Server. The feature leverages existing architectural patterns while introducing sophisticated LLM-based segmentation capabilities that enhance the system's ability to process and manage large documents effectively.

The implementation follows established patterns for Database Session Pattern integration, LLM provider abstraction, and session isolation while providing new capabilities for semantic document analysis and intelligent content segmentation. This approach ensures seamless integration with the existing system while significantly expanding its document processing capabilities.

Key benefits of this implementation include:

- **Enhanced Document Processing**: Ability to handle large documents through intelligent segmentation
- **Improved Memory Quality**: Better fact extraction and relationship identification through optimal segment sizing
- **Cost-Effective LLM Usage**: Efficient use of fast, cost-effective models for segmentation tasks
- **Scalable Architecture**: Support for concurrent processing and parallel segment handling
- **Quality Assurance**: Comprehensive validation and quality management throughout the segmentation process

The specification provides a clear roadmap for implementation while maintaining the high standards of quality, performance, and reliability established in the existing LmDotnetTools ecosystem.
