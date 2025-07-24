# Task List: Natural Tool Use Transformation Implementation

Based on the design document, here are the implementation tasks for this simple transformation feature.

## Overview

This is a straightforward transformation that converts `ToolsCallAggregateMessage` back to natural language format with XML-style tool calls. The implementation should be simple and focused.

**Estimated Effort**: ~1 day of development work

## Task 1: Core Transformation Logic

**Estimated Time**: 2-3 hours

### Implementation:
- [x] Create `ToolsCallAggregateTransformer` static class in `src/LmCore/Middleware/`
- [x] Implement `TransformToNaturalFormat()` method to convert `ToolsCallAggregateMessage` to XML format
- [x] Implement `CombineMessageSequence()` method to merge text messages with tool calls
- [x] Add basic metadata preservation (combine metadata from source messages)
- [x] Handle simple JSON pretty-printing for tool responses

### Requirements:
- [x] Requirement 3 (XML-Style Tool Format)
- [x] Requirement 6 (Preservation of Metadata)

### Tests:
- [x] Test single tool call transformation to XML format
- [x] Test multiple tool calls with separator (---)
- [x] Test message sequence combination (TextMessage + ToolsCallAggregateMessage + TextMessage)
- [x] Test metadata merging
- [x] Test JSON response formatting

## Task 2: Integration Point

**Estimated Time**: 1-2 hours

### Implementation:
- [x] Add extension method `ToNaturalToolUse()` to `MessageExtensions.cs`
- [x] Create simple helper methods for detecting transformable message sequences
- [x] Add basic error handling (graceful fallback if transformation fails)

### Requirements:
- [x] Requirement 1 (Reverse Transformation)
- [x] Requirement 2 (Message Chain Consolidation)
- [x] Requirement 5 (Integration Points)

### Tests:
- [x] Test extension method functionality
- [x] Test error handling and fallback behavior
- [x] Test integration with existing message types

## Task 3: Documentation and Validation

**Estimated Time**: 2-3 hours

### Implementation:
- [x] Update existing `NaturalToolUse.md` with reverse transformation section
- [x] Add simple usage examples showing before/after transformation
- [x] Create basic integration test demonstrating full pipeline
- [x] Add XML format specification to documentation

### Requirements:
- [x] All requirements (documentation and validation)

### Tests:
- [x] Integration test: Transform real ToolsCallAggregateMessage examples
- [x] Validate documentation examples work correctly
- [x] Test backward compatibility (existing code unaffected)

## Implementation Notes

### Keep It Simple:
- No complex configuration options initially
- No elaborate middleware infrastructure  
- No multiple strategy patterns
- Start with sensible defaults

### Core Functionality:
```csharp
// Essential transformation
var naturalFormat = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

// Message sequence combining
var combined = ToolsCallAggregateTransformer.CombineMessageSequence(messageSequence);
```

### XML Output Format:
```
<tool_call name="FunctionName">
{
  "arg1": "value1",
  "arg2": "value2"  
}
</tool_call>
<tool_response name="FunctionName">
Simple response or formatted JSON
</tool_response>
---
<tool_call name="AnotherFunction">
{"param": "value"}
</tool_call>
<tool_response name="AnotherFunction">
Another response
</tool_response>
```

## Dependencies

- **Task 1** must be completed first (core logic)
- **Task 2** depends on Task 1 (integration needs core logic)
- **Task 3** can be done in parallel with Task 2 (documentation)

## Success Criteria

1. **Functional**: Can transform ToolsCallAggregateMessage to readable XML format
2. **Simple**: Minimal code, easy to understand and maintain
3. **Integrated**: Works with existing message pipeline without breaking changes
4. **Tested**: Core functionality validated with unit and integration tests
5. **Documented**: Clear examples of usage and format specification

This simplified approach focuses on delivering the core functionality quickly while maintaining code quality and avoiding over-engineering.
