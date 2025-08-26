using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;
using MemoryServer.Utils;
using Microsoft.Extensions.Options;

namespace MemoryServer.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryServerCore(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null
    )
    {
        // Add memory cache
        services.AddMemoryCache();

        // Configure options from appsettings
        services.Configure<DatabaseOptions>(configuration.GetSection("MemoryServer:Database"));
        services.Configure<MemoryServerOptions>(configuration.GetSection("MemoryServer"));
        services.Configure<DocumentSegmentationOptions>(
            configuration.GetSection("MemoryServer:DocumentSegmentation")
        );

        // Register Database Session Pattern infrastructure
        services.AddDatabaseServices(environment);

        // Register core infrastructure
        services.AddSingleton<MemoryIdGenerator>();

        // Register session management services
        services.AddScoped<ISessionContextResolver, SessionContextResolver>();
        services.AddScoped<ISessionManager, SessionManager>();
        services.AddScoped<TransportSessionInitializer>();

        // Register memory services
        services.AddScoped<IMemoryRepository, MemoryRepository>();
        services.AddScoped<IMemoryService, MemoryService>();

        // Register embedding services for vector storage
        services.AddScoped<IEmbeddingManager, EmbeddingManager>();

        // Register graph database services
        services.AddScoped<IGraphRepository, GraphRepository>();
        services.AddScoped<IGraphExtractionService, GraphExtractionService>();
        services.AddScoped<IGraphDecisionEngine, GraphDecisionEngine>();
        services.AddScoped<IGraphMemoryService, GraphMemoryService>();

        // Register unified search engine for Phase 6
        services.AddScoped<IUnifiedSearchEngine, UnifiedSearchEngine>();

        // Register reranking engine for Phase 7
        services.AddScoped<IRerankingEngine, RerankingEngine>();

        // Register deduplication engine for Phase 8
        services.AddScoped<IDeduplicationEngine, DeduplicationEngine>();

        // Register result enrichment engine for Phase 8
        services.AddScoped<IResultEnricher, ResultEnricher>();

        // Register LLM services
        services.AddLlmServices();

        // Register LmConfig integration
        services.AddScoped<ILmConfigService, LmConfigService>();

        // Register Document Segmentation services (Phase 1)
        services.AddDocumentSegmentationServices();

        // Register MCP tools
        services.AddScoped<MemoryMcpTools>();

        return services;
    }

    public static IServiceCollection AddDocumentSegmentationServices(
        this IServiceCollection services
    )
    {
        // Document Segmentation services - Phase 1 implementation

        // Core segmentation service interface - implemented in Week 3
        services.AddScoped<IDocumentSegmentationService, DocumentSegmentationService>();

        // Specialized segmentation services
        services.AddScoped<ITopicBasedSegmentationService, TopicBasedSegmentationService>();
        services.AddScoped<IStructureBasedSegmentationService, StructureBasedSegmentationService>();
        services.AddScoped<INarrativeBasedSegmentationService, NarrativeBasedSegmentationService>();
        services.AddScoped<IHybridSegmentationService, HybridSegmentationService>();

        // LLM integration services
        services.AddScoped<ILlmProviderIntegrationService, LlmProviderIntegrationService>();

        // Quality assessment services
        services.AddScoped<
            ISegmentationQualityAssessmentService,
            SegmentationQualityAssessmentService
        >();

        // Prompt management service - implemented in Week 2
        services.AddScoped<ISegmentationPromptManager, SegmentationPromptManager>();

        // Repository for segment storage - implemented in Week 2
        services.AddScoped<IDocumentSegmentRepository, DocumentSegmentRepository>();

        // Document size detection service - implemented in Week 2
        services.AddScoped<IDocumentSizeAnalyzer, DocumentSizeAnalyzer>();

        // Error handling and resilience services - Phase 2 error handling
        services.AddErrorHandlingServices();

        // Session context integration is built into all services through dependency injection
        // All services accept SessionContext parameter and use Database Session Pattern

        return services;
    }

    public static IServiceCollection AddErrorHandlingServices(this IServiceCollection services)
    {
        // Register resilience configurations with default values
        services.AddSingleton<CircuitBreakerConfiguration>(_ => new CircuitBreakerConfiguration
        {
            FailureThreshold = 5,
            TimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            ExponentialFactor = 2.0,
        });

        services.AddSingleton<RetryConfiguration>(_ => new RetryConfiguration
        {
            MaxRetries = 3,
            BaseDelayMs = 1000,
            ExponentialFactor = 2.0,
            MaxDelayMs = 30000,
            JitterPercent = 0.1,
        });

        services.AddSingleton<GracefulDegradationConfiguration>(
            _ => new GracefulDegradationConfiguration
            {
                FallbackTimeoutMs = 5000,
                RuleBasedQualityScore = 0.7,
                RuleBasedMaxProcessingMs = 10000,
                MaxPerformanceDegradationPercent = 0.2,
            }
        );

        // Register resilience services
        services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();
        services.AddSingleton<IRetryPolicyService, RetryPolicyService>();
        services.AddScoped<IResilienceService, ResilienceService>();

        return services;
    }

    public static IServiceCollection AddDatabaseServices(
        this IServiceCollection services,
        IHostEnvironment? environment = null
    )
    {
        // For MCP server, always use production database services
        // Test services should only be used in actual test projects
        services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();

        return services;
    }

    public static IServiceCollection AddLlmServices(this IServiceCollection services)
    {
        // Register prompt reader that loads from embedded resources with file system fallback
        services.AddScoped<IPromptReader, EmbeddedPromptReader>();

        // Register LmConfig with embedded resource configuration
        // This will properly register UnifiedAgent, ModelResolver, ProviderAgentFactory, and AppConfig
        services.AddLmConfigFromEmbeddedResource("models.json");

        // Register LmConfig integration
        services.AddScoped<ILmConfigService, LmConfigService>();

        // Add HTTP client factory for provider connections
        services.AddHttpClient();

        // Note: IAgent is now provided through ILmConfigService.CreateAgentAsync()
        // instead of direct dependency injection for better model selection and provider management

        return services;
    }

    // Note: Agent creation methods removed as they are now handled by ILmConfigService
    // which provides better model selection, provider management, and configuration

    public static IServiceCollection AddMcpServices(
        this IServiceCollection services,
        TransportMode transportMode
    )
    {
        var mcpBuilder = services.AddMcpServer();

        if (transportMode == TransportMode.SSE)
        {
            mcpBuilder.WithHttpTransport();
        }
        else
        {
            mcpBuilder.WithStdioServerTransport();
        }

        mcpBuilder.WithToolsFromAssembly(typeof(MemoryMcpTools).Assembly);

        return services;
    }

    public static IServiceCollection AddTestLlmServices(this IServiceCollection services)
    {
        // Register prompt reader that loads from embedded resources with file system fallback
        services.AddScoped<IPromptReader, EmbeddedPromptReader>();

        // Use mock agent for testing
        services.AddScoped<IAgent>(provider => new MockAgent("test-agent"));

        return services;
    }

    public static void InitializeDatabaseSync(this IServiceProvider services)
    {
        var sessionFactory = services.GetRequiredService<ISqliteSessionFactory>();

        // For testing scenarios, we need to initialize synchronously
        // Use a dedicated thread to avoid deadlock issues
        var initTask = Task.Run(async () => await sessionFactory.InitializeDatabaseAsync());
        initTask.Wait();
    }
}
