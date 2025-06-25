# Entity Extraction Model Selection

This document explains how to specify which model to use for entity extraction while keeping all other settings the same.

## Overview

The GraphExtractionService now supports direct model specification through an optional `modelId` parameter. This allows you to choose specific models from your `models.json` configuration for entity extraction tasks.

### Default Model Change

**Important:** The default model has been changed from capability-based selection to **gpt-4.1-nano** for all entity extraction operations. This ensures:

- **Consistent Performance**: 1-second typical latency, 200 tokens/second
- **Cost Efficiency**: $0.1/$0.4 per million tokens (ultra-cost-effective)
- **Reliability**: Same model used across all extraction operations
- **Quality**: Maintains high accuracy with JSON schema support

## Usage Examples

### 1. Using Default Model (gpt-4.1-nano)

```csharp
// Uses gpt-4.1-nano as the default model for fast, cost-effective extraction
var entities = await graphExtractionService.ExtractEntitiesAsync(
    content: "John works at Microsoft and likes pizza",
    sessionContext: sessionContext,
    memoryId: 123);
```

### 2. Using Specific Model ID (New Feature)

```csharp
// Uses GPT-4.1 Nano specifically for fast, cost-effective extraction
var entities = await graphExtractionService.ExtractEntitiesAsync(
    content: "John works at Microsoft and likes pizza", 
    sessionContext: sessionContext,
    memoryId: 123,
    modelId: "gpt-4.1-nano");

// Uses Gemini 2.5 Flash for thinking-capable extraction
var entities = await graphExtractionService.ExtractEntitiesAsync(
    content: "John works at Microsoft and likes pizza",
    sessionContext: sessionContext, 
    memoryId: 123,
    modelId: "gemini-2.5-flash");
```

### 3. Combined Graph Data Extraction

```csharp
// Extract both entities and relationships with a specific model
var (entities, relationships) = await graphExtractionService.ExtractGraphDataAsync(
    content: "John works at Microsoft and likes pizza",
    sessionContext: sessionContext,
    memoryId: 123,
    modelId: "qwen3-32b"); // Use Qwen for multilingual support
```

### 4. Entity Validation with Specific Model

```csharp
// Validate extracted entities using a premium model for higher accuracy
var cleanedEntities = await graphExtractionService.ValidateAndCleanEntitiesAsync(
    entities: extractedEntities,
    sessionContext: sessionContext,
    modelId: "gpt-4.1"); // Use premium model for validation
```

## Available Models

Based on your `models.json` configuration, you can use any of these model IDs:

### Cost-Optimized Models
- `"gpt-4.1-nano"` - Ultra-fast, cost-optimized ($0.1/$0.4 per million tokens)
- `"gemini-2.0-flash-lite"` - Ultra-fast Google model ($0.075/$0.3 per million tokens)
- `"qwen3-8b"` - Cost-effective multilingual model ($0.4/$1.2 per million tokens)

### Balanced Models  
- `"gpt-4.1-mini"` - Balanced cost/performance ($0.4/$1.6 per million tokens)
- `"llama-3.3-70b"` - High-quality open-source model ($0.59/$0.79 per million tokens)
- `"qwen3-32b"` - Balanced multilingual model ($1.2/$3.6 per million tokens)

### Premium Models
- `"gpt-4.1"` - Premium OpenAI model ($2.0/$8.0 per million tokens)
- `"claude-3-sonnet"` - High-quality reasoning model ($3.0/$15.0 per million tokens)
- `"gemini-2.5-pro"` - Google's flagship with thinking ($1.25/$10.0 per million tokens)

### Reasoning Models (with built-in thinking)
- `"deepseek-r1"` - Cost-effective reasoning model ($0.55/$2.19 per million tokens)
- `"gemini-2.5-flash"` - Fast reasoning model ($0.15/$0.6 per million tokens)
- `"qwen3-235b-a22b"` - Large reasoning model ($8.0/$24.0 per million tokens)

## Features Maintained

When using direct model specification, all existing features are preserved:

- ✅ **JSON Schema Support** - Automatically applied based on model capabilities
- ✅ **Error Handling** - Graceful fallback if model is unavailable
- ✅ **Logging** - Complete audit trail of which model was used
- ✅ **Metadata Tracking** - Model used is recorded in entity metadata
- ✅ **Backward Compatibility** - Existing code continues to work unchanged

## Model Selection Logic

1. **When `modelId` is provided:**
   - Validates the model exists in configuration
   - Uses the specified model directly
   - Applies appropriate JSON schema based on model capabilities
   - Falls back to basic options if model validation fails

2. **When `modelId` is null/empty:**
   - Uses existing capability-based selection
   - Selects optimal model for "small-model;json_schema" capability
   - Applies cost optimization if configured

## Error Handling

```csharp
try 
{
    var entities = await graphExtractionService.ExtractEntitiesAsync(
        content, sessionContext, memoryId, modelId: "invalid-model");
}
catch (InvalidOperationException ex)
{
    // Handle case where specified model is not found
    // Automatic fallback to capability-based selection
}
```

## Configuration Integration

The feature integrates seamlessly with existing configuration:

```json
{
  "MemoryServer": {
    "LmConfig": {
      "FallbackStrategy": "cost-optimized",
      "CostOptimization": {
        "Enabled": true,
        "MaxCostPerRequest": 0.01
      }
    }
  }
}
```

Cost optimization and fallback strategies are still applied when using automatic selection, but bypassed when using direct model specification. 