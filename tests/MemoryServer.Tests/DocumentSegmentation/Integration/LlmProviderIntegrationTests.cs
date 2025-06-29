using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Moq;

namespace MemoryServer.DocumentSegmentation.Tests.Integration;

/// <summary>
/// Integration tests for LLM provider connectivity and authentication.
/// Tests the LlmProviderIntegrationService with real and mocked LLM providers.
/// </summary>
public class LlmProviderIntegrationTests : IDisposable
{
  private readonly ServiceProvider _serviceProvider;
  private readonly Mock<IProviderAgentFactory> _mockAgentFactory;
  private readonly Mock<IModelResolver> _mockModelResolver;
  private readonly Mock<ISegmentationPromptManager> _mockPromptManager;
  private readonly Mock<IDocumentAnalysisService> _mockAnalysisService;
  private readonly Mock<IAgent> _mockAgent;
  private readonly ILogger<LlmProviderIntegrationService> _logger;

  public LlmProviderIntegrationTests()
  {
    // Set up service collection for DI
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

    // Create mocks
    _mockAgentFactory = new Mock<IProviderAgentFactory>();
    _mockModelResolver = new Mock<IModelResolver>();
    _mockPromptManager = new Mock<ISegmentationPromptManager>();
    _mockAnalysisService = new Mock<IDocumentAnalysisService>();
    _mockAgent = new Mock<IAgent>();

    // Register mocked services
    services.AddSingleton(_mockAgentFactory.Object);
    services.AddSingleton(_mockModelResolver.Object);
    services.AddSingleton(_mockPromptManager.Object);
    services.AddSingleton(_mockAnalysisService.Object);

    // Build service provider
    _serviceProvider = services.BuildServiceProvider();
    _logger = _serviceProvider.GetRequiredService<ILogger<LlmProviderIntegrationService>>();

    // Set up default mock behaviors
    SetupDefaultMockBehaviors();
  }

  private void SetupDefaultMockBehaviors()
  {
    // Mock provider resolution
    var providerResolution = new ProviderResolution
    {
      Model = new ModelConfig
      {
        Id = "test-model",
        IsReasoning = false,
        Providers = new List<ProviderConfig>()
      },
      Provider = new ProviderConfig
      {
        Name = "TestProvider",
        ModelName = "test-model",
        Priority = 1,
        Pricing = new PricingConfig
        {
          PromptPerMillion = 1.0,
          CompletionPerMillion = 2.0
        }
      },
      Connection = new ProviderConnectionInfo
      {
        EndpointUrl = "https://api.test.com",
        ApiKeyEnvironmentVariable = "TEST_API_KEY"
      }
    };

    _mockModelResolver.Setup(x => x.ResolveProviderAsync(
        It.IsAny<string>(),
        It.IsAny<ProviderSelectionCriteria>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(providerResolution);

    _mockAgentFactory.Setup(x => x.CreateAgent(It.IsAny<ProviderResolution>()))
      .Returns(_mockAgent.Object);

    // Mock successful LLM response
    var testResponse = new TextMessage
    {
      Text = "{\"test\": \"response\"}",
      Role = Role.Assistant
    };

    _mockAgent.Setup(x => x.GenerateReplyAsync(
        It.IsAny<IReadOnlyList<IMessage>>(),
        It.IsAny<GenerateReplyOptions>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(new List<IMessage> { testResponse });

    // Mock prompt manager
    var testPrompt = new PromptTemplate
    {
      SystemPrompt = "Test system prompt",
      UserPrompt = "Test user prompt: {DocumentContent}",
      ExpectedFormat = "json",
      MaxTokens = 1000,
      Temperature = 0.1
    };

    _mockPromptManager.Setup(x => x.GetPromptAsync(
        It.IsAny<SegmentationStrategy>(),
        It.IsAny<string>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(testPrompt);

    // Mock document analysis service
    var testAnalysis = new StrategyRecommendation
    {
      Strategy = SegmentationStrategy.TopicBased,
      Confidence = 0.8,
      Reasoning = "Test reasoning",
      Alternatives = new List<SegmentationStrategy> { SegmentationStrategy.Hybrid }
    };

    _mockAnalysisService.Setup(x => x.AnalyzeOptimalStrategyAsync(
        It.IsAny<string>(),
        It.IsAny<DocumentType>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(testAnalysis);
  }

  [Fact]
  public void LlmProviderIntegrationService_Construction_Succeeds()
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4",
        ["segmentation"] = "gpt-3.5-turbo"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    // Act
    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Assert
    service.Should().NotBeNull();
  }

  [Fact]
  public async Task TestConnectivityAsync_WithValidProvider_ReturnsTrue()
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Act
    var result = await service.TestConnectivityAsync();

    // Assert
    result.Should().BeTrue();
    
    // Verify that the model resolver was called
    _mockModelResolver.Verify(x => x.ResolveProviderAsync(
      "gpt-4",
      It.IsAny<ProviderSelectionCriteria>(),
      It.IsAny<CancellationToken>()), Times.Once);

    // Verify that agent factory was called
    _mockAgentFactory.Verify(x => x.CreateAgent(It.IsAny<ProviderResolution>()), Times.Once);

    // Verify that agent was called with correct parameters
    _mockAgent.Verify(x => x.GenerateReplyAsync(
      It.Is<IReadOnlyList<IMessage>>(messages => 
        messages.Count == 1 && 
        messages[0].Role == Role.User),
      It.IsAny<GenerateReplyOptions>(),
      It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task TestConnectivityAsync_WithProviderResolutionFailure_ReturnsFalse()
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "invalid-model"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    // Mock null resolution (provider not found)
    _mockModelResolver.Setup(x => x.ResolveProviderAsync(
        "invalid-model",
        It.IsAny<ProviderSelectionCriteria>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync((ProviderResolution?)null);

    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Act
    var result = await service.TestConnectivityAsync();

    // Assert
    result.Should().BeFalse();
    
    // Verify that agent factory was not called
    _mockAgentFactory.Verify(x => x.CreateAgent(It.IsAny<ProviderResolution>()), Times.Never);
  }

  [Fact]
  public async Task TestConnectivityAsync_WithAgentException_ReturnsFalse()
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    // Mock agent throwing exception
    _mockAgent.Setup(x => x.GenerateReplyAsync(
        It.IsAny<IReadOnlyList<IMessage>>(),
        It.IsAny<GenerateReplyOptions>(),
        It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidOperationException("Test connection failure"));

    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Act
    var result = await service.TestConnectivityAsync();

    // Assert
    result.Should().BeFalse();
  }

  [Fact]
  public async Task AnalyzeOptimalStrategyAsync_WithHighConfidenceAnalysis_ReturnsAnalysisResult()
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    var highConfidenceAnalysis = new StrategyRecommendation
    {
      Strategy = SegmentationStrategy.StructureBased,
      Confidence = 0.9, // High confidence, should not trigger LLM enhancement
      Reasoning = "Document has clear structural elements",
      Alternatives = new List<SegmentationStrategy> { SegmentationStrategy.Hybrid }
    };

    _mockAnalysisService.Setup(x => x.AnalyzeOptimalStrategyAsync(
        It.IsAny<string>(),
        It.IsAny<DocumentType>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(highConfidenceAnalysis);

    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Act
    var result = await service.AnalyzeOptimalStrategyAsync(
      "Test document content",
      DocumentType.ResearchPaper);

    // Assert
    result.Should().NotBeNull();
    result.Strategy.Should().Be(SegmentationStrategy.StructureBased);
    result.Confidence.Should().Be(0.9);
    result.Reasoning.Should().Be("Document has clear structural elements");

    // Verify that LLM was not called (high confidence analysis)
    _mockAgent.Verify(x => x.GenerateReplyAsync(
      It.IsAny<IReadOnlyList<IMessage>>(),
      It.IsAny<GenerateReplyOptions>(),
      It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task AnalyzeOptimalStrategyAsync_WithLowConfidenceAnalysis_PerformsLlmEnhancement()
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    var lowConfidenceAnalysis = new StrategyRecommendation
    {
      Strategy = SegmentationStrategy.TopicBased,
      Confidence = 0.5, // Low confidence, should trigger LLM enhancement
      Reasoning = "Uncertain about optimal strategy",
      Alternatives = new List<SegmentationStrategy> { SegmentationStrategy.Hybrid }
    };

    _mockAnalysisService.Setup(x => x.AnalyzeOptimalStrategyAsync(
        It.IsAny<string>(),
        It.IsAny<DocumentType>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(lowConfidenceAnalysis);

    // Mock LLM response for strategy enhancement with valid JSON
    var llmResponse = new TextMessage
    {
      Text = @"{
        ""recommended_strategy"": ""TopicBased"",
        ""confidence"": 0.8,
        ""reasoning"": ""Enhanced with LLM analysis: Document shows clear topic boundaries and thematic coherence""
      }",
      Role = Role.Assistant
    };

    _mockAgent.Setup(x => x.GenerateReplyAsync(
        It.IsAny<IReadOnlyList<IMessage>>(),
        It.IsAny<GenerateReplyOptions>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(new List<IMessage> { llmResponse });

    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Act
    var result = await service.AnalyzeOptimalStrategyAsync(
      "Test document content",
      DocumentType.Technical);

    // Assert
    result.Should().NotBeNull();
    result.Strategy.Should().Be(SegmentationStrategy.TopicBased);
    result.Confidence.Should().Be(0.8); // Should match the LLM response
    result.Reasoning.Should().Contain("Enhanced with LLM analysis");

    // Verify that LLM was called for enhancement
    _mockAgent.Verify(x => x.GenerateReplyAsync(
      It.IsAny<IReadOnlyList<IMessage>>(),
      It.IsAny<GenerateReplyOptions>(),
      It.IsAny<CancellationToken>()), Times.Once);

    // Verify that prompt manager was called
    _mockPromptManager.Verify(x => x.GetPromptAsync(
      SegmentationStrategy.Hybrid, // Default strategy for enhancement prompts
      "en",
      It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task AnalyzeOptimalStrategyAsync_WithAnalysisServiceException_ReturnsDefault()
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    _mockAnalysisService.Setup(x => x.AnalyzeOptimalStrategyAsync(
        It.IsAny<string>(),
        It.IsAny<DocumentType>(),
        It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidOperationException("Analysis service failure"));

    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Act
    var result = await service.AnalyzeOptimalStrategyAsync(
      "Test document content",
      DocumentType.Email);

    // Assert
    result.Should().NotBeNull();
    result.Strategy.Should().Be(SegmentationStrategy.TopicBased); // Default for Email
    result.Confidence.Should().Be(0.6);
    result.Reasoning.Should().Be("Default strategy for Email documents");
  }

  [Theory]
  [InlineData(DocumentType.ResearchPaper, SegmentationStrategy.StructureBased)]
  [InlineData(DocumentType.Legal, SegmentationStrategy.StructureBased)]
  [InlineData(DocumentType.Technical, SegmentationStrategy.Hybrid)]
  [InlineData(DocumentType.Email, SegmentationStrategy.TopicBased)]
  [InlineData(DocumentType.Chat, SegmentationStrategy.TopicBased)]
  [InlineData(DocumentType.Generic, SegmentationStrategy.Hybrid)]
  public async Task AnalyzeOptimalStrategyAsync_DefaultStrategyRecommendations_AreCorrect(
    DocumentType documentType, 
    SegmentationStrategy expectedStrategy)
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    // Mock analysis service failure to force default strategy
    _mockAnalysisService.Setup(x => x.AnalyzeOptimalStrategyAsync(
        It.IsAny<string>(),
        It.IsAny<DocumentType>(),
        It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidOperationException("Force default strategy"));

    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Act
    var result = await service.AnalyzeOptimalStrategyAsync(
      "Test document content",
      documentType);

    // Assert
    result.Should().NotBeNull();
    result.Strategy.Should().Be(expectedStrategy);
    result.Confidence.Should().Be(0.6);
    result.Reasoning.Should().Be($"Default strategy for {documentType} documents");
  }

  [Fact]
  public async Task Multiple_Concurrent_Connectivity_Tests_Should_Succeed()
  {
    // Arrange
    var configuration = new LlmProviderConfiguration
    {
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4"
      },
      MaxRetries = 3,
      TimeoutSeconds = 30
    };

    var service = new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      configuration);

    // Act - Run multiple connectivity tests concurrently
    var tasks = Enumerable.Range(0, 5)
      .Select(_ => service.TestConnectivityAsync())
      .ToArray();

    var results = await Task.WhenAll(tasks);

    // Assert
    results.Should().AllSatisfy(x => x.Should().BeTrue());
    
    // Verify that the model resolver was called multiple times
    _mockModelResolver.Verify(x => x.ResolveProviderAsync(
      It.IsAny<string>(),
      It.IsAny<AchieveAi.LmDotnetTools.LmConfig.Models.ProviderSelectionCriteria>(),
      It.IsAny<CancellationToken>()), Times.Exactly(5));
  }

  [Fact]
  public void LlmProviderIntegrationService_WithNullConfiguration_ThrowsArgumentNullException()
  {
    // Act & Assert
    var act = () => new LlmProviderIntegrationService(
      _mockAgentFactory.Object,
      _mockModelResolver.Object,
      _mockPromptManager.Object,
      _mockAnalysisService.Object,
      _logger,
      null!);

    act.Should().Throw<ArgumentNullException>();
  }

  [Fact]
  public void LlmProviderIntegrationService_WithNullDependencies_ThrowsArgumentNullException()
  {
    var configuration = new LlmProviderConfiguration();

    // Test each null dependency
    var act1 = () => new LlmProviderIntegrationService(
      null!, _mockModelResolver.Object, _mockPromptManager.Object, 
      _mockAnalysisService.Object, _logger, configuration);
    
    var act2 = () => new LlmProviderIntegrationService(
      _mockAgentFactory.Object, null!, _mockPromptManager.Object, 
      _mockAnalysisService.Object, _logger, configuration);
    
    var act3 = () => new LlmProviderIntegrationService(
      _mockAgentFactory.Object, _mockModelResolver.Object, null!, 
      _mockAnalysisService.Object, _logger, configuration);
    
    var act4 = () => new LlmProviderIntegrationService(
      _mockAgentFactory.Object, _mockModelResolver.Object, _mockPromptManager.Object, 
      null!, _logger, configuration);
    
    var act5 = () => new LlmProviderIntegrationService(
      _mockAgentFactory.Object, _mockModelResolver.Object, _mockPromptManager.Object, 
      _mockAnalysisService.Object, null!, configuration);

    // Assert
    act1.Should().Throw<ArgumentNullException>();
    act2.Should().Throw<ArgumentNullException>();
    act3.Should().Throw<ArgumentNullException>();
    act4.Should().Throw<ArgumentNullException>();
    act5.Should().Throw<ArgumentNullException>();
  }

  public void Dispose()
  {
    _serviceProvider?.Dispose();
  }
}
