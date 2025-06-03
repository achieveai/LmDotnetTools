# Cost Analysis and Tracking Examples

## Overview

This document demonstrates how our LMConfig system tracks costs at three levels:
1. **Model Level**: Base model identification
2. **Provider Level**: Provider-specific pricing and routing
3. **Subprovider Level**: Actual execution provider (for aggregators like OpenRouter)

## Three-Tier Cost Tracking Architecture

### Cost Hierarchy Structure

```
Model: gpt-4o (2000 prompt tokens, 500 completion tokens)
├── Direct Providers
│   ├── OpenAI: $0.0075 total ($0.005 prompt + $0.0025 completion)
│   └── DeepInfra: $0.0069 total ($0.0044 prompt + $0.0025 completion)
└── Aggregator Providers
    └── OpenRouter: Routes to multiple subproviders
        ├── Subprovider: OpenAI → $0.0075 total (same as direct)
        ├── Subprovider: Azure OpenAI → $0.0075 total
        ├── Subprovider: Together AI → $0.0069 total
        └── Subprovider: Fireworks AI → $0.0065 total (cheapest option)
```

### Cost Tracking Models

```csharp
// Example cost estimation with subprovider details
var estimation = new CostEstimation
{
    ModelId = "gpt-4o",
    Provider = "OpenRouter",
    SubProvider = "Fireworks AI",  // Actual routing destination
    SelectedModel = "accounts/fireworks/models/gpt-4o",
    EstimatedPromptTokens = 2000,
    EstimatedCompletionTokens = 500,
    EstimatedPromptCost = 0.0044m,
    EstimatedCompletionCost = 0.0021m,
    TotalEstimatedCost = 0.0065m,
    PricingInfo = new PricingConfig
    {
        PromptPerMillion = 2.2,
        CompletionPerMillion = 4.2
    }
};

Console.WriteLine($"Route: {estimation.ProviderPath}"); // "OpenRouter -> Fireworks AI"
```

## Practical Cost Analysis Examples

### Example 1: GPT-4o Cost Comparison

#### Research Results
```markdown
## GPT-4o Cost Analysis (1000 prompt + 500 completion tokens)

### Direct Providers
- OpenAI: $0.00375 ($0.0025 + $0.00125)
- DeepInfra: $0.00345 ($0.0022 + $0.00125) - 8% savings

### OpenRouter Subproviders
- OpenRouter → OpenAI: $0.00375 (same as direct)
- OpenRouter → Azure OpenAI: $0.00375 (same as direct)
- OpenRouter → Together AI: $0.00345 (8% savings)
- OpenRouter → Fireworks AI: $0.00325 (13% savings) ⭐ CHEAPEST

### Recommendation
Best Value: OpenRouter → Fireworks AI (13% cost savings with automatic failover)
Most Reliable: OpenAI Direct (99.9% uptime guarantee)
```

#### Configuration Example
```csharp
// Prefer the most economical OpenRouter subprovider
var config = new LMConfigBuilder()
    .WithModel("gpt-4o")
    .PreferEconomic()
    .RequireTags(ProviderTags.Aggregator)  // Forces OpenRouter selection
    .WithBudgetLimit(0.004m)               // Will select Fireworks AI subprovider
    .Build();

var estimation = await modelConfigService.EstimateCostAsync(config, 1000, 500);
// Result: OpenRouter -> Fireworks AI, $0.00325 total
```

### Example 2: Claude 3 Sonnet Cost Comparison

#### Research Results
```markdown
## Claude 3 Sonnet Cost Analysis (2000 prompt + 800 completion tokens)

### Direct Providers
- Anthropic: $0.018 ($0.006 + $0.012)

### OpenRouter Subproviders  
- OpenRouter → Anthropic: $0.018 (same as direct)
- OpenRouter → AWS Bedrock: $0.018 (same pricing, different regions)

### Analysis
- No cost savings available through OpenRouter for Claude
- OpenRouter provides value through automatic failover and multi-region support
- Recommendation: Use OpenRouter for reliability, direct Anthropic for simplicity
```

### Example 3: Llama 3.1 70B Cost Optimization

#### Research Results
```markdown
## Llama 3.1 70B Cost Analysis (1500 prompt + 600 completion tokens)

### Direct Providers
- Groq: $0.00137 ($0.000885 + $0.000474) - Ultra-fast inference
- DeepInfra: $0.00129 ($0.00078 + $0.00045) - 6% cheaper than Groq
- Cerebras: $0.00120 ($0.00075 + $0.00045) - 12% cheaper than Groq

### OpenRouter Subproviders
- OpenRouter → Together AI: $0.00137 (same as Groq pricing)
- OpenRouter → Fireworks AI: $0.00123 (10% cheaper than Groq)
- OpenRouter → Perplexity: $0.00121 (12% cheaper than Groq)
- OpenRouter → Lepton AI: $0.00108 (21% cheaper than Groq) ⭐ CHEAPEST

### Performance vs Cost Analysis
- Fastest: Groq (direct) - 750 tokens/sec, $0.00137
- Best Value: OpenRouter → Lepton AI - 200 tokens/sec, $0.00108 (21% savings)
- Balanced: Cerebras (direct) - 500 tokens/sec, $0.00120 (12% savings)
```

#### Smart Configuration Example
```csharp
// Balanced approach: prefer fast but allow economic if budget constrained
var config = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .PreferTags(ProviderTags.UltraFast, ProviderTags.Economic)  // Prefer fast, fallback to economic
    .WithBudgetLimit(0.0012m)                                   // Forces away from Groq to cheaper options
    .WithMaxLatency(TimeSpan.FromSeconds(5))                    // Ensures reasonable response time
    .Build();

// This will select Cerebras (direct) - fastest option within budget
```

## Cost Tracking Implementation Examples

### Real-Time Cost Tracking

```csharp
public class EnhancedCostTracker
{
    public async Task<CostReport> TrackRequestCost(
        string modelId,
        string provider,
        string? subProvider,
        int promptTokens,
        int completionTokens,
        TimeSpan responseTime,
        string requestId)
    {
        var providerConfig = await GetProviderConfig(provider, subProvider);
        var pricing = subProvider != null 
            ? GetSubProviderPricing(provider, subProvider)
            : providerConfig.Pricing;

        var report = new CostReport
        {
            ModelId = modelId,
            Provider = provider,
            SubProvider = subProvider,
            UsedModel = providerConfig.ModelName,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ActualPromptCost = pricing.CalculatePromptCost(promptTokens),
            ActualCompletionCost = pricing.CalculateCompletionCost(completionTokens),
            TotalActualCost = pricing.CalculateTotalCost(promptTokens, completionTokens),
            PricingInfo = pricing,
            ResponseTime = responseTime,
            RequestId = requestId,
            Timestamp = DateTime.UtcNow
        };

        await SaveCostReport(report);
        return report;
    }
}
```

### Cost Analysis Queries

```csharp
public class CostAnalysisService
{
    // Get cost breakdown by provider path
    public async Task<Dictionary<string, decimal>> GetCostByProviderPath(
        DateTime from, 
        DateTime to)
    {
        var reports = await GetCostReports(from, to);
        
        return reports
            .GroupBy(r => r.ProviderPath)  // "OpenRouter -> Fireworks AI"
            .ToDictionary(
                g => g.Key, 
                g => g.Sum(r => r.TotalActualCost)
            );
    }

    // Find most cost-effective subprovider routes
    public async Task<List<SubproviderAnalysis>> AnalyzeSubproviderCostEffectiveness(
        string modelId,
        TimeSpan period)
    {
        var from = DateTime.UtcNow - period;
        var reports = await GetCostReports(from, DateTime.UtcNow);
        
        return reports
            .Where(r => r.ModelId == modelId && r.SubProvider != null)
            .GroupBy(r => new { r.Provider, r.SubProvider })
            .Select(g => new SubproviderAnalysis
            {
                Provider = g.Key.Provider,
                SubProvider = g.Key.SubProvider!,
                RequestCount = g.Count(),
                TotalCost = g.Sum(r => r.TotalActualCost),
                AverageCost = g.Average(r => r.TotalActualCost),
                AverageResponseTime = TimeSpan.FromMilliseconds(
                    g.Where(r => r.ResponseTime.HasValue)
                     .Average(r => r.ResponseTime!.Value.TotalMilliseconds)
                ),
                CostEfficiencyScore = CalculateCostEfficiencyScore(g.ToList())
            })
            .OrderBy(a => a.AverageCost)
            .ToList();
    }
}

public record SubproviderAnalysis
{
    public required string Provider { get; init; }
    public required string SubProvider { get; init; }
    public int RequestCount { get; init; }
    public decimal TotalCost { get; init; }
    public decimal AverageCost { get; init; }
    public TimeSpan AverageResponseTime { get; init; }
    public double CostEfficiencyScore { get; init; }  // Cost vs performance score
    
    public string ProviderPath => $"{Provider} -> {SubProvider}";
}
```

## OpenRouter-Specific Cost Optimization

### Understanding OpenRouter Routing

OpenRouter uses intelligent routing based on:
1. **Cost preferences** (if you specify economic selection)
2. **Provider availability** (automatic failover)
3. **Geographic proximity** (latency optimization)
4. **Load balancing** (distributing requests across subproviders)

### OpenRouter Cost Research Process

```bash
# Step-by-step OpenRouter research for a new model
1. Visit https://openrouter.ai/models
2. Search for model: "llama-3.1-70b"
3. Click on model to see provider details:
   
   Model: meta-llama/llama-3.1-70b-instruct
   Available Providers:
   ├── Together AI: $0.59/$0.79 per 1M tokens (Primary)
   ├── Fireworks AI: $0.54/$0.74 per 1M tokens (Secondary) 
   ├── Perplexity: $0.52/$0.75 per 1M tokens (Tertiary)
   └── Lepton AI: $0.50/$0.70 per 1M tokens (Economy)

4. Note routing preferences and reliability metrics
5. Test actual routing behavior with API calls
6. Document in SubProviders configuration
```

### Configuration for OpenRouter Cost Optimization

```csharp
// Force cheapest subprovider selection
var economicConfig = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .WithOpenRouter(openRouter => openRouter
        .PreferCheapestSubprovider()      // Route to Lepton AI
        .WithFailoverEnabled()            // Fallback to more expensive if needed
        .WithMaxRetries(3))
    .WithBudgetLimit(0.0011m)             // Forces cheapest routing
    .Build();

// Balanced approach with reliability
var balancedConfig = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .WithOpenRouter(openRouter => openRouter
        .PreferReliableSubproviders()     // Route to Together AI or Fireworks AI
        .WithCostThreshold(0.0015m)       // Don't exceed this cost
        .WithLatencyPriority())           // Prefer faster subproviders
    .Build();
```

## Cost Monitoring and Alerts

### Budget Management with Subprovider Tracking

```csharp
public class BudgetManager
{
    public async Task<BudgetStatus> CheckBudget(
        string modelId,
        decimal requestBudget,
        ProviderSelectionStrategy strategy)
    {
        // Get all available provider options
        var costComparison = await CreateCostComparison(modelId, 1000, 500);
        
        // Filter options within budget
        var affordableOptions = costComparison.Options
            .Where(o => o.TotalCost <= requestBudget)
            .ToList();

        if (!affordableOptions.Any())
        {
            return new BudgetStatus
            {
                IsWithinBudget = false,
                CheapestOption = costComparison.Cheapest,
                RequiredBudget = costComparison.Cheapest.TotalCost,
                Recommendation = $"Increase budget to {costComparison.Cheapest.TotalCost:C4} to use {costComparison.Cheapest.ProviderPath}"
            };
        }

        // Select best option based on strategy
        var selectedOption = strategy switch
        {
            ProviderSelectionStrategy.Economic => affordableOptions.OrderBy(o => o.TotalCost).First(),
            ProviderSelectionStrategy.Fast => affordableOptions.OrderBy(o => o.LatencyScore).First(),
            ProviderSelectionStrategy.HighReliability => affordableOptions.OrderByDescending(o => o.UptimePercentage).First(),
            _ => affordableOptions.OrderBy(o => o.TotalCost).First()
        };

        return new BudgetStatus
        {
            IsWithinBudget = true,
            SelectedOption = selectedOption,
            PotentialSavings = requestBudget - selectedOption.TotalCost,
            AlternativeOptions = affordableOptions.Where(o => o != selectedOption).ToList()
        };
    }
}

public record BudgetStatus
{
    public bool IsWithinBudget { get; init; }
    public CostOption? SelectedOption { get; init; }
    public CostOption? CheapestOption { get; init; }
    public decimal? RequiredBudget { get; init; }
    public decimal? PotentialSavings { get; init; }
    public string? Recommendation { get; init; }
    public IReadOnlyList<CostOption>? AlternativeOptions { get; init; }
}
```

## Best Practices for Cost Management

### 1. Research Phase Best Practices
- Always research both direct and aggregator pricing
- Document subprovider options for each model
- Consider reliability vs cost trade-offs
- Monitor pricing changes regularly

### 2. Configuration Best Practices
- Set realistic budget limits based on your cost analysis
- Use tags to express preferences (economic, fast, reliable)
- Configure appropriate fallback strategies
- Test configurations with cost estimation before production

### 3. Monitoring Best Practices
- Track costs at the subprovider level for accurate analysis
- Set up alerts for cost anomalies
- Review cost reports regularly to identify optimization opportunities
- Monitor subprovider performance and reliability metrics

### 4. Optimization Best Practices
- Regularly analyze subprovider cost-effectiveness
- Adjust configurations based on actual usage patterns
- Consider using economic subproviders for non-critical requests
- Implement cost-aware caching strategies

This comprehensive cost tracking system ensures you have full visibility into your LLM costs across all provider tiers, enabling data-driven optimization decisions. 