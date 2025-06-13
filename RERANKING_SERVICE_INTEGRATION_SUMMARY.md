# Reranking Service Integration with LmConfigService

## Overview
Successfully integrated reranking services with the LmConfigService to enable centralized model management and environment variable configuration for reranking operations, following the same patterns used for embedding services.

## Changes Made

### 1. Interface Update - ILmConfigService.cs
**Added:** `CreateRerankServiceAsync` method to the interface
- Method signature: `Task<IRerankService> CreateRerankServiceAsync(CancellationToken cancellationToken = default)`
- Updated documentation to include "reranking" in capability examples

### 2. Service Implementation - LmConfigService.cs
**Added:** Complete reranking service factory implementation
- `CreateRerankServiceAsync()` method that follows the same pattern as `CreateEmbeddingServiceAsync()`
- `CreateCohereRerankingService()` private method for Cohere-specific configuration
- `CohereRerankService` inner class that properly implements `IRerankService` interface

**Environment Variable Support:**
- `RERANKING_API_KEY` - Primary API key for reranking services
- `RERANKING_API_URL` - Base URL for reranking services  
- `RERANKING_MODEL` - Runtime model override
- Fallback support to `COHERE_API_KEY` and `COHERE_BASE_URL` for backward compatibility

**Default Configuration:**
- Added default reranking model "rerank-english-v3.0" to `CreateDefaultAppConfig()`
- Configured with Cohere provider and appropriate pricing
- Supports "reranking" capability

### 3. Implementation Details

#### CohereRerankService Class
- Extends `BaseRerankService` to inherit proper interface implementation
- Implements all required interface methods:
  - `RerankAsync(RerankRequest, CancellationToken)` - Primary reranking method
  - `RerankAsync(string, IReadOnlyList<string>, string, int?, CancellationToken)` - Convenience method
  - `GetAvailableModelsAsync(CancellationToken)` - Returns supported model list
- Uses Cohere v2/rerank API endpoint
- Proper JSON serialization/deserialization with `RerankRequest`/`RerankResponse` models
- Error handling and HTTP status code management

#### Configuration Pattern
Follows the established pattern from embedding services:
1. Check for service-specific environment variables (RERANKING_*)
2. Fallback to provider-specific variables (COHERE_*)
3. Final fallback to general configuration
4. Model override via environment variable
5. Comprehensive error messaging

## Environment Variables

### Complete Set for Reranking Services:
- **RERANKING_API_KEY** - API key for reranking services
- **RERANKING_API_URL** - Base URL for reranking services (optional, defaults to Cohere)
- **RERANKING_MODEL** - Model override (e.g., "rerank-v3.5")

### Backward Compatibility:
- **COHERE_API_KEY** - Fallback API key
- **COHERE_BASE_URL** - Fallback base URL

## Usage Example

```csharp
// Create reranking service via LmConfigService
var rerankService = await lmConfigService.CreateRerankServiceAsync();

// Use with RerankRequest
var request = new RerankRequest
{
    Query = "search query",
    Documents = documents.ToImmutableList(),
    Model = "rerank-english-v3.0",
    TopN = 5
};
var response = await rerankService.RerankAsync(request);

// Or use convenience method
var response2 = await rerankService.RerankAsync(
    "search query", 
    documents, 
    "rerank-english-v3.0", 
    topK: 5);
```

## Integration Points

### With Memory Server
- Reranking services can now be created through the same centralized configuration system
- Supports the LmConfig model selection and fallback strategies
- Integrates with dependency injection and logging infrastructure

### With Existing Embedding Services
- Follows identical patterns for consistency
- Same environment variable naming convention
- Compatible with existing LmConfig-based model management

## Benefits

1. **Centralized Configuration**: Reranking models managed through same system as LLM and embedding models
2. **Environment Variable Standardization**: Consistent RERANKING_* prefix pattern
3. **Runtime Model Override**: Easy model switching without configuration changes
4. **Provider Abstraction**: Support for future reranking providers beyond Cohere
5. **Proper Interface Implementation**: Full compliance with IRerankService contract
6. **Backward Compatibility**: Existing deployments continue to work
7. **Error Handling**: Comprehensive validation and meaningful error messages

## Testing Results

- **MemoryServer Tests**: 225/225 passing ✅
- **LmEmbeddings Tests**: 319/319 passing ✅
- **Build Status**: Clean build with 0 errors ✅

## Files Modified

1. `McpServers/Memory/MemoryServer/Services/ILmConfigService.cs`
2. `McpServers/Memory/MemoryServer/Services/LmConfigService.cs`

All changes maintain backward compatibility and follow established patterns. 