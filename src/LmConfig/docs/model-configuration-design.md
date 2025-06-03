# Model Configuration & LLM Provider Resolution Design

## Overview
This document describes how LLM models and their providers are configured, loaded, and dynamically selected at runtime based on priority and availability. It covers storage schema, environment variables, DI setup, resolution algorithm, fallback strategies, and extensibility.

---

## Configuration Schema

### AppConfig
```csharp
public record AppConfig
{
    public required IReadOnlyList<ModelConfig> Models { get; init; }
}
```

### ModelConfig
- **Id** (`string`): Unique model identifier (e.g. `gpt-4`).
- **IsReasoning** (`bool`): Indicates special reasoning capabilities.
- **Providers** (`IReadOnlyList<ProviderConfig>`): Ordered list of provider candidates.

```csharp
public record ModelConfig
{
    public required string Id { get; init; }
    public required bool IsReasoning { get; init; }
    public required IReadOnlyList<ProviderConfig> Providers { get; init; }
}
```

### ProviderConfig
- **Name** (`string`): Provider key (e.g. `OpenAI`, `AzureAI`).
- **ModelName** (`string`): Provider-specific model name.
- **Priority** (`int`, default `1`): Higher value = preferred.
- **Pricing** (`PricingConfig`): Cost metrics for usage tracking.
- **SubProviders** (optional): List of `SubProviderConfig` for tiered failover.
- **Tags** (optional): Custom tags for filtering (e.g. `chat`, `reasoning`).

```csharp
public record ProviderConfig
{
    public required string Name { get; init; }
    public required string ModelName { get; init; }
    public int Priority { get; init; } = 1;
    public required PricingConfig Pricing { get; init; }
    public IReadOnlyList<SubProviderConfig>? SubProviders { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}
```

### SubProviderConfig & PricingConfig
```csharp
public record SubProviderConfig
{
    public required string Name { get; init; }
    public required string ModelName { get; init; }
    public required int Priority { get; init; }
    public required PricingConfig Pricing { get; init; }
}

public record PricingConfig
{
    public required double PromptPerMillion { get; init; }
    public required double CompletionPerMillion { get; init; }
}
```

---

## Storage in appsettings.json
Place under a top-level section, e.g.: 

```json
"AppConfig": {
  "Models": [
    {
      "Id": "gpt-4",
      "IsReasoning": true,
      "Providers": [
        {
          "Name": "OpenAI",
          "ModelName": "gpt-4",
          "Priority": 2,
          "Pricing": {
            "PromptPerMillion": 0.03,
            "CompletionPerMillion": 0.06
          },
          "Tags": ["chat","reasoning"]
        },
        {
          "Name": "AzureAI",
          "ModelName": "gpt-4",
          "Priority": 1,
          "Pricing": {
            "PromptPerMillion": 0.03,
            "CompletionPerMillion": 0.06
          }
        }
      ]
    }
  ]
}
```

---

## Environment Variables for Credentials
Each provider must expose two env vars:
- `{PROVIDER_NAME}_API_KEY`
- `{PROVIDER_NAME}_BASE_URL`

`DynamicProviderAgent.HasProviderCredentials` checks both.

---

## Dependency Injection Setup
In `Program.cs` or Startup:
```csharp
builder.Services.Configure<AppConfig>(
    builder.Configuration.GetSection("AppConfig")
);
builder.Services.AddModelConfigurationServices(
    builder.Configuration
);
```
Registers `ModelConfigurationService` and the `StreamingAgentFactory` using `DynamicProviderAgent`.

---

## Provider Resolution Algorithm
Pseudocode for `SelectProviderForModel(modelId, requestedTags)`:  
1. `modelConfig = GetModelConfig(modelId)`  
2. `providers = modelConfig.Providers`  
3. If `requestedTags` given: filter providers where all tags match.  
4. `available = providers.Where(HasProviderCredentials)`  
5. Sort `available` by descending `Priority`.  
6. If any: return first as chosen provider.  

**Fallback #1**: Search across all models for `ProviderConfig.Name == "OpenRouter"` with credentials, pick highest priority.  
**Fallback #2**: Search across all models for any provider with credentials, pick highest priority.  
If none: throw/no provider available.

See `DynamicProviderAgent.SelectProviderForModel` in code.

---

## Middleware Pipeline & Agent Creation
For the chosen provider, `CreateAgentForProvider`:
1. Instantiate base client:
```csharp
var client = new OpenClient(
    Env.GetString($"{providerName}_API_KEY"),
    Env.GetString($"{providerName}_BASE_URL")
);
```  
2. Wrap in `OpenClientAgent`.  
3. Call `BuildStandardMiddlewarePipeline` to add:
   - OpenRouter-specific middlewares
   - Caching
   - Usage tracking with pricing
   - Model ID mapping
   - Message conversion

---

## Extensibility
- **Adding providers**: update `appsettings.json` and set env vars.
- **Priority control**: adjust `Priority` fields to influence failover order.
- **Tags & filtering**: use `Tags` in `ProviderConfig` and pass `requestedTags` to agent.
- **SubProviders**: can be resolved via `GetSubProviderConfig` for more granular routing.
