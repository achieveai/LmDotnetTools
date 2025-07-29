# Design Document: Model Updated Since Filter

## Overview

This document outlines the design approach for implementing the Model Updated Since Filter feature in ModelConfigGenerator. The design focuses on surgical changes to the existing codebase while maintaining compatibility and following established patterns.

## Design Philosophy

### Surgical Changes Approach

After analyzing the current implementation, we can implement this feature with **surgical changes** rather than a complete rewrite:

**Pros:**
- ✅ Minimal risk - leverages proven existing architecture
- ✅ Fast implementation - only 5 files need modification
- ✅ Backward compatibility - no breaking changes to existing functionality
- ✅ Consistent patterns - follows established filtering and CLI patterns
- ✅ Testable - isolated changes that can be unit tested independently

**Cons:**
- ⚠️ Adds one more property to ModelConfig (acceptable growth)
- ⚠️ Requires parsing additional field from OpenRouter API (minimal overhead)

### Alternative Considered: Wrapper-Based Approach

We considered creating a wrapper around ModelConfig to add date information, but rejected it because:
- More complex architecture
- Additional abstraction layer
- Inconsistent with existing patterns
- Higher implementation cost

## Architectural Design

### High-Level Architecture

```
User Input (CLI) → GeneratorOptions → ModelConfigGeneratorService.ApplyFilters → Filtered Results
                                           ↑
OpenRouter API → OpenRouterModelService → ModelConfig (with CreatedDate)
```

### Component Integration

1. **Data Layer**: `OpenRouterModelService` parses `created` field from API
2. **Model Layer**: `ModelConfig` includes `CreatedDate` property  
3. **Service Layer**: `ModelConfigGeneratorService` applies date filtering
4. **CLI Layer**: `Program.cs` parses `--model-updated-since` parameter
5. **Configuration**: `GeneratorOptions` holds date filter setting

## Detailed Design

### 1. Data Structure Changes

#### ModelConfig Enhancement
```csharp
public record ModelConfig
{
    // ... existing properties ...
    
    /// <summary>
    /// The date when this model was created/added to OpenRouter.
    /// Null if date information is not available.
    /// </summary>
    public DateTime? CreatedDate { get; init; }
}
```

**Design Rationale:**
- `DateTime?` allows for missing date information
- `init` accessor maintains immutability
- Property name clearly indicates data source and meaning

### 2. Configuration Layer

#### GeneratorOptions Enhancement
```csharp
public record GeneratorOptions
{
    // ... existing properties ...
    
    /// <summary>
    /// Filter models to only include those updated since this date.
    /// Models without date information will be excluded.
    /// </summary>
    public DateTime? ModelUpdatedSince { get; init; }
}
```

**Design Rationale:**
- Follows existing property naming pattern
- `DateTime?` allows optional filtering
- Clear documentation about exclusion behavior

### 3. Data Parsing Layer

#### OpenRouterModelService Changes

**Location**: `CreateModelConfigFromGroupAsync` method

```csharp
// Add after existing field parsing
var createdTimestamp = GetLongValue(primaryModelNode, "created");
var createdDate = createdTimestamp.HasValue 
    ? DateTimeOffset.FromUnixTimeSeconds(createdTimestamp.Value).DateTime
    : (DateTime?)null;

// Include in ModelConfig creation
return new ModelConfig
{
    Id = modelSlug,
    IsReasoning = isReasoning,
    Capabilities = capabilities,
    Providers = providers,
    CreatedDate = createdDate // New property
};
```

**Design Rationale:**
- Reuses existing `GetLongValue` helper method
- Uses .NET's built-in Unix timestamp conversion
- Graceful handling of missing data
- Minimal changes to existing method

#### Helper Method Addition

```csharp
/// <summary>
/// Safely extracts a long value from a JsonNode.
/// </summary>
private static long? GetLongValue(JsonNode? node, string propertyName)
{
    try
    {
        return node?[propertyName]?.GetValue<long>();
    }
    catch (FormatException)
    {
        return null;
    }
}
```

**Design Rationale:**
- Follows existing helper method pattern (`GetStringValue`, `GetIntValue`, etc.)
- Safe parsing with graceful error handling
- Reusable for future long field parsing needs

### 4. Filtering Layer

#### ModelConfigGeneratorService Enhancement

**Location**: `ApplyFilters` method, after existing filters

```csharp
// Filter by model update date
if (options.ModelUpdatedSince.HasValue)
{
    var beforeCount = filtered.Count();
    filtered = filtered.Where(model => 
        model.CreatedDate.HasValue && 
        model.CreatedDate.Value.Date >= options.ModelUpdatedSince.Value.Date);
    
    var afterCount = filtered.Count();
    var excludedCount = beforeCount - afterCount;
    
    _logger.LogDebug("Filtered by models updated since {Date}: {Count} models remaining, {Excluded} excluded", 
        options.ModelUpdatedSince.Value.ToShortDateString(), afterCount, excludedCount);
}
```

**Design Rationale:**
- Inserted after existing filters for logical flow
- Date-only comparison ignores time components
- Detailed logging for debugging and user feedback
- Explicitly excludes models without date information

### 5. Command Line Interface

#### Program.cs Enhancement

**Location**: `ParseArguments` method switch statement

```csharp
case "--model-updated-since":
    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine("Error: --model-updated-since requires a value");
        return null;
    }
    if (!DateTime.TryParse(args[i + 1], out var sinceDate))
    {
        Console.Error.WriteLine("Error: --model-updated-since requires a valid date (YYYY-MM-DD)");
        return null;
    }
    options = options with { ModelUpdatedSince = sinceDate };
    i++;
    break;
```

**Location**: `ShowHelp` method

```csharp
Console.WriteLine("  --model-updated-since <date>  Include only models updated since this date (YYYY-MM-DD)");
```

**Design Rationale:**
- Follows existing parameter parsing pattern
- Two-stage validation (value exists, then valid date)
- Clear error messages matching existing style
- Help text follows established format

## Data Flow Design

### 1. Startup Flow
```
User Command → Parse Arguments → Create GeneratorOptions → Pass to Service
```

### 2. Data Fetching Flow
```
OpenRouter API → JSON Response → Parse 'created' field → Convert to DateTime → Store in ModelConfig
```

### 3. Filtering Flow
```
All Models → Family Filter → Capability Filters → Performance Filters → Date Filter → Final Results
```

### 4. Error Handling Flow
```
Missing Date → Log Warning → Exclude Model
Invalid Date Input → Error Message → Exit
No Models After Filter → Log Info → Continue
```

## Error Handling Strategy

### User Input Errors
- **Invalid date format**: Clear error message with expected format
- **Missing parameter value**: Specific error about missing value
- **Future dates**: Accept (for testing scenarios)

### Data Processing Errors
- **Missing `created` field**: Log at debug level, exclude model
- **Invalid timestamp**: Log warning, exclude model
- **Zero models after filtering**: Log informative message, continue

### Logging Strategy
- **Debug Level**: Detailed filtering statistics
- **Info Level**: Summary of filtering results
- **Warning Level**: Data quality issues
- **Error Level**: Configuration or system errors

## Performance Considerations

### Memory Impact
- **Minimal**: One additional `DateTime?` property per model
- **Estimated**: ~8 bytes per model, negligible for typical use

### CPU Impact
- **Date Parsing**: One-time cost during model loading
- **Filtering**: Lightweight DateTime comparison
- **Estimated**: <1ms additional processing time

### I/O Impact
- **No additional API calls**: Uses existing OpenRouter data
- **Same caching behavior**: Leverages existing caching system

## Testing Strategy

### Unit Tests Locations

#### ModelConfigGeneratorService.Tests
```csharp
[Test]
public void ApplyFilters_WithModelUpdatedSince_FiltersCorrectly()
{
    // Test date filtering logic
}

[Test]
public void ApplyFilters_WithModelUpdatedSinceAndOtherFilters_AppliesAllFilters()
{
    // Test AND logic with existing filters
}

[Test]
public void ApplyFilters_WithMissingCreatedDates_ExcludesModels()
{
    // Test exclusion of models without dates
}
```

#### Program.Tests
```csharp
[Test]
public void ParseArguments_WithValidDate_ParsesCorrectly()
{
    // Test command line parsing
}

[Test]
public void ParseArguments_WithInvalidDate_ReturnsNull()
{
    // Test error handling
}
```

#### OpenRouterModelService.Tests
```csharp
[Test]
public void CreateModelConfig_WithCreatedField_ParsesCorrectly()
{
    // Test Unix timestamp parsing
}

[Test]
public void CreateModelConfig_WithoutCreatedField_HandlesGracefully()
{
    // Test missing data handling
}
```

### Integration Tests
- End-to-end CLI parameter testing
- Full filtering pipeline validation
- Generated configuration verification

## Risk Mitigation

### Data Quality Risks
- **Missing dates**: Explicit exclusion with logging
- **Invalid timestamps**: Safe parsing with error handling
- **Future API changes**: Defensive programming with null checks

### Backward Compatibility
- **Existing CLI**: No changes to existing parameters
- **Existing configs**: Generated configs remain compatible
- **Existing filters**: All existing functionality preserved

### Performance Risks
- **Large model sets**: Efficient LINQ filtering
- **Memory usage**: Minimal additional memory footprint
- **Processing time**: Lightweight date operations

## Documentation Requirements

### User Documentation
- Update README.md with new parameter
- Add examples to usage section
- Update command line options table

### Developer Documentation
- Update API documentation for ModelConfig
- Document new property in code comments
- Update architecture diagrams if needed

### Help System
- Add parameter to --help output
- Include format specification and examples

## Implementation Phases

### Phase 1: Core Implementation
1. Add `CreatedDate` property to `ModelConfig`
2. Update `OpenRouterModelService` to parse date
3. Add date filtering to `ModelConfigGeneratorService`

### Phase 2: CLI Integration
1. Add parameter parsing to `Program.cs`
2. Update help text and validation
3. Test command line interface

### Phase 3: Testing & Documentation
1. Implement unit tests
2. Add integration tests  
3. Update documentation

### Phase 4: Validation & Polish
1. End-to-end testing
2. Performance validation
3. Documentation review

## Success Metrics

### Functional Metrics
- ✅ CLI parameter accepted and parsed correctly
- ✅ Date filtering works with all existing filters
- ✅ Models without dates excluded appropriately
- ✅ Generated configs contain only filtered models

### Quality Metrics
- ✅ No regression in existing functionality
- ✅ All unit tests pass
- ✅ Integration tests pass
- ✅ Performance impact <5%

### Usability Metrics
- ✅ Clear error messages for invalid input
- ✅ Helpful logging for debugging
- ✅ Documentation covers common use cases

This design provides a clear, maintainable, and efficient implementation path while preserving the existing architecture and ensuring reliable operation.
