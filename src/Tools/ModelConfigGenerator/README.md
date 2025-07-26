# ModelConfigGenerator

A powerful utility for generating Models.config files from OpenRouter API data with advanced filtering capabilities.

## Overview

ModelConfigGenerator automatically fetches model information from OpenRouter's API and converts it into the `Models.config` format used by LmDotnetTools. It supports extensive filtering by model families, capabilities, performance characteristics, and costs.

## Features

- **Family-based Filtering**: Filter models by families like Llama, Claude, GPT, Qwen, DeepSeek, Kimi, etc.
- **Capability Filtering**: Include only reasoning models, multimodal models, or models with specific features
- **Performance Filtering**: Filter by context length, cost per token, and other performance metrics
- **Date-based Filtering**: Include only models updated since a specific date for the latest releases
- **Comprehensive Output**: Generates complete Models.config with capabilities, pricing, and provider information
- **Flexible Options**: Support for compact JSON, capability exclusion, and output customization
- **Built-in Caching**: Leverages OpenRouter service caching for efficient operation

## Usage

### Basic Usage

Generate a complete Models.config with all available models:

```bash
dotnet run --project ModelConfigGenerator
```

### Family-based Filtering

Generate config with specific model families:

```bash
# Single family
dotnet run --project ModelConfigGenerator -- --families llama

# Multiple families
dotnet run --project ModelConfigGenerator -- --families llama,claude,gpt --output ./my-models.json

# DeepSeek and Qwen models only
dotnet run --project ModelConfigGenerator -- --families deepseek,qwen --verbose
```

### Capability-based Filtering

Filter by model capabilities:

```bash
# Only reasoning models
dotnet run --project ModelConfigGenerator -- --reasoning-only --output reasoning-models.json

# Only multimodal models  
dotnet run --project ModelConfigGenerator -- --multimodal-only

# Combine with family filtering
dotnet run --project ModelConfigGenerator -- --families claude,gpt --multimodal-only
```

### Performance-based Filtering

Filter by performance characteristics:

```bash
# Models with at least 100K context length
dotnet run --project ModelConfigGenerator -- --min-context 100000

# Cost-effective models (max $5 per million tokens)
dotnet run --project ModelConfigGenerator -- --max-cost 5.0

# Top 10 models only
dotnet run --project ModelConfigGenerator -- --max-models 10

# High-performance, cost-effective reasoning models
dotnet run --project ModelConfigGenerator -- --reasoning-only --min-context 50000 --max-cost 10.0 --max-models 5
```

### Date-based Filtering

Filter by model release/update date:

```bash
# Models released since January 1, 2024
dotnet run --project ModelConfigGenerator -- --model-updated-since 2024-01-01

# Recent reasoning models only
dotnet run --project ModelConfigGenerator -- --model-updated-since 2024-06-01 --reasoning-only

# Latest Claude and GPT models
dotnet run --project ModelConfigGenerator -- --families claude,gpt --model-updated-since 2024-01-01 --output latest-models.json

# Combine with performance filtering for cutting-edge models
dotnet run --project ModelConfigGenerator -- --model-updated-since 2024-01-01 --min-context 100000 --max-models 5 --verbose
```

### Output Customization

Customize the output format:

```bash
# Compact JSON without indentation
dotnet run --project ModelConfigGenerator -- --compact --output compact-models.json

# Exclude detailed capabilities (smaller file)
dotnet run --project ModelConfigGenerator -- --no-capabilities

# Verbose logging for debugging
dotnet run --project ModelConfigGenerator -- --verbose --families llama
```

## Command Line Options

| Option | Alias | Description | Example |
|--------|-------|-------------|---------|
| `--output` | `-o` | Output file path | `--output ./config/models.json` |
| `--families` | `-f` | Comma-separated model families | `--families llama,claude,gpt` |
| `--verbose` | `-v` | Enable verbose logging | `--verbose` |
| `--max-models` | `-m` | Maximum number of models | `--max-models 20` |
| `--reasoning-only` | `-r` | Include only reasoning models | `--reasoning-only` |
| `--multimodal-only` | | Include only multimodal models | `--multimodal-only` |
| `--min-context` | | Minimum context length | `--min-context 100000` |
| `--max-cost` | | Maximum cost per million tokens | `--max-cost 5.0` |
| `--model-updated-since` | | Include only models updated since date | `--model-updated-since 2024-01-01` |
| `--no-capabilities` | | Exclude capabilities information | `--no-capabilities` |
| `--compact` | | Generate compact JSON | `--compact` |
| `--list-families` | | List supported families | `--list-families` |
| `--help` | `-h` | Show help | `--help` |

## Supported Model Families

The utility automatically detects and categorizes models into the following families:

- **llama** - Meta's Llama models (meta-llama)
- **claude** - Anthropic's Claude models  
- **gpt** - OpenAI's GPT models
- **qwen** - Alibaba's Qwen models
- **deepseek** - DeepSeek models
- **kimi** - Moonshot's Kimi models  
- **gemini** - Google's Gemini models
- **mistral** - Mistral AI models
- **cohere** - Cohere Command models
- **yi** - 01.AI Yi models
- **phi** - Microsoft Phi models
- **falcon** - Technology Innovation Institute Falcon models
- **wizardlm** - WizardLM models
- **vicuna** - Vicuna models
- **alpaca** - Alpaca models
- **nous** - Nous Research models (Hermes)

Use `--list-families` to see the current list of supported families.

## Examples

### Generate Config for Specific Use Cases

**High-quality reasoning models:**
```bash
dotnet run -- --reasoning-only --min-context 50000 --max-models 5 --output reasoning-models.json --verbose
```

**Cost-effective chat models:**
```bash
dotnet run -- --families llama,qwen,yi --max-cost 2.0 --min-context 32000 --output budget-models.json
```

**Multimodal models for vision tasks:**
```bash
dotnet run -- --multimodal-only --families gpt,claude,gemini --output vision-models.json
```

**Chinese language models:**
```bash
dotnet run -- --families qwen,yi,kimi --output chinese-models.json
```

**Enterprise-grade models:**
```bash
dotnet run -- --families gpt,claude --min-context 100000 --reasoning-only --output enterprise-models.json
```

**Latest cutting-edge models:**
```bash
dotnet run -- --model-updated-since 2024-01-01 --reasoning-only --max-models 10 --output latest-reasoning.json
```

**Recent multimodal models:**
```bash
dotnet run -- --model-updated-since 2024-06-01 --multimodal-only --families gpt,claude,gemini --output recent-vision.json
```

## Output Format

The generated Models.config file follows the standard LmDotnetTools format:

```json
{
  "models": [
    {
      "id": "meta-llama/llama-3.1-70b",
      "is_reasoning": false,
      "capabilities": {
        "token_limits": {
          "max_context_tokens": 131072,
          "max_output_tokens": 4096
        },
        "supports_streaming": true,
        "supported_features": ["long-context"]
      },
      "providers": [
        {
          "name": "OpenRouter",
          "model_name": "meta-llama/llama-3.1-70b", 
          "priority": 1,
          "pricing": {
            "prompt_per_million": 0.5,
            "completion_per_million": 0.75
          }
        }
      ]
    }
  ],
  "provider_registry": {
    "OpenRouter": {
      "endpoint_url": "https://openrouter.ai/api/v1",
      "api_key_environment_variable": "OPENROUTER_API_KEY",
      "compatibility": "OpenAI"
    }
  }
}
```

## Error Handling

The utility includes comprehensive error handling:

- **Network Issues**: Automatic retries with exponential backoff
- **API Errors**: Graceful degradation with informative error messages  
- **Invalid Filters**: Validation of command line arguments
- **File I/O**: Automatic directory creation and permission handling

## Performance

- **Caching**: Leverages OpenRouter service's 24-hour cache with background refresh
- **Concurrent Requests**: Optimized API calls with rate limiting
- **Memory Efficient**: Streaming JSON processing for large datasets
- **Fast Filtering**: Compiled regex patterns for family matching

## Development

### Building

```bash
dotnet build src/Tools/ModelConfigGenerator/ModelConfigGenerator.csproj
```

### Testing

```bash
dotnet test tests/ModelConfigGenerator.Tests/ModelConfigGenerator.Tests.csproj
```

### Dependencies

- **LmConfig**: Core model configuration types and OpenRouter service
- **Microsoft.Extensions.Hosting**: Dependency injection and logging
- **Microsoft.Extensions.Http**: HTTP client factory

## Contributing

1. Add new model family patterns in `ModelFamilyPatterns` dictionary
2. Extend filtering logic in `ApplyFilters` method  
3. Add corresponding tests in the test project
4. Update documentation with new options

## License

This utility is part of the LmDotnetTools project and follows the same licensing terms.
