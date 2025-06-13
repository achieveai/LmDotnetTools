# LLM Integration Configuration Guide

This guide explains how to configure LLM providers for intelligent graph processing in the Memory MCP Server.

## Overview

The Memory MCP Server uses Large Language Models (LLMs) to automatically extract entities and relationships from conversation content, building a knowledge graph that enhances memory search and organization.

## Supported Providers

- **OpenAI**: GPT-4, GPT-3.5-turbo
- **Anthropic**: Claude-3 Sonnet, Claude-3 Haiku

## Configuration Steps

### 1. Set Environment Variables

Create environment variables for your API keys:

```bash
# Windows (PowerShell)
$env:OPENAI_API_KEY = "sk-proj-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
$env:ANTHROPIC_API_KEY = "sk-ant-api03-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"

# Linux/macOS (Bash)
export OPENAI_API_KEY="sk-proj-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
export ANTHROPIC_API_KEY="sk-ant-api03-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
```

### 2. Configure Provider Settings

Edit `appsettings.json` to customize LLM behavior:

```json
{
  "MemoryServer": {
    "LLM": {
      "DefaultProvider": "openai",
      "EnableGraphProcessing": true,
      "OpenAI": {
        "Model": "gpt-4",
        "Temperature": 0.0,
        "MaxTokens": 1000,
        "Timeout": 30,
        "MaxRetries": 3
      },
      "Anthropic": {
        "Model": "claude-3-sonnet-20240229",
        "Temperature": 0.0,
        "MaxTokens": 1000,
        "Timeout": 30,
        "MaxRetries": 3
      }
    }
  }
}
```

### 3. Enable Logging

To see LLM integration in action, set logging level to Information:

```json
{
  "Logging": {
    "LogLevel": {
      "MemoryServer": "Information"
    }
  }
}
```

## Features

When properly configured, the LLM integration provides:

### Entity Extraction
- **People**: Names, roles, titles
- **Places**: Locations, addresses, venues  
- **Organizations**: Companies, institutions, groups
- **Concepts**: Topics, subjects, ideas
- **Objects**: Products, tools, items
- **Events**: Meetings, activities, occasions

### Relationship Extraction
- **Preferences**: likes, dislikes, prefers
- **Associations**: works at, lives in, member of
- **Actions**: bought, visited, attended
- **Attributes**: is, has, owns
- **Temporal**: before, after, during
- **Social**: friend of, colleague of, family of

### Graph Intelligence
- **Conflict Resolution**: Merges duplicate entities intelligently
- **Confidence Scoring**: Assigns confidence levels to extracted data
- **Temporal Context**: Tracks when relationships were established
- **Memory Linking**: Connects entities across multiple memories

## Verification

After configuration, you should see logs like:

```
[Information] Starting graph processing for memory 123
[Information] Extracted 3 entities and 2 relationships from memory 123
[Information] Graph processing completed for memory 123: 2 entities, 1 relationships added in 1250ms
```

## Troubleshooting

### API Key Issues
- **"LLM features will be disabled"**: API key not found or invalid
- **"Mock response from mock-openai"**: Using MockAgent fallback

### Graph Processing Issues
- **No entities extracted**: Content may be too short or non-informative
- **Processing timeout**: Increase timeout in provider settings
- **High processing time**: Consider using faster models (GPT-3.5, Claude Haiku)

## Cost Optimization

To optimize API costs:

1. **Use faster, cheaper models** for initial processing:
   - OpenAI: `gpt-3.5-turbo`
   - Anthropic: `claude-3-haiku-20240307`

2. **Adjust MaxTokens** based on your content:
   - Short messages: 500 tokens
   - Long conversations: 1500 tokens

3. **Enable caching** in production:
   ```json
   "Memory": {
     "EnableCaching": true,
     "CacheSize": 1000
   }
   ```

## Disabling LLM Features

To disable LLM processing while keeping other features:

```json
{
  "MemoryServer": {
    "LLM": {
      "EnableGraphProcessing": false
    }
  }
}
```

The server will continue to work normally but without intelligent entity/relationship extraction. 