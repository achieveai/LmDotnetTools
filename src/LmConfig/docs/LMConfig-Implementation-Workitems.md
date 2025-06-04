# LMConfig Implementation Work Items

## Executive Summary

This document outlines the implementation plan for the unified LMConfig system, breaking down the comprehensive design into manageable phases and work items. The implementation is structured to maintain backward compatibility while incrementally adding new capabilities.

**Total Estimated Effort**: 13-16 weeks  
**Team Size**: 2-3 developers  
**Target Completion**: Q2 2024  

## Implementation Strategy

### Core Principles
1. **Incremental Delivery**: Each phase delivers working functionality
2. **Backward Compatibility**: Existing code continues to work throughout implementation
3. **Test-Driven Development**: Comprehensive test coverage for all new features
4. **Documentation-First**: Clear documentation and examples for each feature

### Risk Mitigation
- Maintain existing `GenerateReplyOptions` API during transition
- Implement feature flags for gradual rollout
- Comprehensive validation to prevent runtime errors
- Fallback mechanisms for unsupported configurations

## Phase Overview

| Phase | Duration | Focus | Key Deliverables |
|-------|----------|-------|------------------|
| Phase 1 | 3 weeks | Foundation | Model capabilities, basic LMConfig |
| Phase 2 | 4 weeks | Provider Integration | Enhanced selection, provider configs, connection management |
| Phase 3 | 2 weeks | Cost Management | Cost estimation, budget constraints |
| Phase 4 | 3 weeks | Advanced Features | Auto-adaptation, cross-model support |
| Phase 5 | 2 weeks | Testing & Docs | Complete test coverage, migration guides |

---

## Phase 1: Foundation (3 weeks)

### Objective
Establish the core model capabilities system and basic LMConfig infrastructure.

### Work Items

#### 1.1 Model Capabilities System ✅ COMPLETED
**Priority**: Must Have  
**Effort**: 5 days  
**Dependencies**: None  

**Description**: Implement the core model capabilities system to define what each model can do.

**Tasks**:
- [x] Create `ModelCapabilities` record with all capability types
- [x] Implement `ThinkingCapability` with support for different thinking types
- [x] Implement `MultimodalCapability` for image/audio/video support
- [x] Implement `FunctionCallingCapability` for tool support variations
- [x] Implement `TokenLimits`, `ResponseFormatCapability`, `PerformanceCharacteristics`
- [x] Create `ThinkingType` enum (None, Anthropic, DeepSeek, OpenAI, Custom)

**Acceptance Criteria**:
- [x] All capability records are immutable and well-documented
- [x] Capability types cover all major model variations
- [x] JSON serialization/deserialization works correctly
- [x] Validation prevents invalid capability combinations

**Testing Requirements**:
- [x] Unit tests for all capability record types
- [x] JSON serialization round-trip tests
- [x] Validation logic tests

#### 1.2 Enhanced ModelConfig
**Priority**: Must Have  
**Effort**: 2 days  
**Dependencies**: 1.1  

**Description**: Update ModelConfig to include capabilities and maintain backward compatibility.

**Tasks**:
- [ ] Add `Capabilities` property to `ModelConfig`
- [ ] Update appsettings.json schema documentation
- [ ] Create capability validation for model configurations
- [ ] Implement capability-based model filtering

**Acceptance Criteria**:
- [ ] Existing ModelConfig continues to work without capabilities
- [ ] New ModelConfig with capabilities validates correctly
- [ ] appsettings.json examples include comprehensive capability definitions
- [ ] Model filtering by capabilities works correctly

**Testing Requirements**:
- [ ] Backward compatibility tests with existing configurations
- [ ] Capability validation tests
- [ ] Model filtering tests

#### 1.3 Provider Tags System
**Priority**: Must Have  
**Effort**: 1 day  
**Dependencies**: None  

**Description**: Implement predefined provider tags and tag-based filtering.

**Tasks**:
- [ ] Create `ProviderTags` static class with predefined constants
- [ ] Implement tag validation logic
- [ ] Add tag-based provider filtering methods
- [ ] Update existing provider configurations with appropriate tags

**Acceptance Criteria**:
- [ ] All predefined tags are well-documented with clear meanings
- [ ] Tag validation prevents typos and invalid combinations
- [ ] Tag-based filtering works efficiently
- [ ] Existing providers are properly tagged

**Testing Requirements**:
- [ ] Tag validation tests
- [ ] Tag-based filtering performance tests
- [ ] Provider tag assignment tests

#### 1.4 Basic LMConfig Class
**Priority**: Must Have  
**Effort**: 3 days  
**Dependencies**: 1.1  

**Description**: Implement the core LMConfig class with basic configuration options.

**Tasks**:
- [ ] Create `LMConfig` class with all basic properties
- [ ] Implement `ThinkingConfig` and `MultimodalConfig` records
- [ ] Add capability requirements properties
- [ ] Implement basic validation logic
- [ ] Create conversion methods to/from `GenerateReplyOptions`

**Acceptance Criteria**:
- [ ] LMConfig covers all common configuration scenarios
- [ ] Validation catches common configuration errors
- [ ] Conversion to GenerateReplyOptions maintains backward compatibility
- [ ] Configuration is immutable after creation

**Testing Requirements**:
- [ ] Configuration validation tests
- [ ] Conversion round-trip tests
- [ ] Edge case handling tests

#### 1.5 Basic LMConfigBuilder
**Priority**: Must Have  
**Effort**: 4 days  
**Dependencies**: 1.4  

**Description**: Implement fluent API builder for LMConfig creation.

**Tasks**:
- [ ] Create `LMConfigBuilder` class with fluent interface
- [ ] Implement basic configuration methods (model, temperature, etc.)
- [ ] Add capability requirement methods
- [ ] Implement validation in Build() method
- [ ] Create provider selection strategy methods

**Acceptance Criteria**:
- [ ] Fluent API is intuitive and well-documented
- [ ] Builder validates configuration before creating LMConfig
- [ ] All common configuration patterns are supported
- [ ] Error messages are clear and actionable

**Testing Requirements**:
- [ ] Fluent API usage tests
- [ ] Validation error message tests
- [ ] Builder state management tests

---

## Phase 2: Provider Integration (4 weeks)

### Objective
Integrate the new configuration system with existing provider infrastructure, implement capability-aware provider selection, and enhance provider connection management.

### Work Items

#### 2.1 Enhanced ModelConfigurationService
**Priority**: Must Have  
**Effort**: 5 days  
**Dependencies**: 1.1, 1.4  

**Description**: Enhance ModelConfigurationService to support capability-aware provider selection.

**Tasks**:
- [ ] Add capability-aware provider selection methods
- [ ] Implement `GetModelCapabilities()` method
- [ ] Add `GetModelsWithCapabilities()` filtering
- [ ] Implement configuration validation against capabilities
- [ ] Add automatic configuration adaptation logic

**Acceptance Criteria**:
- [ ] Provider selection considers both tags and capabilities
- [ ] Capability validation prevents invalid configurations
- [ ] Automatic adaptation works for all supported models
- [ ] Fallback logic handles unsupported capabilities gracefully

**Testing Requirements**:
- [ ] Provider selection algorithm tests
- [ ] Capability validation tests
- [ ] Configuration adaptation tests
- [ ] Fallback scenario tests

#### 2.2 Provider Feature Configuration System
**Priority**: Must Have  
**Effort**: 4 days  
**Dependencies**: 1.4  

**Description**: Implement provider-specific feature configuration classes.

**Tasks**:
- [ ] Create abstract `ProviderFeatureConfig` base class
- [ ] Implement `OpenRouterFeatureConfig` with routing and parameters
- [ ] Implement `AnthropicFeatureConfig` with thinking and system prompts
- [ ] Implement `OpenAIFeatureConfig` with structured outputs
- [ ] Add validation and application logic for each provider

**Acceptance Criteria**:
- [ ] Each provider config supports its unique features
- [ ] Validation prevents invalid provider-specific configurations
- [ ] Configuration application works with existing agents
- [ ] Provider configs are extensible for new features

**Testing Requirements**:
- [ ] Provider-specific configuration tests
- [ ] Validation logic tests
- [ ] Integration tests with existing agents

#### 2.2.1 OpenAI-Compatible Providers Integration
**Priority**: Must Have  
**Effort**: 3 days  
**Dependencies**: 2.2  

**Description**: Configure and test support for OpenAI-compatible providers (DeepInfra, Cerebras, Groq, Google Gemini).

**Tasks**:
- [ ] Add DeepInfra provider configuration with model mappings and pricing
- [ ] Add Cerebras provider configuration with high-performance model support
- [ ] Add Groq provider configuration with ultra-fast inference models
- [ ] Add Google Gemini provider configuration with OpenAI compatibility layer
- [ ] Update provider tag assignments for new provider characteristics
- [ ] Create comprehensive test suite for OpenAI-compatible providers
- [ ] Validate API compatibility and feature support for each provider
- [ ] Add provider-specific error handling and fallback logic

**Acceptance Criteria**:
- [ ] All OpenAI-compatible providers work with existing OpenAI agent implementation
- [ ] Provider selection correctly identifies and prioritizes based on tags
- [ ] Cost estimation works accurately for all new providers
- [ ] Error handling provides clear feedback for provider-specific issues
- [ ] Provider failover works correctly when primary providers are unavailable

**Testing Requirements**:
- [ ] Integration tests for each OpenAI-compatible provider
- [ ] Cost calculation accuracy tests for all providers
- [ ] Provider selection and failover tests
- [ ] API compatibility validation tests
- [ ] Performance comparison tests between providers

**Provider-Specific Configuration Details**:

**DeepInfra**:
- Base URL: `https://api.deepinfra.com/v1/openai/`
- Authentication: API key via `DEEPINFRA_API_KEY`
- Tags: `["economic", "multi-vendor", "openai-compatible"]`
- Models: GPT-4o, GPT-4o-mini, Llama-3.1-70B, DeepSeek-R1-Distill

**Cerebras**:
- Base URL: `https://api.cerebras.ai/v1/`
- Authentication: API key via `CEREBRAS_API_KEY`
- Tags: `["ultra-fast", "high-performance", "openai-compatible"]`
- Models: Llama-4-Scout, Llama-3.1-8B, specialized inference models

**Groq**:
- Base URL: `https://api.groq.com/openai/v1/`
- Authentication: API key via `GROQ_API_KEY`
- Tags: `["ultra-fast", "high-performance", "openai-compatible"]`
- Models: Llama-3.1-70B, Llama-4-Scout, Gemma-2-9B, Mistral variants, DeepSeek-R1

**Google Gemini**:
- Base URL: `https://generativelanguage.googleapis.com/v1beta/openai/`
- Authentication: API key via `GEMINI_API_KEY`
- Tags: `["economic", "multimodal", "long-context", "openai-compatible"]`
- Models: Gemini-2.0-Flash, Gemini-2.5-Pro, Gemini-2.5-Flash

#### 2.2.2 Free Models Research and Integration
**Priority**: Should Have  
**Effort**: 2 days  
**Dependencies**: 2.1  

**Description**: Research and add OpenRouter free models to expand development and testing options.

**Tasks**:
- [ ] Research comprehensive list of OpenRouter free models (models ending with `:free`)
- [ ] Document model capabilities and characteristics for each free model
- [ ] Add free model configurations to appsettings-example.json
- [ ] Assign appropriate tags including "free" tag for all free models
- [ ] Create capability definitions for free models
- [ ] Validate free model functionality through testing

**Acceptance Criteria**:
- [ ] All major OpenRouter free models are documented and configured
- [ ] Free models have accurate capability definitions
- [ ] Free models are properly tagged for easy selection
- [ ] Configuration examples work with free models
- [ ] Documentation includes free model usage examples

**Free Models Identified**:
- `meta-llama/llama-4-maverick:free` - 400B total, 17B active, multimodal
- `meta-llama/llama-4-scout:free` - 109B total, 17B active, long context
- `google/gemini-2.5-pro-exp-03-25:free` - Large model, 1M context, multimodal
- `deepseek/deepseek-r1:free` - Reasoning model with 163K context
- `qwen/qwen3-30b-a3b:free` - MoE architecture, 40K context
- And 10+ additional free models

**Testing Requirements**:
- [ ] Free model functionality tests
- [ ] Tag-based selection tests for free models
- [ ] Cost tracking verification (should be $0.00)
- [ ] Provider availability tests

#### 2.3 Provider Connection Configuration Enhancement
**Priority**: Must Have  
**Effort**: 3 days  
**Dependencies**: 2.1, 2.2, 2.4  

**Description**: Enhance LMConfig to include provider connection details (endpoint, API key environment variable) for better encapsulation and flexibility.

**Tasks**:
- [ ] Create `ProviderConnectionConfig` record for connection details
- [ ] Add provider connection properties to LMConfig
- [ ] Add provider registry reference properties to LMConfig (preferred provider name, fallback provider names)
- [ ] Implement connection configuration in LMConfigBuilder
- [ ] Update provider selection logic to use connection config from LMConfig or resolve from registry
- [ ] Add validation for provider connection configurations
- [ ] Create provider connection configuration examples
- [ ] Update existing code to support both legacy env var patterns and new config

**Design Details**:
```csharp
public record ProviderConnectionConfig
{
    public required string EndpointUrl { get; init; }
    public required string ApiKeyEnvironmentVariable { get; init; }
    public ProviderCompatibility Compatibility { get; init; } = ProviderCompatibility.OpenAI;
    public Dictionary<string, string>? Headers { get; init; }
    public TimeSpan? Timeout { get; init; }
}

public enum ProviderCompatibility
{
    OpenAI,          // OpenAI API compatible
    Anthropic,       // Anthropic API format
    Custom           // Custom provider format
}
```

**Acceptance Criteria**:
- [ ] LMConfig can specify provider connection details directly or reference by name from registry
- [ ] Provider registry takes precedence over hardcoded connection details
- [ ] Connection config takes precedence over environment variable defaults
- [ ] Backward compatibility maintained with existing environment variable approach
- [ ] Validation prevents invalid connection configurations
- [ ] Multiple provider connections can be configured for fallback scenarios

**Testing Requirements**:
- [ ] Provider connection configuration tests
- [ ] Provider registry integration tests
- [ ] Backward compatibility tests with environment variables
- [ ] Connection validation tests
- [ ] Multi-provider connection tests

#### 2.4 Provider Registry Implementation  
**Priority**: Must Have  
**Effort**: 4 days  
**Dependencies**: None  

**Description**: Implement infrastructure-level provider registry that maps provider names to connection details, allowing centralized configuration of provider endpoints and API keys.

**Tasks**:
- [ ] Create `ProviderRegistryConfig` and `ProviderConnectionInfo` records
- [ ] Implement `IProviderRegistry` interface and `ProviderRegistry` service
- [ ] Add ProviderRegistry section to appsettings.json schema
- [ ] Configure all major providers in appsettings-example.json (OpenAI, Anthropic, OpenRouter, DeepInfra, Groq, Cerebras, GoogleGemini)
- [ ] Add provider registry validation for environment variables
- [ ] Integrate registry with dependency injection
- [ ] Create provider registry configuration examples
- [ ] Add registry-based provider resolution to ModelConfigurationService

**Provider Registry Design**:
```csharp
public record ProviderRegistryConfig
{
    public required Dictionary<string, ProviderConnectionInfo> Providers { get; init; }
}

public record ProviderConnectionInfo
{
    public required string EndpointUrl { get; init; }
    public required string ApiKeyEnvironmentVariable { get; init; }
    public ProviderCompatibility Compatibility { get; init; } = ProviderCompatibility.OpenAI;
    public Dictionary<string, string>? Headers { get; init; }
    public TimeSpan? Timeout { get; init; }
    public int MaxRetries { get; init; } = 3;
    public string? Description { get; init; }
}

public interface IProviderRegistry
{
    ProviderConnectionInfo? GetProviderConnection(string providerName);
    IReadOnlyList<string> GetRegisteredProviders();
    bool IsProviderRegistered(string providerName);
    ValidationResult ValidateEnvironmentVariables();
}
```

**Acceptance Criteria**:
- [ ] Provider registry loads from appsettings.json configuration
- [ ] All major providers are pre-configured with correct endpoints and environment variable names
- [ ] Provider registry validates environment variable availability
- [ ] Registry supports custom headers and timeouts per provider
- [ ] Provider lookup by name works correctly
- [ ] Registry validation provides clear feedback on missing environment variables
- [ ] Registry integrates with dependency injection container

**Testing Requirements**:
- [ ] Provider registry configuration loading tests
- [ ] Provider lookup and resolution tests
- [ ] Environment variable validation tests
- [ ] Provider registry service integration tests
- [ ] Configuration schema validation tests

**Detailed Test Cases**:

```csharp
// Test Case 1: Provider Registry Configuration Loading
[Test]
public void ProviderRegistry_LoadsFromConfiguration_Successfully()
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.test.json")
        .Build();
    
    var registryConfig = configuration.GetSection("ProviderRegistry").Get<ProviderRegistryConfig>();
    
    Assert.IsNotNull(registryConfig);
    Assert.IsTrue(registryConfig.Providers.ContainsKey("OpenAI"));
    Assert.IsTrue(registryConfig.Providers.ContainsKey("OpenRouter"));
    Assert.AreEqual("https://api.openai.com/v1", registryConfig.Providers["OpenAI"].EndpointUrl);
    Assert.AreEqual("OPENAI_API_KEY", registryConfig.Providers["OpenAI"].ApiKeyEnvironmentVariable);
}

// Test Case 2: Provider Lookup and Resolution
[Test]
public void ProviderRegistry_GetProviderConnection_ReturnsCorrectProvider()
{
    var registry = CreateTestProviderRegistry();
    
    var openAIConnection = registry.GetProviderConnection("OpenAI");
    
    Assert.IsNotNull(openAIConnection);
    Assert.AreEqual("https://api.openai.com/v1", openAIConnection.EndpointUrl);
    Assert.AreEqual("OPENAI_API_KEY", openAIConnection.ApiKeyEnvironmentVariable);
    Assert.AreEqual(ProviderCompatibility.OpenAI, openAIConnection.Compatibility);
}

// Test Case 3: Environment Variable Validation
[Test]
public void ProviderRegistry_ValidateEnvironmentVariables_DetectsMissingKeys()
{
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
    Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null); // Missing
    
    var registry = CreateTestProviderRegistry();
    var validation = registry.ValidateEnvironmentVariables();
    
    Assert.IsFalse(validation.IsValid);
    Assert.IsTrue(validation.Warnings.Any(w => w.Contains("OPENROUTER_API_KEY")));
}

// Test Case 4: Provider Registry Service Integration
[Test]
public void ProviderRegistry_IntegratesWithDependencyInjection_Successfully()
{
    var services = new ServiceCollection();
    var configuration = CreateTestConfiguration();
    
    services.Configure<ProviderRegistryConfig>(configuration.GetSection("ProviderRegistry"));
    services.AddSingleton<IProviderRegistry, ProviderRegistry>();
    
    var serviceProvider = services.BuildServiceProvider();
    var registry = serviceProvider.GetRequiredService<IProviderRegistry>();
    
    Assert.IsNotNull(registry);
    Assert.IsTrue(registry.IsProviderRegistered("OpenAI"));
}

// Test Case 5: Free Provider Selection with Tags
[Test]
public async Task ModelConfigurationService_SelectsFreeProviderr_WhenFreeTagRequired()
{
    var config = new LMConfigBuilder()
        .WithModel("deepseek-r1-distill-llama-70b")
        .RequireTags("free")
        .Build();
    
    var selectedProvider = await modelConfigService.SelectProviderForModel(config);
    
    Assert.IsNotNull(selectedProvider);
    Assert.IsTrue(selectedProvider.Tags.Contains("free"));
    Assert.AreEqual(0.0, selectedProvider.Pricing.PromptPerMillion);
    Assert.AreEqual(0.0, selectedProvider.Pricing.CompletionPerMillion);
}

// Test Case 6: Provider Fallback Chain
[Test]
public async Task LMConfigBuilder_WithProviderFallbacks_CreatesCorrectFallbackChain()
{
    var config = new LMConfigBuilder()
        .WithModel("gpt-4")
        .WithProvider("OpenAI", "OpenRouter", "DeepInfra")
        .Build();
    
    Assert.AreEqual("OpenAI", config.PreferredProviderName);
    Assert.IsNotNull(config.FallbackProviderNames);
    Assert.AreEqual(2, config.FallbackProviderNames.Count);
    Assert.Contains("OpenRouter", config.FallbackProviderNames);
    Assert.Contains("DeepInfra", config.FallbackProviderNames);
}

// Test Case 7: Provider Connection Info Validation
[Test]
public void ProviderConnectionInfo_Validate_CatchesInvalidConfiguration()
{
    var invalidConnection = new ProviderConnectionInfo
    {
        EndpointUrl = "invalid-url",  // Invalid URL
        ApiKeyEnvironmentVariable = "", // Empty env var
        MaxRetries = -1 // Invalid retry count
    };
    
    var validation = invalidConnection.Validate();
    
    Assert.IsFalse(validation.IsValid);
    Assert.IsTrue(validation.Errors.Any(e => e.Contains("EndpointUrl")));
    Assert.IsTrue(validation.Errors.Any(e => e.Contains("ApiKeyEnvironmentVariable")));
    Assert.IsTrue(validation.Errors.Any(e => e.Contains("MaxRetries")));
}

// Test Case 8: Provider Health Check
[Test]
public async Task ProviderHealthService_CheckProviderHealth_ReturnsCorrectStatus()
{
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
    Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
    
    var healthService = new ProviderHealthService(CreateTestProviderRegistry());
    var healthResults = await healthService.CheckProviderHealth();
    
    Assert.IsTrue(healthResults["OpenAI"]);    // Has API key
    Assert.IsFalse(healthResults["OpenRouter"]); // Missing API key
}

// Test Case 9: Configuration Schema Validation
[Test]
public void ProviderRegistryConfig_DeserializesFromJson_WithAllProperties()
{
    var json = @"{
        ""ProviderRegistry"": {
            ""OpenAI"": {
                ""EndpointUrl"": ""https://api.openai.com/v1"",
                ""ApiKeyEnvironmentVariable"": ""OPENAI_API_KEY"",
                ""Compatibility"": ""OpenAI"",
                ""Headers"": {
                    ""X-Custom"": ""value""
                },
                ""Timeout"": ""00:01:00"",
                ""MaxRetries"": 3,
                ""Description"": ""OpenAI API""
            }
        }
    }";
    
    var configuration = new ConfigurationBuilder()
        .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
        .Build();
    
    var registryConfig = configuration.GetSection("ProviderRegistry").Get<ProviderRegistryConfig>();
    var openAI = registryConfig.Providers["OpenAI"];
    
    Assert.AreEqual("https://api.openai.com/v1", openAI.EndpointUrl);
    Assert.AreEqual("OPENAI_API_KEY", openAI.ApiKeyEnvironmentVariable);
    Assert.AreEqual(ProviderCompatibility.OpenAI, openAI.Compatibility);
    Assert.IsNotNull(openAI.Headers);
    Assert.AreEqual("value", openAI.Headers["X-Custom"]);
    Assert.AreEqual(TimeSpan.FromMinutes(1), openAI.Timeout);
    Assert.AreEqual(3, openAI.MaxRetries);
    Assert.AreEqual("OpenAI API", openAI.Description);
}

// Test Case 10: Tag-Based Provider Selection Performance
[Test]
public void ModelConfigurationService_TagBasedSelection_PerformsWithinAcceptableTime()
{
    var stopwatch = Stopwatch.StartNew();
    
    for (int i = 0; i < 1000; i++)
    {
        var config = new LMConfigBuilder()
            .WithModel("gpt-4")
            .RequireTags("economic", "fast")
            .PreferTags("reliable")
            .Build();
        
        var provider = modelConfigService.SelectProviderForModel(config);
    }
    
    stopwatch.Stop();
    Assert.Less(stopwatch.ElapsedMilliseconds, 1000); // Should complete in under 1 second
}

// Helper Methods
private IProviderRegistry CreateTestProviderRegistry()
{
    var config = new ProviderRegistryConfig
    {
        Providers = new Dictionary<string, ProviderConnectionInfo>
        {
            ["OpenAI"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://api.openai.com/v1",
                ApiKeyEnvironmentVariable = "OPENAI_API_KEY",
                Compatibility = ProviderCompatibility.OpenAI,
                Timeout = TimeSpan.FromMinutes(1),
                MaxRetries = 3
            },
            ["OpenRouter"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://openrouter.ai/api/v1",
                ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
                Compatibility = ProviderCompatibility.OpenAI,
                Timeout = TimeSpan.FromMinutes(2),
                MaxRetries = 3
            }
        }
    };
    
    var options = Options.Create(config);
    return new ProviderRegistry(options);
}

private IConfiguration CreateTestConfiguration()
{
    var json = @"{
        ""ProviderRegistry"": {
            ""OpenAI"": {
                ""EndpointUrl"": ""https://api.openai.com/v1"",
                ""ApiKeyEnvironmentVariable"": ""OPENAI_API_KEY"",
                ""Compatibility"": ""OpenAI""
            }
        }
    }";
    
    return new ConfigurationBuilder()
        .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
        .Build();
}

#### 2.5 Enhanced DynamicProviderAgent
**Priority**: Must Have  
**Effort**: 4 days  
**Dependencies**: 2.1, 2.2, 2.3  

**Description**: Enhance DynamicProviderAgent to work with LMConfig and capability-aware selection.

**Tasks**:
- [ ] Add LMConfig overload to GenerateReplyAsync
- [ ] Implement capability-aware provider selection
- [ ] Add configuration adaptation logic
- [ ] Integrate provider connection configuration support
- [ ] Maintain backward compatibility with GenerateReplyOptions
- [ ] Add comprehensive logging for provider selection decisions

**Acceptance Criteria**:
- [ ] Both LMConfig and GenerateReplyOptions work seamlessly
- [ ] Provider selection considers capabilities and preferences
- [ ] Configuration adaptation happens transparently
- [ ] Provider connections work with both env vars and config
- [ ] Logging provides clear insight into selection decisions

**Testing Requirements**:
- [ ] Backward compatibility tests
- [ ] Provider selection tests
- [ ] Configuration adaptation tests
- [ ] Connection configuration tests
- [ ] Logging verification tests

#### 2.6 Capability Validation System
**Priority**: Must Have  
**Effort**: 3 days  
**Dependencies**: 2.1  

**Description**: Implement comprehensive validation system for capability compatibility.

**Tasks**:
- [ ] Create `ValidationResult` class with detailed error information
- [ ] Implement capability compatibility checking
- [ ] Add configuration suggestion logic for invalid configurations
- [ ] Create validation error message templates
- [ ] Implement validation caching for performance

**Acceptance Criteria**:
- [ ] Validation catches all capability mismatches
- [ ] Error messages are clear and actionable
- [ ] Suggestions help users fix invalid configurations
- [ ] Validation performance is acceptable for real-time use

**Testing Requirements**:
- [ ] Comprehensive validation scenario tests
- [ ] Error message quality tests
- [ ] Validation performance tests

#### 2.7 Provider-Specific Builder Extensions
**Priority**: Should Have  
**Effort**: 3 days  
**Dependencies**: 1.5, 2.2, 2.3  

**Description**: Add provider-specific configuration methods to LMConfigBuilder.

**Tasks**:
- [ ] Implement `WithOpenRouter()` configuration method
- [ ] Implement `WithAnthropic()` configuration method
- [ ] Implement `WithOpenAI()` configuration method
- [ ] Implement `WithDeepInfra()` configuration method
- [ ] Implement `WithCerebras()` configuration method
- [ ] Implement `WithGroq()` configuration method
- [ ] Implement `WithGoogleGemini()` configuration method
- [ ] Add `WithProviderConnection()` method for connection config
- [ ] Create provider-specific builder classes
- [ ] Add validation for provider-specific configurations

**Acceptance Criteria**:
- [ ] Provider-specific methods are intuitive and well-documented
- [ ] Builder validates provider-specific configurations
- [ ] Provider configs integrate seamlessly with main configuration
- [ ] Connection configuration works with provider-specific methods
- [ ] Error messages guide users to correct configurations
- [ ] OpenAI-compatible providers inherit common OpenAI configuration patterns

**Testing Requirements**:
- [ ] Provider-specific builder tests
- [ ] Configuration validation tests
- [ ] Connection configuration integration tests
- [ ] Integration tests with main builder

---

## Phase 3: Cost Management (2 weeks)

### Objective
Implement comprehensive cost estimation, tracking, and budget management features.

### Work Items

#### 3.1 Cost Estimation System
**Priority**: Must Have  
**Effort**: 3 days  
**Dependencies**: 2.1  

**Description**: Implement cost estimation based on model capabilities and pricing.

**Tasks**:
- [ ] Create `CostEstimation` class with detailed cost breakdown
- [ ] Implement token-based cost calculation
- [ ] Add capability-aware cost estimation (thinking, multimodal)
- [ ] Create cost estimation methods in ModelConfigurationService
- [ ] Add cost estimation caching for performance

**Acceptance Criteria**:
- [ ] Cost estimation is accurate for all supported models
- [ ] Capability usage is factored into cost calculations
- [ ] Estimation performance is suitable for real-time use
- [ ] Cost breakdown provides detailed information

**Testing Requirements**:
- [ ] Cost calculation accuracy tests
- [ ] Capability cost factor tests
- [ ] Performance tests for cost estimation

#### 3.2 Budget Constraint System
**Priority**: Must Have  
**Effort**: 2 days  
**Dependencies**: 3.1  

**Description**: Implement budget constraints and validation.

**Tasks**:
- [ ] Add budget limit properties to LMConfig
- [ ] Implement budget validation before request execution
- [ ] Add budget-aware provider selection
- [ ] Create budget exceeded error handling
- [ ] Add budget tracking and reporting

**Acceptance Criteria**:
- [ ] Budget limits prevent expensive requests
- [ ] Budget validation happens before API calls
- [ ] Budget-aware selection chooses cost-effective providers
- [ ] Budget tracking provides usage insights

**Testing Requirements**:
- [ ] Budget validation tests
- [ ] Budget-aware selection tests
- [ ] Budget tracking accuracy tests

#### 3.3 Cost Tracking and Reporting
**Priority**: Should Have  
**Effort**: 3 days  
**Dependencies**: 3.1  

**Description**: Implement actual cost tracking and historical reporting.

**Tasks**:
- [ ] Create `CostReport` class for actual usage tracking
- [ ] Implement cost tracking in agent execution
- [ ] Add cost history storage and retrieval
- [ ] Create cost analysis and reporting methods
- [ ] Add cost aggregation by provider, model, time period

**Acceptance Criteria**:
- [ ] Actual costs are tracked accurately
- [ ] Cost history provides useful insights
- [ ] Reporting supports various aggregation levels
- [ ] Cost data can be exported for analysis

**Testing Requirements**:
- [ ] Cost tracking accuracy tests
- [ ] Cost history storage tests
- [ ] Reporting functionality tests

#### 3.4 Enhanced LMConfigBuilder with Cost Features
**Priority**: Must Have  
**Effort**: 2 days  
**Dependencies**: 3.1, 3.2  

**Description**: Add cost management methods to LMConfigBuilder.

**Tasks**:
- [ ] Add `WithBudgetLimit()` method
- [ ] Add `WithCostEstimation()` and `WithCostTracking()` methods
- [ ] Implement cost-aware provider selection methods
- [ ] Add cost estimation preview in builder
- [ ] Create cost-related validation in Build() method

**Acceptance Criteria**:
- [ ] Cost management methods are intuitive
- [ ] Builder provides cost estimates during configuration
- [ ] Cost validation prevents budget overruns
- [ ] Cost features integrate seamlessly with other configuration

**Testing Requirements**:
- [ ] Cost management builder tests
- [ ] Cost estimation integration tests
- [ ] Budget validation tests

---

## Phase 4: Advanced Features (3 weeks)

### Objective
Implement advanced features like automatic capability adaptation, cross-model configuration, and performance optimization.

### Work Items

#### 4.1 Automatic Capability Adaptation
**Priority**: Must Have  
**Effort**: 4 days  
**Dependencies**: 2.1, 2.2  

**Description**: Implement automatic adaptation of configurations to model capabilities.

**Tasks**:
- [ ] Create capability adaptation algorithms for each feature type
- [ ] Implement thinking configuration adaptation (Claude vs O1 vs DeepSeek)
- [ ] Add multimodal configuration adaptation
- [ ] Create function calling adaptation logic
- [ ] Add adaptation logging and reporting

**Acceptance Criteria**:
- [ ] Same configuration works across different model types
- [ ] Adaptation preserves user intent while respecting model limitations
- [ ] Adaptation decisions are logged and transparent
- [ ] Fallback behavior is predictable and documented

**Testing Requirements**:
- [ ] Cross-model adaptation tests
- [ ] Adaptation algorithm tests
- [ ] Adaptation logging tests

#### 4.2 Cross-Model Configuration Support
**Priority**: Must Have  
**Effort**: 3 days  
**Dependencies**: 4.1  

**Description**: Enable configurations that work seamlessly across different model types.

**Tasks**:
- [ ] Implement model-agnostic configuration patterns
- [ ] Create configuration templates for common scenarios
- [ ] Add model suggestion logic for unsupported features
- [ ] Implement graceful degradation for missing capabilities
- [ ] Create configuration migration helpers

**Acceptance Criteria**:
- [ ] Configurations are portable across compatible models
- [ ] Missing capabilities degrade gracefully
- [ ] Model suggestions help users find alternatives
- [ ] Configuration migration is seamless

**Testing Requirements**:
- [ ] Cross-model compatibility tests
- [ ] Graceful degradation tests
- [ ] Model suggestion tests

#### 4.3 Performance Optimization
**Priority**: Should Have  
**Effort**: 3 days  
**Dependencies**: 2.1, 3.1  

**Description**: Optimize performance for configuration creation, validation, and provider selection.

**Tasks**:
- [ ] Implement caching for capability lookups
- [ ] Optimize provider selection algorithms
- [ ] Add lazy loading for expensive operations
- [ ] Implement configuration object pooling
- [ ] Add performance monitoring and metrics

**Acceptance Criteria**:
- [ ] Configuration creation is fast enough for real-time use
- [ ] Provider selection completes within acceptable time limits
- [ ] Memory usage is optimized for high-throughput scenarios
- [ ] Performance metrics provide visibility into bottlenecks

**Testing Requirements**:
- [ ] Performance benchmark tests
- [ ] Memory usage tests
- [ ] Caching effectiveness tests

#### 4.4 Advanced Validation and Error Handling
**Priority**: Must Have  
**Effort**: 3 days  
**Dependencies**: 2.5  

**Description**: Implement comprehensive validation and user-friendly error handling.

**Tasks**:
- [ ] Create detailed validation rules for all configuration combinations
- [ ] Implement context-aware error messages
- [ ] Add configuration suggestion engine
- [ ] Create validation rule documentation
- [ ] Implement validation rule testing framework

**Acceptance Criteria**:
- [ ] All invalid configurations are caught before execution
- [ ] Error messages provide clear guidance for fixes
- [ ] Suggestions help users create valid configurations
- [ ] Validation rules are well-documented and testable

**Testing Requirements**:
- [ ] Comprehensive validation tests
- [ ] Error message quality tests
- [ ] Suggestion engine tests

#### 4.5 Configuration Serialization and Templates
**Priority**: Could Have  
**Effort**: 2 days  
**Dependencies**: 1.4  

**Description**: Enable configuration serialization and template system.

**Tasks**:
- [ ] Implement JSON serialization for LMConfig
- [ ] Create configuration template system
- [ ] Add configuration import/export functionality
- [ ] Create predefined configuration templates
- [ ] Add template validation and versioning

**Acceptance Criteria**:
- [ ] Configurations can be serialized and deserialized reliably
- [ ] Templates provide starting points for common scenarios
- [ ] Template system is extensible and maintainable
- [ ] Configuration versioning handles schema evolution

**Testing Requirements**:
- [ ] Serialization round-trip tests
- [ ] Template functionality tests
- [ ] Version compatibility tests

---

## Phase 5: Testing & Documentation (2 weeks)

### Objective
Ensure comprehensive test coverage, create migration documentation, and provide complete API documentation.

### Work Items

#### 5.1 Comprehensive Test Coverage
**Priority**: Must Have  
**Effort**: 4 days  
**Dependencies**: All previous phases  

**Description**: Achieve comprehensive test coverage for all new functionality.

**Tasks**:
- [ ] Create integration tests for end-to-end scenarios
- [ ] Add performance tests for critical paths
- [ ] Implement stress tests for high-load scenarios
- [ ] Create compatibility tests with existing code
- [ ] Add regression tests for bug fixes

**Acceptance Criteria**:
- [ ] Test coverage is >90% for all new code
- [ ] Integration tests cover all major usage scenarios
- [ ] Performance tests validate acceptable response times
- [ ] Compatibility tests ensure backward compatibility

**Testing Requirements**:
- [ ] Unit test coverage reports
- [ ] Integration test scenarios
- [ ] Performance benchmark results

#### 5.2 Migration Documentation
**Priority**: Must Have  
**Effort**: 2 days  
**Dependencies**: All implementation phases  

**Description**: Create comprehensive migration guides for existing code.

**Tasks**:
- [ ] Create step-by-step migration guide from GenerateReplyOptions
- [ ] Document breaking changes and mitigation strategies
- [ ] Create code examples for common migration scenarios
- [ ] Add troubleshooting guide for migration issues
- [ ] Create automated migration tools where possible

**Acceptance Criteria**:
- [ ] Migration guide covers all common scenarios
- [ ] Examples are tested and working
- [ ] Troubleshooting guide addresses common issues
- [ ] Migration tools reduce manual effort

**Testing Requirements**:
- [ ] Migration example validation
- [ ] Migration tool testing

#### 5.3 API Documentation
**Priority**: Must Have  
**Effort**: 2 days  
**Dependencies**: All implementation phases  

**Description**: Create complete API documentation with examples.

**Tasks**:
- [ ] Generate API documentation from code comments
- [ ] Create usage examples for all major features
- [ ] Document configuration patterns and best practices
- [ ] Add troubleshooting and FAQ sections
- [ ] Create interactive documentation with code samples

**Acceptance Criteria**:
- [ ] All public APIs are documented with examples
- [ ] Documentation is accurate and up-to-date
- [ ] Examples are tested and working
- [ ] Documentation is easily discoverable and navigable

**Testing Requirements**:
- [ ] Documentation example validation
- [ ] Documentation completeness checks

#### 5.4 Performance Benchmarking
**Priority**: Should Have  
**Effort**: 2 days  
**Dependencies**: All implementation phases  

**Description**: Establish performance benchmarks and monitoring.

**Tasks**:
- [ ] Create performance benchmark suite
- [ ] Establish baseline performance metrics
- [ ] Add performance regression testing
- [ ] Create performance monitoring dashboard
- [ ] Document performance characteristics and limits

**Acceptance Criteria**:
- [ ] Performance benchmarks cover all critical paths
- [ ] Baseline metrics are established and documented
- [ ] Regression testing catches performance degradation
- [ ] Monitoring provides visibility into production performance

**Testing Requirements**:
- [ ] Benchmark accuracy validation
- [ ] Performance monitoring tests

---

## Dependencies and Critical Path

### Phase Dependencies
```
Phase 1 (Foundation) → Phase 2 (Provider Integration) → Phase 4 (Advanced Features)
                   ↘ Phase 3 (Cost Management) ↗
                                ↓
                        Phase 5 (Testing & Docs)
```

### Critical Path Items
1. **Model Capabilities System** (1.1) - Foundation for everything else
2. **Enhanced ModelConfigurationService** (2.1) - Core integration point
3. **Enhanced DynamicProviderAgent** (2.3) - Main execution path
4. **Automatic Capability Adaptation** (4.1) - Key differentiator

### Risk Mitigation Strategies

#### High-Risk Items
- **Model Capabilities System**: Complex domain modeling
  - *Mitigation*: Start with simple capabilities, iterate based on feedback
- **Provider Integration**: Integration with existing complex systems
  - *Mitigation*: Maintain backward compatibility, extensive testing
- **Performance**: New system may be slower than existing
  - *Mitigation*: Performance testing from day 1, optimization in Phase 4

#### Medium-Risk Items
- **Cost Management**: Accuracy of cost calculations
  - *Mitigation*: Validate against known pricing, add safety margins
- **Cross-Model Support**: Complexity of adaptation algorithms
  - *Mitigation*: Start with simple adaptations, add complexity gradually

## Definition of Done

### For Each Work Item
- [ ] Code is implemented and reviewed
- [ ] Unit tests are written and passing
- [ ] Integration tests cover the feature
- [ ] Documentation is updated
- [ ] Performance impact is measured
- [ ] Backward compatibility is verified

### For Each Phase
- [ ] All work items are complete
- [ ] End-to-end testing is successful
- [ ] Performance benchmarks are met
- [ ] Documentation is complete and accurate
- [ ] Migration path is validated

### For Overall Project
- [ ] All phases are complete
- [ ] System passes comprehensive testing
- [ ] Performance meets or exceeds existing system
- [ ] Migration documentation is complete
- [ ] Production deployment is successful

## Success Metrics

### Technical Metrics
- **Test Coverage**: >90% for all new code
- **Performance**: Configuration creation <10ms, Provider selection <50ms
- **Reliability**: Zero breaking changes to existing APIs
- **Maintainability**: Code complexity metrics within acceptable ranges

### Business Metrics
- **Developer Experience**: Reduced configuration code by 50%
- **Cost Optimization**: 20% reduction in LLM costs through better provider selection
- **Feature Adoption**: 80% of new integrations use LMConfig within 6 months
- **Support Reduction**: 30% reduction in configuration-related support tickets

---

## Conclusion

This implementation plan provides a structured approach to delivering the unified LMConfig system while maintaining production stability and backward compatibility. The phased approach allows for incremental delivery of value while building toward the complete vision.

**Key Success Factors**:
1. Maintain backward compatibility throughout implementation
2. Comprehensive testing at each phase
3. Clear documentation and migration guides
4. Performance monitoring and optimization
5. Regular stakeholder feedback and iteration

**Next Steps**:
1. Review and approve this implementation plan
2. Set up development environment and tooling
3. Begin Phase 1 implementation
4. Establish regular review and feedback cycles 