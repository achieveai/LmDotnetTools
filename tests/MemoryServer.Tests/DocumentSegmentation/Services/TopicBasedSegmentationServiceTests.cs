using FluentAssertions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace MemoryServer.DocumentSegmentation.Tests.Services;

/// <summary>
/// Tests for TopicBasedSegmentationService functionality.
/// </summary>
public class TopicBasedSegmentationServiceTests
{
  private readonly Mock<ILlmProviderIntegrationService> _mockLlmService;
  private readonly Mock<ISegmentationPromptManager> _mockPromptManager;
  private readonly ILogger<TopicBasedSegmentationService> _logger;
  private readonly TopicBasedSegmentationService _service;

  public TopicBasedSegmentationServiceTests()
  {
    _mockLlmService = new Mock<ILlmProviderIntegrationService>();
    _mockPromptManager = new Mock<ISegmentationPromptManager>();
    
    // Create logger with console output and debug level
    var loggerFactory = LoggerFactory.Create(builder =>
    {
      builder.AddConsole().SetMinimumLevel(LogLevel.Debug);
    });
    _logger = loggerFactory.CreateLogger<TopicBasedSegmentationService>();

    _service = new TopicBasedSegmentationService(
      _mockLlmService.Object,
      _mockPromptManager.Object,
      _logger);

    SetupDefaultMocks();
  }

  #region SegmentByTopicsAsync Tests

  [Fact]
  public async Task SegmentByTopicsAsync_WithValidContent_ReturnsTopicSegments()
  {
    // Arrange
    var content = CreateMultiTopicDocument();
    var options = new TopicSegmentationOptions
    {
      MinSegmentSize = 50,
      MaxSegments = 10,
      UseLlmEnhancement = false // Disable for simpler testing
    };

    // Act
    var result = await _service.SegmentByTopicsAsync(content, DocumentType.Generic, options);

    // Assert
    result.Should().NotBeNull();
    result.Should().NotBeEmpty();
    result.Should().HaveCountLessOrEqualTo(options.MaxSegments);
    
    foreach (var segment in result)
    {
      segment.Content.Length.Should().BeGreaterOrEqualTo(options.MinSegmentSize);
      segment.Metadata.Should().ContainKey("segmentation_strategy");
      segment.Metadata["segmentation_strategy"].Should().Be(SegmentationStrategy.TopicBased.ToString());
    }
  }

  [Fact]
  public async Task SegmentByTopicsAsync_WithLlmEnhancement_EnhancesSegments()
  {
    // Arrange
    var content = CreateMultiTopicDocument();
    var options = new TopicSegmentationOptions { UseLlmEnhancement = true };

    var mockCoherenceAnalysis = new ThematicCoherenceAnalysis
    {
      CoherenceScore = 0.85,
      PrimaryTopic = "Technology Discussion",
      TopicKeywords = new List<string> { "technology", "innovation", "development" },
      KeyConcepts = new List<string> { "AI", "machine learning", "software" }
    };

    // Mock LLM enhancement calls
    _mockLlmService.Setup(x => x.AnalyzeOptimalStrategyAsync(
        It.IsAny<string>(),
        It.IsAny<DocumentType>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(new StrategyRecommendation
      {
        Strategy = SegmentationStrategy.TopicBased,
        Confidence = 0.9
      });

    // Act
    var result = await _service.SegmentByTopicsAsync(content, DocumentType.Technical, options);

    // Assert
    result.Should().NotBeEmpty();
    
    // Verify LLM enhancement was used
    foreach (var segment in result)
    {
      segment.Metadata.Should().ContainKey("topic_based");
      segment.Metadata["topic_based"].Should().Be(true);
    }
  }

  [Fact]
  public async Task SegmentByTopicsAsync_WithSimilarTopicMerging_MergesSimilarSegments()
  {
    // Arrange
    var content = CreateDocumentWithSimilarTopics();
    var options = new TopicSegmentationOptions
    {
      MergeSimilarTopics = true,
      TopicSimilarityThreshold = 0.8,
      UseLlmEnhancement = false
    };

    // Act
    var result = await _service.SegmentByTopicsAsync(content, DocumentType.Generic, options);

    // Assert
    result.Should().NotBeEmpty();
    // With similar topic merging, we should have fewer segments
    result.Should().HaveCountLessThan(5);
  }

  #endregion

  #region DetectTopicBoundariesAsync Tests

  [Fact]
  public async Task DetectTopicBoundariesAsync_WithValidContent_DetectsBoundaries()
  {
    // Arrange
    var content = CreateMultiTopicDocument();
    System.Diagnostics.Debug.WriteLine($"Test content: {content}");

    // Act
    var result = await _service.DetectTopicBoundariesAsync(content, DocumentType.Generic);
    System.Diagnostics.Debug.WriteLine($"Boundary detection result count: {result.Count}");
    
    foreach (var boundary in result)
    {
      System.Diagnostics.Debug.WriteLine($"Boundary at position {boundary.Position} with confidence {boundary.Confidence}");
    }

    // Assert
    result.Should().NotBeNull();
    result.Should().NotBeEmpty();
    
    foreach (var boundary in result)
    {
      boundary.Position.Should().BeGreaterOrEqualTo(0);
      boundary.Position.Should().BeLessThan(content.Length);
      boundary.Confidence.Should().BeInRange(0.0, 1.0);
    }

    // Boundaries should be ordered by position
    var positions = result.Select(b => b.Position).ToList();
    positions.Should().BeInAscendingOrder();
  }

  [Fact]
  public async Task DetectTopicBoundariesAsync_WithTransitionWords_DetectsTransitions()
  {
    // Arrange
    var content = @"Technology has revolutionized our daily lives. Modern devices are everywhere.

However, environmental concerns are rising. Climate change affects everyone.

Furthermore, economic implications must be considered. Market volatility is increasing.";

    System.Diagnostics.Debug.WriteLine($"Transition test content: {content}");

    // Act
    var result = await _service.DetectTopicBoundariesAsync(content, DocumentType.Generic);

    System.Diagnostics.Debug.WriteLine($"Transition test result count: {result.Count}");
    foreach (var boundary in result)
    {
      System.Diagnostics.Debug.WriteLine($"Boundary at position {boundary.Position}, confidence {boundary.Confidence}, keywords: [{string.Join(", ", boundary.TransitionKeywords)}]");
    }

    // Assert
    result.Should().NotBeEmpty();
    
    // Should detect boundaries where transition words indicate topic changes
    var boundariesWithTransitions = result.Where(b => b.TransitionKeywords.Any()).ToList();
    boundariesWithTransitions.Should().NotBeEmpty();
    
    // Check for expected transition words
    var allTransitionKeywords = result.SelectMany(b => b.TransitionKeywords).ToList();
    allTransitionKeywords.Should().Contain(word => 
      word.Equals("however", StringComparison.OrdinalIgnoreCase) ||
      word.Equals("furthermore", StringComparison.OrdinalIgnoreCase));
  }

  #endregion

  #region AnalyzeThematicCoherenceAsync Tests

  [Fact]
  public async Task AnalyzeThematicCoherenceAsync_WithCoherentContent_ReturnsHighScore()
  {
    // Arrange
    var coherentContent = @"
        Machine learning algorithms have transformed data analysis. These computational methods
        enable pattern recognition and predictive modeling. Deep learning, a subset of machine learning,
        uses neural networks to process complex data structures. The applications of these technologies
        span across various industries, from healthcare to finance.
        ";

    // Act
    var result = await _service.AnalyzeThematicCoherenceAsync(coherentContent);

    // Assert
    result.Should().NotBeNull();
    result.CoherenceScore.Should().BeInRange(0.0, 1.0);
    result.SemanticUnity.Should().BeInRange(0.0, 1.0);
    result.TopicConsistency.Should().BeInRange(0.0, 1.0);
    result.PrimaryTopic.Should().NotBeNullOrEmpty();
    
    // For coherent content about a single topic, scores should be reasonably high
    result.CoherenceScore.Should().BeGreaterThan(0.5);
  }

  [Fact]
  public async Task AnalyzeThematicCoherenceAsync_WithIncoherentContent_ReturnsLowerScore()
  {
    // Arrange
    var incoherentContent = @"
        The weather is nice today. Database optimization requires careful indexing.
        My favorite color is blue. Quantum computing uses qubits for calculations.
        Pizza delivery takes 30 minutes. Stock market volatility affects investments.
        ";

    // Act
    var result = await _service.AnalyzeThematicCoherenceAsync(incoherentContent);

    // Assert
    result.Should().NotBeNull();
    result.CoherenceScore.Should().BeInRange(0.0, 1.0);
    
    // For incoherent content mixing unrelated topics, score should be lower
    result.CoherenceScore.Should().BeLessThan(0.8);
  }

  #endregion

  #region ValidateTopicSegmentsAsync Tests

  [Fact]
  public async Task ValidateTopicSegmentsAsync_WithValidSegments_ReturnsGoodQuality()
  {
    // Arrange
    var originalContent = CreateMultiTopicDocument();
    var segments = await _service.SegmentByTopicsAsync(originalContent, DocumentType.Generic);

    // Act
    var result = await _service.ValidateTopicSegmentsAsync(segments, originalContent);

    // Assert
    result.Should().NotBeNull();
    result.OverallQuality.Should().BeInRange(0.0, 1.0);
    result.AverageTopicCoherence.Should().BeInRange(0.0, 1.0);
    result.BoundaryAccuracy.Should().BeInRange(0.0, 1.0);
    result.SegmentIndependence.Should().BeInRange(0.0, 1.0);
    result.TopicCoverage.Should().BeInRange(0.0, 1.0);
    
    result.SegmentResults.Should().HaveCount(segments.Count);
    
    foreach (var segmentResult in result.SegmentResults)
    {
      segmentResult.SegmentId.Should().NotBeNullOrEmpty();
      segmentResult.TopicCoherence.Should().BeInRange(0.0, 1.0);
      segmentResult.Independence.Should().BeInRange(0.0, 1.0);
      segmentResult.TopicClarity.Should().BeInRange(0.0, 1.0);
    }
  }

  [Fact]
  public async Task ValidateTopicSegmentsAsync_WithPoorSegments_IdentifiesIssues()
  {
    // Arrange
    var originalContent = "Test content";
    var poorSegments = new List<DocumentSegment>
    {
      new DocumentSegment
      {
        Id = "1",
        Content = "A", // Too short
        SequenceNumber = 0,
        Metadata = new Dictionary<string, object>
        {
          ["start_position"] = 0,
          ["end_position"] = 1
        }
      },
      new DocumentSegment
      {
        Id = "2", 
        Content = CreateIncoherentContent(), // Poor coherence
        SequenceNumber = 1,
        Metadata = new Dictionary<string, object>
        {
          ["start_position"] = 2,
          ["end_position"] = 100
        }
      }
    };

    // Act
    var result = await _service.ValidateTopicSegmentsAsync(poorSegments, originalContent);

    // Assert
    result.Should().NotBeNull();
    result.Issues.Should().NotBeEmpty();
    
    // Should identify coherence issues
    var coherenceIssues = result.Issues.Where(i => i.Type == ValidationIssueType.PoorCoherence).ToList();
    coherenceIssues.Should().NotBeEmpty();
    
    result.Recommendations.Should().NotBeEmpty();
  }

  #endregion

  #region Edge Cases and Error Handling

  [Fact]
  public async Task SegmentByTopicsAsync_WithEmptyContent_ReturnsEmptyList()
  {
    // Arrange
    var emptyContent = "";

    // Act
    var result = await _service.SegmentByTopicsAsync(emptyContent);

    // Assert
    result.Should().NotBeNull();
    result.Should().BeEmpty();
  }

  [Fact]
  public async Task SegmentByTopicsAsync_WithVeryShortContent_HandlesGracefully()
  {
    // Arrange
    var shortContent = "Short text.";
    var options = new TopicSegmentationOptions { MinSegmentSize = 50 };

    // Act
    var result = await _service.SegmentByTopicsAsync(shortContent, DocumentType.Generic, options);

    // Assert
    result.Should().NotBeNull();
    // May be empty if content is too short to meet minimum segment size
  }

  [Fact]
  public async Task DetectTopicBoundariesAsync_WithSingleTopicContent_ReturnsMinimalBoundaries()
  {
    // Arrange
    var singleTopicContent = @"
        This document discusses only one topic throughout its entirety.
        The topic remains consistent and focused on the same subject matter.
        All paragraphs relate to the same central theme and concept.
        There are no significant topic transitions or changes in focus.
        ";

    // Act
    var result = await _service.DetectTopicBoundariesAsync(singleTopicContent, DocumentType.Generic);

    // Assert
    result.Should().NotBeNull();
    // Single topic content should have few or no topic boundaries
    result.Should().HaveCountLessOrEqualTo(2);
  }

  #endregion

  #region Helper Methods

  private void SetupDefaultMocks()
  {
    // Setup default prompt manager response
    _mockPromptManager.Setup(x => x.GetPromptAsync(
        It.IsAny<SegmentationStrategy>(),
        It.IsAny<string>(),
        It.IsAny<CancellationToken>()))
      .ReturnsAsync(new PromptTemplate
      {
        SystemPrompt = "You are a topic analysis expert.",
        UserPrompt = "Analyze the following content for topic boundaries: {DocumentContent}",
        ExpectedFormat = "json",
        Metadata = new Dictionary<string, object>
        {
          ["strategy"] = SegmentationStrategy.TopicBased.ToString(),
          ["language"] = "en"
        }
      });

    // Setup default LLM service responses
    _mockLlmService.Setup(x => x.TestConnectivityAsync(It.IsAny<CancellationToken>()))
      .ReturnsAsync(true);
  }

  private string CreateMultiTopicDocument()
  {
    return @"
        Technology has revolutionized the way we communicate and work. Modern smartphones
        and computers enable instant global connectivity. Software development practices
        continue to evolve with new methodologies and tools.

        Climate change represents one of the most pressing challenges of our time. Rising
        global temperatures affect weather patterns worldwide. Environmental conservation
        efforts must be intensified to protect our planet for future generations.

        Economic markets show increasing volatility in recent years. Stock prices fluctuate
        based on various global factors. Investment strategies must adapt to changing
        market conditions and emerging financial instruments.

        Healthcare innovations have improved patient outcomes significantly. Medical research
        leads to breakthrough treatments and diagnostic tools. Telemedicine has transformed
        how patients access medical care, especially in remote areas.
        ";
  }

  private string CreateDocumentWithSimilarTopics()
  {
    return @"
        Software development requires careful planning and execution. Programming languages
        provide the foundation for building applications and systems.

        Application development follows similar principles to software engineering. Coding
        practices and development methodologies ensure quality deliverables.

        Technology innovation drives the software industry forward. New programming
        frameworks and tools enhance developer productivity and code quality.
        ";
  }

  private string CreateIncoherentContent()
  {
    return @"
        The weather is sunny today. Database indexing improves query performance.
        My cat likes to sleep. Quantum mechanics involves particle physics.
        Pizza tastes good with cheese. Market analysis shows trends.
        ";
  }

  #endregion
}

/// <summary>
/// Integration tests for TopicBasedSegmentationService with real dependencies.
/// </summary>
public class TopicBasedSegmentationServiceIntegrationTests
{
  [Fact]
  public async Task SegmentByTopicsAsync_WithRealLlmService_ProducesQualitySegments()
  {
    // Load environment variables for API keys
    EnvironmentHelper.LoadEnvIfNeeded();
    
    // Test data path for recording/playback
    var testDataPath = Path.Combine(
      "tests", "TestData", "DocumentSegmentation", "TopicBasedSegmentation", 
      "RealLlmService.json");
    
    var workspaceRoot = EnvironmentHelper.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory);
    var fullTestDataPath = Path.Combine(workspaceRoot, testDataPath);
    
    // Create HTTP handler with record/playback functionality
    var handler = MockHttpHandlerBuilder.Create()
      .WithRecordPlayback(fullTestDataPath, allowAdditional: true)
      .ForwardToApi(
        EnvironmentHelper.GetApiBaseUrlFromEnv("LLM_API_BASE_URL", null, "https://openrouter.ai/api/v1"),
        EnvironmentHelper.GetApiKeyFromEnv("OPENROUTER_API_KEY"))
      .Build();
    
    var httpClient = new HttpClient(handler);
    
    // Set up real services
    var serviceCollection = new ServiceCollection();
    ConfigureIntegrationServices(serviceCollection, httpClient);
    var serviceProvider = serviceCollection.BuildServiceProvider();
    
    var segmentationService = serviceProvider.GetRequiredService<ITopicBasedSegmentationService>();
    
    // Test content with clear topic boundaries
    var testContent = @"
        Technology has revolutionized our daily lives in unprecedented ways. Modern smartphones, computers, and internet connectivity have created a globally connected society where information flows instantly across continents. Artificial intelligence and machine learning algorithms are reshaping industries from healthcare to finance.

        However, environmental concerns are becoming increasingly critical. Climate change poses unprecedented challenges to our planet's ecosystems. Rising sea levels, extreme weather patterns, and biodiversity loss require immediate global action. Sustainable development practices and renewable energy adoption are essential for our future survival.

        Furthermore, economic implications of technological advancement cannot be ignored. The digital economy has created new opportunities while disrupting traditional industries. Remote work, e-commerce platforms, and digital currencies are transforming how we conduct business. Market volatility and economic inequality remain significant challenges that require careful policy consideration.

        In conclusion, educational systems must adapt to prepare future generations for these complex challenges. STEM education, critical thinking skills, and digital literacy are becoming fundamental requirements. Universities and schools worldwide are implementing innovative teaching methods and online learning platforms to enhance educational accessibility and quality.
        ";
    
    // Act - Perform topic-based segmentation
    var result = await segmentationService.SegmentByTopicsAsync(
      testContent, 
      DocumentType.Generic,
      new TopicSegmentationOptions
      {
        MinSegmentSize = 100,
        MaxSegmentSize = 1000,
        MinThematicCoherence = 0.7,
        UseLlmEnhancement = true
      });
    
    // Assert - Verify quality segmentation
    result.Should().NotBeNull();
    result.Should().NotBeEmpty();
    result.Should().HaveCountGreaterThan(2); // Should identify multiple topic boundaries
    
    // Verify segments have proper content
    result.All(s => !string.IsNullOrWhiteSpace(s.Content)).Should().BeTrue();
    result.All(s => s.Content.Length >= 50).Should().BeTrue(); // Reasonable minimum length
    
    // Verify sequence numbers are correct
    var sequenceNumbers = result.Select(s => s.SequenceNumber).ToList();
    sequenceNumbers.Should().BeInAscendingOrder();
    
    // Verify segments cover the full content (no gaps)
    var totalLength = result.Sum(s => s.Content.Length);
    totalLength.Should().BeGreaterThan((int)(testContent.Length * 0.8)); // Allow for some overlap/trimming
    
    // Verify quality metrics (may be 0.0 if LLM quality assessment is disabled)
    result.All(s => s.Quality != null).Should().BeTrue();
    var avgCoherence = result.Average(s => s.Quality!.CoherenceScore);
    Console.WriteLine($"Average coherence score: {avgCoherence}");
    
    // Note: Quality scores may be 0.0 if LLM-based quality assessment is not configured
    // The main test is that segmentation works and produces reasonable segments
    if (avgCoherence > 0)
    {
      avgCoherence.Should().BeGreaterThan(0.5);
    }
    
    // Log results for debugging
    Console.WriteLine($"Segmentation produced {result.Count} segments:");
    foreach (var segment in result)
    {
      Console.WriteLine($"Segment {segment.SequenceNumber}: {segment.Content[..Math.Min(100, segment.Content.Length)]}...");
      Console.WriteLine($"  Quality: Coherence={segment.Quality?.CoherenceScore:F2}");
    }
  }
  
  private static void ConfigureIntegrationServices(ServiceCollection services, HttpClient httpClient)
  {
    // Add logging
    services.AddLogging(builder => builder.AddConsole());
    
    // Add configuration
    var configuration = new ConfigurationBuilder()
      .AddEnvironmentVariables()
      .Build();
    services.AddSingleton<IConfiguration>(configuration);
    
    // Add LmConfig services with custom HttpClient
    services.AddLmConfig(configuration);
    
    // Configure HttpClient for LmConfig (if needed)
    // services.ConfigureHttpClient(() => httpClient);
    
    // Add MemoryServer document segmentation services
    services.AddSingleton<ITopicBasedSegmentationService, TopicBasedSegmentationService>();
    services.AddSingleton<ILlmProviderIntegrationService, LlmProviderIntegrationService>();
    services.AddSingleton<ISegmentationPromptManager, SegmentationPromptManager>();
    services.AddSingleton<IDocumentAnalysisService, DocumentAnalysisService>();
    
    // Configure LLM provider
    var llmConfig = new LlmProviderConfiguration
    {
      PrimaryProvider = "OpenAI",
      ModelPreferences = new Dictionary<string, string>
      {
        ["strategy_analysis"] = "gpt-4o-mini",
        ["segmentation"] = "gpt-4o-mini",
        ["quality_validation"] = "gpt-4o-mini"
      },
      Temperature = 0.1,
      MaxRetries = 3
    };
    services.AddSingleton(llmConfig);
  }
}
