# Model Research Methodology

## Overview

This document provides a systematic approach to researching new language models and their support across different providers. Use this guide when adding new models to the LMConfig system or when evaluating model availability for specific use cases.

## Provider Research Links and Methodology

### 1. OpenAI (Native Provider)

#### Primary Resources
- **Models Documentation**: https://platform.openai.com/docs/models
- **API Reference**: https://platform.openai.com/docs/api-reference/models
- **Pricing**: https://openai.com/api/pricing/
- **Changelog**: https://platform.openai.com/docs/changelog

#### Research Methodology
1. **Check Models Page**: Visit the models documentation for latest model releases
2. **API Reference**: Verify API endpoints and parameters
3. **Pricing Calculator**: Get accurate cost per token
4. **Changelog**: Check for recent updates, deprecations, or new features

#### Key Information to Extract
```
- Model ID (e.g., "gpt-4o", "gpt-4o-mini")
- Context window size
- Output token limits
- Pricing (prompt/completion per million tokens)
- Capabilities (function calling, vision, audio, etc.)
- Release date and status (preview, stable, deprecated)
```

### 2. Anthropic (Native Provider)

#### Primary Resources
- **Models Overview**: https://docs.anthropic.com/en/docs/about-claude/models
- **API Documentation**: https://docs.anthropic.com/en/api/messages
- **Pricing**: https://www.anthropic.com/pricing
- **Model Capabilities**: https://docs.anthropic.com/en/docs/build-with-claude/tool-use

#### Research Methodology
1. **Models Page**: Check for new Claude variants and capabilities
2. **API Docs**: Verify thinking parameters and message format
3. **Tool Use Guide**: Check function calling capabilities
4. **Vision Guide**: Check multimodal capabilities

#### Key Information to Extract
```
- Model name (e.g., "claude-3-5-sonnet-20241022")
- Context window and max output tokens
- Thinking capability (budget tokens, parameter names)
- Vision support (formats, size limits)
- Tool use capabilities
- Pricing per token
```

### 3. DeepInfra (OpenAI-Compatible)

#### Primary Resources
- **Model Gallery**: https://deepinfra.com/models
- **API Documentation**: https://deepinfra.com/docs
- **Pricing**: https://deepinfra.com/pricing
- **Model Search**: https://deepinfra.com/models?type=text-generation

#### Research Methodology
1. **Search by Model Family**: Use filters (GPT, Llama, DeepSeek, etc.)
2. **Check Model Cards**: Click on specific models for details
3. **API Endpoint**: Verify OpenAI compatibility and endpoint format
4. **Pricing Calculator**: Check per-token costs

#### Search Process
```bash
# Example searches on DeepInfra
1. Search "GPT-4" → Check for OpenAI model availability
2. Search "Llama-3" → Check for Meta model variants
3. Search "DeepSeek" → Check for reasoning model availability
4. Filter by "Text Generation" → See all available chat models
```

#### Key Information to Extract
```
- DeepInfra model path (e.g., "openai/gpt-4o")
- OpenAI compatibility level
- Pricing compared to native provider
- Available model variants
- Performance metrics (if available)
```

### 4. Groq (OpenAI-Compatible)

#### Primary Resources
- **Models Documentation**: https://console.groq.com/docs/models
- **API Reference**: https://console.groq.com/docs/api-reference
- **Pricing**: https://console.groq.com/docs/pricing
- **Performance Benchmarks**: https://groq.com/

#### Research Methodology
1. **Models List**: Check supported models and their IDs
2. **Performance Data**: Note tokens/second metrics
3. **API Compatibility**: Verify OpenAI API compatibility
4. **Model Limits**: Check context windows and restrictions

#### Key Information to Extract
```
- Groq model ID (e.g., "llama3-70b-8192")
- Tokens per second performance
- Context window size
- Pricing per million tokens
- Hardware acceleration benefits
```

### 5. Cerebras (OpenAI-Compatible)

#### Primary Resources
- **Inference Documentation**: https://inference-docs.cerebras.ai/
- **Quickstart Guide**: https://inference-docs.cerebras.ai/quickstart
- **Model List**: https://inference-docs.cerebras.ai/reference/chat-completions
- **Pricing**: https://www.cerebras.ai/pricing

#### Research Methodology
1. **API Reference**: Check supported models in chat completions
2. **Performance Claims**: Look for speed benchmarks
3. **Model Availability**: Check which models are available
4. **Pricing Structure**: Understand cost model

#### Key Information to Extract
```
- Cerebras model names
- Ultra-fast inference claims (validate with benchmarks)
- OpenAI API compatibility level
- Pricing structure
- Hardware advantages
```

### 6. OpenRouter (Aggregator with Subproviders) 🔄

#### Primary Resources
- **Models List**: https://openrouter.ai/models
- **API Documentation**: https://openrouter.ai/docs/quick-start
- **Pricing Comparison**: https://openrouter.ai/models?pricing=true
- **Provider Status**: https://openrouter.ai/status
- **Provider Details**: https://openrouter.ai/docs/providers

#### Research Methodology
1. **Model Search**: Use the models page with filters
2. **Provider Comparison**: Check which providers offer each model
3. **Pricing Analysis**: Compare costs across providers
4. **Status Monitoring**: Check provider availability
5. **Subprovider Research**: Identify underlying providers for each model

#### Subprovider Research Process
```bash
# OpenRouter subprovider research process
1. Visit https://openrouter.ai/models
2. Search for specific model (e.g., "GPT-4", "Claude", "Llama")
3. Click on model to see detailed provider information
4. Note all underlying providers (subproviders) that serve this model:
   - OpenAI (for GPT models)
   - Anthropic (for Claude models)  
   - Together AI (for Llama models)
   - Fireworks AI (for various models)
   - Perplexity (for specific variants)
5. Check pricing for each subprovider route
6. Note any routing preferences or load balancing
7. Check provider reliability and status
```

#### Subprovider Identification
When researching an OpenRouter model, identify:
```
Primary Model: gpt-4o
OpenRouter Provider: OpenRouter
Available Subproviders:
├── OpenAI (native) - $2.50/$10.00 per 1M tokens - Priority: 1
├── Together AI - $2.40/$9.60 per 1M tokens - Priority: 2  
├── Fireworks AI - $2.30/$9.20 per 1M tokens - Priority: 3
└── DeepInfra - $2.20/$9.00 per 1M tokens - Priority: 4
```

#### Key Information to Extract
```
- All providers offering the model (subproviders)
- Price comparison across subproviders
- Provider reliability metrics for each subprovider
- Model-specific routing preferences
- Failover capabilities between subproviders
- Regional availability of subproviders
- Performance differences between subprovider routes
```

### 7. Google Gemini (OpenAI-Compatible)

#### Primary Resources
- **AI Studio**: https://ai.google.dev/gemini-api/docs/ai-studio-quickstart
- **API Documentation**: https://ai.google.dev/gemini-api/docs
- **Model Garden**: https://ai.google.dev/gemini-api/docs/models/gemini
- **Pricing**: https://ai.google.dev/pricing

#### Research Methodology
1. **Model Garden**: Check available Gemini models
2. **API Docs**: Verify OpenAI compatibility layer
3. **Capabilities**: Check multimodal, long context support
4. **Pricing**: Compare with other providers

#### Key Information to Extract
```
- Gemini model variants (Flash, Pro, Ultra)
- Context window sizes (up to 2M tokens)
- Multimodal capabilities (image, audio, video)
- OpenAI compatibility features
- Pricing per token
```

## Cost Tracking and Analysis

### Three-Tier Cost Tracking

Our system tracks costs at three levels:
1. **Model Level**: Base model costs and capabilities
2. **Provider Level**: Provider-specific pricing and routing
3. **Subprovider Level**: Actual execution provider (for aggregators)

#### Cost Tracking Structure
```
Model: gpt-4o
├── Provider: OpenAI (Direct)
│   └── Cost: $2.50/$10.00 per 1M tokens
├── Provider: OpenRouter (Aggregator)
│   ├── Subprovider: OpenAI → $2.50/$10.00 per 1M tokens
│   ├── Subprovider: Together AI → $2.40/$9.60 per 1M tokens
│   └── Subprovider: Fireworks AI → $2.30/$9.20 per 1M tokens
└── Provider: DeepInfra (Direct)
    └── Cost: $2.20/$9.00 per 1M tokens
```

### Enhanced Cost Research Methodology

#### Step 1: Direct Provider Research
For each model, research direct provider pricing:
```markdown
## Direct Provider Costs for [MODEL_NAME]
- OpenAI: $X.XX/$X.XX per 1M tokens
- Anthropic: $X.XX/$X.XX per 1M tokens  
- DeepInfra: $X.XX/$X.XX per 1M tokens
- Groq: $X.XX/$X.XX per 1M tokens
- Cerebras: $X.XX/$X.XX per 1M tokens
- Google Gemini: $X.XX/$X.XX per 1M tokens
```

#### Step 2: Aggregator Subprovider Research
For aggregators like OpenRouter:
```markdown
## OpenRouter Subprovider Costs for [MODEL_NAME]
- Route 1: OpenAI → $X.XX/$X.XX per 1M tokens (reliability: 99.9%)
- Route 2: Together AI → $X.XX/$X.XX per 1M tokens (reliability: 99.5%)
- Route 3: Fireworks AI → $X.XX/$X.XX per 1M tokens (reliability: 99.2%)
- Route 4: DeepInfra → $X.XX/$X.XX per 1M tokens (reliability: 98.8%)
```

#### Step 3: Cost Optimization Analysis
```markdown
## Cost Analysis for [MODEL_NAME]
### Cheapest Options:
1. DeepInfra (Direct): $X.XX total
2. OpenRouter → Fireworks AI: $X.XX total
3. OpenRouter → Together AI: $X.XX total

### Most Reliable Options:
1. OpenAI (Direct): $X.XX total (99.9% uptime)
2. OpenRouter → OpenAI: $X.XX total (99.9% uptime)
3. Anthropic (Direct): $X.XX total (99.8% uptime)

### Best Value (Cost vs Reliability):
1. OpenRouter → Together AI: $X.XX total (99.5% uptime)
2. DeepInfra (Direct): $X.XX total (98.8% uptime)
```

## Systematic Research Process

### Step 1: Initial Model Discovery

1. **Monitor Release Announcements**
   - Follow provider Twitter/X accounts
   - Subscribe to API changelogs
   - Monitor AI news sources (HuggingFace, Papers with Code)

2. **Check Official Blogs and Documentation**
   - OpenAI: https://openai.com/index/
   - Anthropic: https://www.anthropic.com/news
   - Google AI: https://blog.google/technology/ai/

### Step 2: Multi-Provider Research

Use this enhanced checklist for each new model:

```markdown
## Model Research Checklist: [MODEL_NAME]

### Provider Availability
- [ ] OpenAI Native: ✓/✗ - Model ID: _____, Pricing: _____
- [ ] Anthropic Native: ✓/✗ - Model ID: _____, Pricing: _____
- [ ] DeepInfra: ✓/✗ - Model Path: _____, Pricing: _____
- [ ] Groq: ✓/✗ - Model ID: _____, Performance: _____
- [ ] Cerebras: ✓/✗ - Model ID: _____, Performance: _____
- [ ] Google Gemini: ✓/✗ - Model ID: _____, Pricing: _____

### OpenRouter Subprovider Analysis
- [ ] OpenRouter Available: ✓/✗ 
- [ ] Subprovider 1: _____ - Pricing: _____ - Reliability: _____%
- [ ] Subprovider 2: _____ - Pricing: _____ - Reliability: _____%
- [ ] Subprovider 3: _____ - Pricing: _____ - Reliability: _____%
- [ ] Default Routing: _____ (which subprovider gets priority)
- [ ] Failover Logic: _____ (how fallbacks work)

### Capabilities Assessment
- [ ] Context Window: _____ tokens
- [ ] Max Output: _____ tokens
- [ ] Function Calling: ✓/✗
- [ ] Vision/Multimodal: ✓/✗
- [ ] Thinking/Reasoning: ✓/✗ - Type: _____
- [ ] Streaming: ✓/✗
- [ ] JSON Mode: ✓/✗

### Cost Analysis
- [ ] Cheapest Direct Provider: _____
- [ ] Cheapest via OpenRouter: _____ (subprovider: _____)
- [ ] Best Performance/Cost: _____
- [ ] Most Reliable Option: _____

### Tags to Assign
- [ ] Performance: [fast/ultra-fast/economic/premium]
- [ ] Capabilities: [reasoning/multimodal/coding/creative]
- [ ] Characteristics: [openai-compatible/high-performance/aggregator/etc.]
```

### Step 3: Capability Deep Dive

For each model, research specific capabilities:

#### Thinking/Reasoning Models
1. **Check for reasoning capabilities**
   - Look for "reasoning", "thinking", "CoT" (Chain of Thought) mentions
   - Test with reasoning-heavy prompts
   - Check if thinking process is exposed

2. **Parameter Research**
   ```bash
   # Questions to answer:
   - Does it support thinking budget tokens?
   - What parameter names are used?
   - Is thinking built-in or configurable?
   - Is thinking content returned or hidden?
   ```

#### Multimodal Models
1. **Image Support**
   - Supported formats (JPEG, PNG, WebP, GIF)
   - Size limits per image
   - Number of images per request
   - Resolution handling

2. **Audio/Video Support**
   - Audio formats (MP3, WAV, M4A)
   - Video support (rare but emerging)
   - Processing limitations

#### Function Calling Models
1. **Tool Use Capabilities**
   - Parallel function calling support
   - Tool choice options (auto, required, specific)
   - Maximum tools per request
   - JSON schema support

### Step 4: Performance Testing

When possible, conduct performance tests:

```csharp
// Example performance test script
var testPrompt = "Explain quantum computing in simple terms.";
var providers = ["OpenAI", "DeepInfra", "Groq", "Cerebras"];

foreach (var provider in providers)
{
    var start = DateTime.UtcNow;
    var response = await TestProvider(provider, model, testPrompt);
    var latency = DateTime.UtcNow - start;
    
    Console.WriteLine($"{provider}: {latency.TotalSeconds:F2}s, {response.Length} chars");
}
```

## Documentation Templates

### Model Configuration Template

```json
{
  "Id": "new-model-id",
  "IsReasoning": false,
  "Capabilities": {
    "thinking": null,
    "multimodal": {
      "supports_images": true,
      "supports_audio": false,
      "supports_video": false,
      "supported_image_formats": ["jpeg", "png", "webp"],
      "max_image_size": 20971520,
      "max_images_per_message": 10
    },
    "function_calling": {
      "supports_tools": true,
      "supports_parallel_calls": true,
      "supports_tool_choice": true,
      "max_tools_per_request": 128,
      "supported_tool_types": ["function"]
    },
    "token_limits": {
      "max_context_tokens": 128000,
      "max_output_tokens": 4096,
      "recommended_max_prompt_tokens": 120000
    },
    "response_formats": {
      "supports_json_mode": true,
      "supports_structured_output": true,
      "supports_json_schema": true
    },
    "supports_streaming": true,
    "supported_features": ["multimodal", "function-calling"],
    "performance": {
      "typical_latency": "00:00:02",
      "max_latency": "00:00:10",
      "tokens_per_second": 75.0,
      "quality_tier": "high"
    }
  },
  "Providers": [
    {
      "Name": "ProviderName",
      "ModelName": "provider-specific-model-name",
      "Priority": 2,
      "Pricing": {
        "PromptPerMillion": 2.5,
        "CompletionPerMillion": 10.0
      },
      "Tags": ["fast", "multimodal", "openai-compatible"]
    }
  ]
}
```

## Research Tools and Scripts

### Automated Model Discovery Script

```bash
#!/bin/bash
# model-discovery.sh - Automated model research script

MODEL_NAME=$1

echo "Researching model: $MODEL_NAME"
echo "=================================="

# OpenAI
echo "Checking OpenAI..."
curl -s "https://api.openai.com/v1/models" \
  -H "Authorization: Bearer $OPENAI_API_KEY" | \
  jq -r ".data[] | select(.id | contains(\"$MODEL_NAME\")) | .id"

# DeepInfra
echo "Checking DeepInfra..."
curl -s "https://api.deepinfra.com/v1/openai/models" \
  -H "Authorization: Bearer $DEEPINFRA_API_KEY" | \
  jq -r ".data[] | select(.id | contains(\"$MODEL_NAME\")) | .id"

# Add similar checks for other providers...
```

### Cost Comparison Script

```csharp
// CostComparisonTool.cs
public class CostComparisonTool
{
    public async Task<Dictionary<string, decimal>> CompareCosts(
        string modelId, 
        int promptTokens = 1000, 
        int completionTokens = 500)
    {
        var costs = new Dictionary<string, decimal>();
        
        foreach (var provider in GetProvidersForModel(modelId))
        {
            var config = provider.Pricing;
            var totalCost = 
                (promptTokens * config.PromptPerMillion / 1_000_000m) +
                (completionTokens * config.CompletionPerMillion / 1_000_000m);
                
            costs[provider.Name] = totalCost;
        }
        
        return costs.OrderBy(kvp => kvp.Value).ToDictionary();
    }
}
```

## Continuous Monitoring

### Weekly Research Routine

1. **Monday: Check for New Models**
   - Review OpenAI changelog
   - Check Anthropic announcements
   - Scan provider model lists

2. **Wednesday: Cost Analysis Update**
   - Update pricing information
   - Compare provider costs
   - Identify new cost-effective options

3. **Friday: Capability Assessment**
   - Test new features
   - Update capability matrices
   - Document any breaking changes

### Automated Alerts

Set up monitoring for:
- New model announcements on provider blogs
- API changelog updates
- Pricing changes
- Provider status updates

## Common Research Patterns

### New Reasoning Model Research
1. Check for "reasoning", "thinking", "CoT" keywords
2. Test with complex reasoning prompts
3. Identify thinking configuration options
4. Compare reasoning quality across providers

### New Multimodal Model Research
1. Test image processing capabilities
2. Check supported formats and limits
3. Evaluate audio/video support
4. Assess quality vs. cost trade-offs

### New Code Model Research
1. Test coding capabilities across languages
2. Check function calling support
3. Evaluate code quality and accuracy
4. Compare performance for code generation

## Research Quality Checklist

Before adding a new model to LMConfig:

- [ ] Verified availability across all relevant providers
- [ ] Documented all capabilities accurately
- [ ] Tested API compatibility
- [ ] Compared pricing across providers
- [ ] Assigned appropriate tags
- [ ] Created provider configurations
- [ ] Added to appsettings examples
- [ ] Updated documentation
- [ ] Created test cases

This systematic approach ensures comprehensive research and accurate model integration into the LMConfig system. 