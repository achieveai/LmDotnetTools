# Document Segmentation Integration Test Data

This directory contains cached HTTP responses for integration tests that use real LLM services via MockHttpHandlerBuilder.

## Structure

- `TopicBasedSegmentation/` - Test data for topic-based segmentation integration tests
- `RealLlmService.json` - Cached responses for the main integration test

## How it works

1. **First run**: Tests with `allowAdditional: true` record real API responses to JSON files
2. **Subsequent runs**: Tests use cached responses for fast execution without API calls
3. **CI/CD**: Uses cached responses, no API keys needed

## Test Data Files

Each JSON file contains recorded HTTP interactions with structure:
```json
{
  "Interactions": [
    {
      "SerializedRequest": { /* OpenAI/Anthropic request */ },
      "SerializedResponse": { /* API response */ },
      "IsStreaming": false,
      "Provider": "OpenAI"
    }
  ]
}
```
