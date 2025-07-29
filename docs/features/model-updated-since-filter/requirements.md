# Feature Specification: Model Updated Since Filter

## High-Level Overview

The Model Updated Since Filter is a new command-line parameter for ModelConfigGenerator that allows users to filter models based on their creation/update date. This feature helps users focus on the latest models by excluding older ones, ensuring generated configurations contain only recent and relevant models.

## High Level Requirements

- **Primary Goal**: Enable filtering of models by date to focus on latest releases
- **User Experience**: Simple YYYY-MM-DD date input via command line parameter
- **Integration**: Seamless integration with existing filtering system using AND logic
- **Data Source**: Utilize OpenRouter API's `created` field (Unix timestamp)
- **Safety**: Skip models without date information rather than including them

## Existing Solutions

### Current Filtering Capabilities

ModelConfigGenerator already supports comprehensive filtering:

- **Family-based**: `--families llama,claude,gpt`
- **Capability-based**: `--reasoning-only`, `--multimodal-only`
- **Performance-based**: `--min-context`, `--max-cost`, `--max-models`

### Gap Addressed

No existing mechanism to filter models by recency/update date, which is crucial as the AI model landscape evolves rapidly with frequent new releases.

## Current Implementation

### Filtering Architecture

The current `ApplyFilters` method in `ModelConfigGeneratorService` applies filters sequentially:

1. Family filtering (regex-based)
2. Reasoning capability filtering
3. Multimodal capability filtering
4. Context length filtering
5. Cost filtering
6. Model count limiting

### Data Flow

```
OpenRouter API → OpenRouterModelService → ModelConfig objects → ModelConfigGeneratorService.ApplyFilters → Filtered results
```

### Missing Component

The `ModelConfig` objects do not currently include date information from OpenRouter's `created` field.

## Detailed Requirements

### Requirement 1: Command Line Parameter

**User Story**: As a user, I want to specify a minimum date for models so that I only get models created/updated since that date.

#### Acceptance Criteria:

1. **WHEN** user provides `--model-updated-since 2024-01-01` **THEN** only models created on or after January 1, 2024 **SHALL** be included
2. **WHEN** user provides invalid date format **THEN** application **SHALL** display error message and exit
3. **WHEN** user provides `--model-updated-since` without value **THEN** application **SHALL** display error message and exit
4. **WHEN** user combines with other filters **THEN** date filter **SHALL** work with AND logic alongside existing filters

### Requirement 2: Date Data Integration

**User Story**: As a developer, I want the system to parse and utilize OpenRouter's date information for filtering.

#### Acceptance Criteria:

1. **WHEN** fetching model data **THEN** system **SHALL** parse the `created` field from OpenRouter API response
2. **WHEN** `created` field is missing for a model **THEN** system **SHALL** exclude that model from results
3. **WHEN** `created` field is invalid **THEN** system **SHALL** log warning and exclude that model
4. **WHEN** comparing dates **THEN** system **SHALL** use date component only (ignore time) for day-level filtering

### Requirement 3: Date Processing Logic

**User Story**: As a user, I want intuitive date handling that works with my local timezone expectations.

#### Acceptance Criteria:

1. **WHEN** user inputs YYYY-MM-DD format **THEN** system **SHALL** parse as local date
2. **WHEN** comparing with OpenRouter timestamps **THEN** system **SHALL** convert to UTC for consistent comparison
3. **WHEN** date parsing fails **THEN** system **SHALL** provide clear error message with expected format
4. **WHEN** filtering models **THEN** system **SHALL** include models where `created_date >= user_specified_date`

### Requirement 4: Integration with Existing Filters

**User Story**: As a user, I want to combine date filtering with other filters for precise model selection.

#### Acceptance Criteria:

1. **WHEN** using `--families llama --model-updated-since 2024-01-01` **THEN** system **SHALL** return only Llama models created since 2024-01-01
2. **WHEN** using multiple filters **THEN** all conditions **SHALL** be met (AND logic)
3. **WHEN** date filter eliminates all models **THEN** system **SHALL** log appropriate message and generate empty config
4. **WHEN** verbose logging enabled **THEN** system **SHALL** log how many models were filtered out by date

### Requirement 5: Error Handling and User Experience

**User Story**: As a user, I want clear feedback when date filtering encounters issues.

#### Acceptance Criteria:

1. **WHEN** no models match date criteria **THEN** system **SHALL** log informative message about date range
2. **WHEN** many models lack date information **THEN** system **SHALL** log count of excluded models
3. **WHEN** user provides future date **THEN** system **SHALL** accept it (for testing/edge cases)
4. **WHEN** help is requested **THEN** system **SHALL** document the date parameter with examples

## Implementation Details

### Code Changes Required

#### 1. GeneratorOptions.cs
```csharp
/// <summary>
/// Filter models to only include those updated since this date.
/// Models without date information will be excluded.
/// </summary>
public DateTime? ModelUpdatedSince { get; init; }
```

#### 2. Program.cs - Command Line Parsing
```csharp
case "--model-updated-since":
    if (i + 1 >= args.Length || !DateTime.TryParse(args[i + 1], out var sinceDate))
    {
        Console.Error.WriteLine("Error: --model-updated-since requires a valid date (YYYY-MM-DD)");
        return null;
    }
    options = options with { ModelUpdatedSince = sinceDate };
    i++;
    break;
```

#### 3. ModelConfigGeneratorService.cs - Filter Logic
```csharp
// Filter by model update date
if (options.ModelUpdatedSince.HasValue)
{
    filtered = filtered.Where(model => 
        model.CreatedDate.HasValue && 
        model.CreatedDate.Value.Date >= options.ModelUpdatedSince.Value.Date);
    _logger.LogDebug("Filtered by models updated since {Date}: {Count} models remaining", 
        options.ModelUpdatedSince.Value.ToShortDateString(), filtered.Count());
}
```

#### 4. OpenRouterModelService.cs - Data Parsing
```csharp
// In CreateModelConfigFromGroupAsync method
var createdTimestamp = GetLongValue(primaryModelNode, "created");
var createdDate = createdTimestamp.HasValue 
    ? DateTimeOffset.FromUnixTimeSeconds(createdTimestamp.Value).DateTime
    : (DateTime?)null;
```

#### 5. ModelConfig.cs - Data Structure
```csharp
/// <summary>
/// The date when this model was created/added to OpenRouter.
/// Null if date information is not available.
/// </summary>
public DateTime? CreatedDate { get; init; }
```

### Testing Strategy

#### Unit Tests
- Date parsing validation
- Filter logic with various date combinations
- Edge cases (missing dates, invalid formats, future dates)
- Integration with existing filters

#### Integration Tests
- End-to-end CLI parameter parsing
- OpenRouter API data parsing
- Generated config validation

### Documentation Updates

#### README.md
- Add parameter to options table
- Add examples demonstrating date filtering
- Update usage examples

#### Help Text
```
--model-updated-since <date>  Include only models updated since this date (YYYY-MM-DD format)
```

## Risk Considerations

### Data Availability
- **Risk**: OpenRouter may not provide `created` field for all models
- **Mitigation**: Skip models without date info (user preference)

### Performance Impact
- **Risk**: Additional filtering step may slow down processing
- **Mitigation**: Date comparison is lightweight, minimal impact expected

### User Confusion
- **Risk**: Users may not understand OpenRouter vs. original model dates
- **Mitigation**: Clear documentation explaining data source

## Success Criteria

1. **Functional**: Users can successfully filter models by date using `--model-updated-since YYYY-MM-DD`
2. **Integration**: Date filtering works seamlessly with all existing filters
3. **Performance**: No significant performance degradation in model processing
4. **Usability**: Clear error messages and documentation for date parameter
5. **Reliability**: Graceful handling of missing or invalid date data

## Example Usage

```bash
# Get only recent models
dotnet run -- --model-updated-since 2024-01-01

# Combine with family filtering
dotnet run -- --families llama,claude --model-updated-since 2024-06-01

# Recent reasoning models only
dotnet run -- --model-updated-since 2024-01-01 --reasoning-only --max-models 10

# Verbose output to see filtering details
dotnet run -- --model-updated-since 2024-01-01 --verbose
```
