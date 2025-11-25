using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;
using MemoryServer.Utils;

namespace MemoryServer.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryServerCore(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Add memory cache
        _ = services.AddMemoryCache();

        // Configure options from appsettings
        _ = services.Configure<DatabaseOptions>(configuration.GetSection("MemoryServer:Database"));
        _ = services.Configure<MemoryServerOptions>(configuration.GetSection("MemoryServer"));
        _ = services.Configure<DocumentSegmentationOptions>(
            configuration.GetSection("MemoryServer:DocumentSegmentation")
        );

        // Register Database Session Pattern infrastructure
        _ = services.AddDatabaseServices(environment);

        // Register core infrastructure
        _ = services.AddSingleton<MemoryIdGenerator>();

        // Register session management services
        _ = services.AddScoped<ISessionContextResolver, SessionContextResolver>();
        _ = services.AddScoped<ISessionManager, SessionManager>();
        _ = services.AddScoped<TransportSessionInitializer>();

        // Register memory services
        _ = services.AddScoped<IMemoryRepository, MemoryRepository>();
        _ = services.AddScoped<IMemoryService, MemoryService>();

        // Register embedding services for vector storage
        _ = services.AddScoped<IEmbeddingManager, EmbeddingManager>();

        // Register graph database services
        _ = services.AddScoped<IGraphRepository, GraphRepository>();
        _ = services.AddScoped<IGraphExtractionService, GraphExtractionService>();
        _ = services.AddScoped<IGraphDecisionEngine, GraphDecisionEngine>();
        _ = services.AddScoped<IGraphMemoryService, GraphMemoryService>();

        // Register unified search engine for Phase 6
        _ = services.AddScoped<IUnifiedSearchEngine, UnifiedSearchEngine>();

        // Register reranking engine for Phase 7
        _ = services.AddScoped<IRerankingEngine, RerankingEngine>();

        // Register deduplication engine for Phase 8
        _ = services.AddScoped<IDeduplicationEngine, DeduplicationEngine>();

        // Register result enrichment engine for Phase 8
        _ = services.AddScoped<IResultEnricher, ResultEnricher>();

        // Register LLM services
        _ = services.AddLlmServices();

        // Register LmConfig integration
        _ = services.AddScoped<ILmConfigService, LmConfigService>();

        // Register Document Segmentation services (Phase 1)
        _ = services.AddDocumentSegmentationServices();

        // Register MCP tools
        _ = services.AddScoped<MemoryMcpTools>();

        return services;
    }

    public static IServiceCollection AddDocumentSegmentationServices(this IServiceCollection services)
    {
        // Document Segmentation services - Phase 1 implementation

        // Core segmentation service interface - implemented in Week 3
        _ = services.AddScoped<IDocumentSegmentationService, DocumentSegmentationService>();

        // Specialized segmentation services
        _ = services.AddScoped<ITopicBasedSegmentationService, TopicBasedSegmentationService>();
        _ = services.AddScoped<IStructureBasedSegmentationService, StructureBasedSegmentationService>();
        _ = services.AddScoped<INarrativeBasedSegmentationService, NarrativeBasedSegmentationService>();
        _ = services.AddScoped<IHybridSegmentationService, HybridSegmentationService>();

        // LLM integration services
        _ = services.AddScoped<ILlmProviderIntegrationService, LlmProviderIntegrationService>();

        // Quality assessment services
        _ = services.AddScoped<ISegmentationQualityAssessmentService, SegmentationQualityAssessmentService>();

        // Prompt management service - implemented in Week 2
        _ = services.AddScoped<ISegmentationPromptManager, SegmentationPromptManager>();

        // Repository for segment storage - implemented in Week 2
        _ = services.AddScoped<IDocumentSegmentRepository, DocumentSegmentRepository>();

        // Document size detection service - implemented in Week 2
        _ = services.AddScoped<IDocumentSizeAnalyzer, DocumentSizeAnalyzer>();

        // Error handling and resilience services - Phase 2 error handling
        _ = services.AddErrorHandlingServices();

        // Session context integration is built into all services through dependency injection
        // All services accept SessionContext parameter and use Database Session Pattern

        return services;
    }

    public static IServiceCollection AddErrorHandlingServices(this IServiceCollection services)
    {
        // Register resilience configurations with default values
        _ = services.AddSingleton<CircuitBreakerConfiguration>(_ => new CircuitBreakerConfiguration
        {
            FailureThreshold = 5,
            TimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            ExponentialFactor = 2.0,
        });

        _ = services.AddSingleton<RetryConfiguration>(_ => new RetryConfiguration
        {
            MaxRetries = 3,
            BaseDelayMs = 1000,
            ExponentialFactor = 2.0,
            MaxDelayMs = 30000,
            JitterPercent = 0.1,
        });

        _ = services.AddSingleton<GracefulDegradationConfiguration>(_ => new GracefulDegradationConfiguration
        {
            FallbackTimeoutMs = 5000,
            RuleBasedQualityScore = 0.7,
            RuleBasedMaxProcessingMs = 10000,
            MaxPerformanceDegradationPercent = 0.2,
        });

        // Register resilience services
        _ = services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();
        _ = services.AddSingleton<IRetryPolicyService, RetryPolicyService>();
        _ = services.AddScoped<IResilienceService, ResilienceService>();

        return services;
    }

    public static IServiceCollection AddDatabaseServices(
        this IServiceCollection services,
        IHostEnvironment? environment = null
    )
    {
        // For MCP server, always use production database services
        // Test services should only be used in actual test projects
        _ = services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();

        return services;
    }

    public static IServiceCollection AddLlmServices(this IServiceCollection services)
    {
        // Register prompt reader that loads from embedded resources with file system fallback
        _ = services.AddScoped<IPromptReader, EmbeddedPromptReader>();

        // Register LmConfig with embedded resource configuration
        // This will properly register UnifiedAgent, ModelResolver, ProviderAgentFactory, and AppConfig
        _ = services.AddLmConfigFromEmbeddedResource("models.json");

        // Register LmConfig integration
        _ = services.AddScoped<ILmConfigService, LmConfigService>();

        // Add HTTP client factory for provider connections
        _ = services.AddHttpClient();

        // Note: IAgent is now provided through ILmConfigService.CreateAgentAsync()
        // instead of direct dependency injection for better model selection and provider management

        return services;
    }

    // Note: Agent creation methods removed as they are now handled by ILmConfigService
    // which provides better model selection, provider management, and configuration

    public static IServiceCollection AddMcpServices(this IServiceCollection services, TransportMode transportMode)
    {
        var mcpBuilder = services.AddMcpServer();

        _ = transportMode == TransportMode.SSE ? mcpBuilder.WithHttpTransport() : mcpBuilder.WithStdioServerTransport();

        _ = mcpBuilder.WithToolsFromAssembly(typeof(MemoryMcpTools).Assembly);

        return services;
    }

    public static IServiceCollection AddTestLlmServices(this IServiceCollection services)
    {
        // Register prompt reader that loads from embedded resources with file system fallback
        _ = services.AddScoped<IPromptReader, EmbeddedPromptReader>();

        // Use mock agent for testing
        _ = services.AddScoped<IAgent>(provider => new MockAgent("test-agent"));

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
