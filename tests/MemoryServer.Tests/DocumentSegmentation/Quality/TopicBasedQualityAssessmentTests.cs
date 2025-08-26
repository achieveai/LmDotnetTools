using System.Text;
using FluentAssertions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace MemoryServer.DocumentSegmentation.Tests.Quality;

/// <summary>
/// Advanced quality assessment tests for Topic-Based Segmentation.
/// Tests sophisticated quality metrics and validation algorithms.
/// </summary>
public class TopicBasedQualityAssessmentTests
{
    private readonly Mock<ILlmProviderIntegrationService> _mockLlmService;
    private readonly Mock<ISegmentationPromptManager> _mockPromptManager;
    private readonly ILogger<TopicBasedSegmentationService> _logger;
    private readonly TopicBasedSegmentationService _service;
    private readonly ITestOutputHelper _output;

    public TopicBasedQualityAssessmentTests(ITestOutputHelper output)
    {
        _output = output;
        _mockLlmService = new Mock<ILlmProviderIntegrationService>();
        _mockPromptManager = new Mock<ISegmentationPromptManager>();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole().SetMinimumLevel(LogLevel.Information);
        });
        _logger = loggerFactory.CreateLogger<TopicBasedSegmentationService>();

        _service = new TopicBasedSegmentationService(
            _mockLlmService.Object,
            _mockPromptManager.Object,
            _logger
        );

        SetupDefaultMocks();
    }

    #region Topic Coherence Scoring Tests

    [Theory]
    [MemberData(nameof(TopicCoherenceTestCases))]
    public async Task AnalyzeThematicCoherence_WithVariousContent_ReturnsAccurateCoherenceScores(
        string testName,
        string content,
        double expectedMinCoherence,
        double expectedMaxCoherence,
        string description
    )
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine($"Running coherence test: {testName}");
        System.Diagnostics.Debug.WriteLine($"Description: {description}");
        System.Diagnostics.Debug.WriteLine(
            $"Expected range: {expectedMinCoherence:F2} - {expectedMaxCoherence:F2}"
        );
        System.Diagnostics.Debug.WriteLine($"Content length: {content.Length} characters");

        // Act
        var result = await _service.AnalyzeThematicCoherenceAsync(content);

        // Assert
        result.Should().NotBeNull();
        result
            .CoherenceScore.Should()
            .BeInRange(
                expectedMinCoherence,
                expectedMaxCoherence,
                $"Coherence score for {testName} should be between {expectedMinCoherence:F2} and {expectedMaxCoherence:F2}"
            );

        System.Diagnostics.Debug.WriteLine($"Actual coherence score: {result.CoherenceScore:F2}");
        System.Diagnostics.Debug.WriteLine($"Primary topic: {result.PrimaryTopic}");
        System.Diagnostics.Debug.WriteLine(
            $"Topic keywords: [{string.Join(", ", result.TopicKeywords)}]"
        );

        // Additional quality checks
        result.SemanticUnity.Should().BeInRange(0.0, 1.0);
        result.TopicConsistency.Should().BeInRange(0.0, 1.0);
        result.PrimaryTopic.Should().NotBeNullOrWhiteSpace();

        _output.WriteLine(
            $"{testName}: Score={result.CoherenceScore:F2}, Topic={result.PrimaryTopic}"
        );
    }

    [Fact]
    public async Task AnalyzeThematicCoherence_WithMultipleTopics_IdentifiesTopicTransitions()
    {
        // Arrange
        var multiTopicContent =
            @"
      Artificial intelligence has revolutionized data processing. Machine learning algorithms 
      can now identify patterns in vast datasets with remarkable accuracy.
      
      Moving to a different subject, cooking techniques have evolved significantly over the centuries.
      French cuisine emphasizes precise temperature control and timing in food preparation.
      
      In contrast, automotive engineering focuses on mechanical systems and propulsion.
      Electric vehicles are becoming more prevalent due to environmental concerns.
    ";

        System.Diagnostics.Debug.WriteLine($"Multi-topic content: {multiTopicContent}");

        // Act
        var result = await _service.AnalyzeThematicCoherenceAsync(multiTopicContent);

        System.Diagnostics.Debug.WriteLine(
            $"Coherence result: Score={result.CoherenceScore:F3}, PrimaryTopic={result.PrimaryTopic}"
        );

        // Assert
        result.Should().NotBeNull();

        // Multiple topics should result in lower coherence
        result
            .CoherenceScore.Should()
            .BeLessThan(0.8, "Content with multiple distinct topics should have lower coherence");

        // Should identify topic diversity
        result
            .TopicKeywords.Should()
            .HaveCountGreaterThan(5, "Multi-topic content should have diverse keywords");

        System.Diagnostics.Debug.WriteLine($"Multi-topic coherence: {result.CoherenceScore:F2}");
        System.Diagnostics.Debug.WriteLine(
            $"Keywords found: [{string.Join(", ", result.TopicKeywords)}]"
        );
    }

    #endregion

    #region Semantic Similarity Analysis Tests

    [Theory]
    [MemberData(nameof(SimilarityTestCases))]
    public async Task AnalyzeSegmentSimilarity_WithVariousSegmentPairs_ReturnsAccurateSimilarity(
        string testName,
        string segment1,
        string segment2,
        double expectedMinSimilarity,
        double expectedMaxSimilarity,
        string description
    )
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine($"Running similarity test: {testName}");
        System.Diagnostics.Debug.WriteLine($"Description: {description}");
        System.Diagnostics.Debug.WriteLine($"Segment 1 length: {segment1.Length}");
        System.Diagnostics.Debug.WriteLine($"Segment 2 length: {segment2.Length}");

        var segments = new List<DocumentSegment>
        {
            CreateSegment("1", segment1, 0),
            CreateSegment("2", segment2, 1),
        };

        // Act
        var validation = await _service.ValidateTopicSegmentsAsync(
            segments,
            segment1 + "\n\n" + segment2
        );

        // Assert
        validation.Should().NotBeNull();

        // Check segment independence (inverse of similarity)
        var independence = validation.SegmentIndependence;
        var similarity = 1.0 - independence; // Convert independence to similarity

        similarity
            .Should()
            .BeInRange(
                expectedMinSimilarity,
                expectedMaxSimilarity,
                $"Similarity for {testName} should be between {expectedMinSimilarity:F2} and {expectedMaxSimilarity:F2}"
            );

        System.Diagnostics.Debug.WriteLine($"Calculated similarity: {similarity:F2}");
        System.Diagnostics.Debug.WriteLine($"Segment independence: {independence:F2}");

        _output.WriteLine(
            $"{testName}: Similarity={similarity:F2}, Independence={independence:F2}"
        );
    }

    [Fact]
    public async Task ValidateTopicSegments_WithSimilarAdjacentSegments_IdentifiesRedundancy()
    {
        // Arrange
        var similarSegments = new List<DocumentSegment>
        {
            CreateSegment("1", "Machine learning algorithms process data efficiently.", 0),
            CreateSegment("2", "Data processing using machine learning is very efficient.", 1),
            CreateSegment("3", "Artificial intelligence handles information processing well.", 2),
        };

        var originalContent = string.Join("\n\n", similarSegments.Select(s => s.Content));
        System.Diagnostics.Debug.WriteLine(
            $"Testing similar segments with content length: {originalContent.Length}"
        );

        // Act
        var validation = await _service.ValidateTopicSegmentsAsync(
            similarSegments,
            originalContent
        );

        // Assert
        validation.Should().NotBeNull();

        // Should identify low independence due to similarity
        validation
            .SegmentIndependence.Should()
            .BeLessThan(0.7, "Similar segments should have low independence scores");

        // Should recommend consolidation
        var consolidationRecommendations = validation
            .Recommendations.Where(r =>
                r.Contains("merge", StringComparison.OrdinalIgnoreCase)
                || r.Contains("consolidat", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        consolidationRecommendations
            .Should()
            .NotBeEmpty("Should recommend consolidating similar segments");

        System.Diagnostics.Debug.WriteLine(
            $"Independence score: {validation.SegmentIndependence:F2}"
        );
        System.Diagnostics.Debug.WriteLine(
            $"Consolidation recommendations: {consolidationRecommendations.Count}"
        );
    }

    #endregion

    #region Topic Coverage Validation Tests

    [Theory]
    [MemberData(nameof(TopicCoverageTestCases))]
    public async Task ValidateTopicCoverage_WithVariousDocuments_AssessesCompleteness(
        string testName,
        string originalDocument,
        List<string> segmentContents,
        double expectedMinCoverage,
        double expectedMaxCoverage,
        string description
    )
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine($"Running coverage test: {testName}");
        System.Diagnostics.Debug.WriteLine($"Description: {description}");
        System.Diagnostics.Debug.WriteLine($"Original document length: {originalDocument.Length}");
        System.Diagnostics.Debug.WriteLine($"Number of segments: {segmentContents.Count}");

        var segments = segmentContents
            .Select((content, index) => CreateSegment((index + 1).ToString(), content, index))
            .ToList();

        // Act
        var validation = await _service.ValidateTopicSegmentsAsync(segments, originalDocument);

        // Assert
        validation.Should().NotBeNull();
        validation
            .TopicCoverage.Should()
            .BeInRange(
                expectedMinCoverage,
                expectedMaxCoverage,
                $"Topic coverage for {testName} should be between {expectedMinCoverage:F2} and {expectedMaxCoverage:F2}"
            );

        System.Diagnostics.Debug.WriteLine($"Actual topic coverage: {validation.TopicCoverage:F2}");
        System.Diagnostics.Debug.WriteLine($"Overall quality: {validation.OverallQuality:F2}");

        _output.WriteLine(
            $"{testName}: Coverage={validation.TopicCoverage:F2}, Quality={validation.OverallQuality:F2}"
        );
    }

    [Fact]
    public async Task ValidateTopicCoverage_WithIncompleteSegmentation_IdentifiesGaps()
    {
        // Arrange
        var completeDocument =
            @"
      Technology has transformed modern communication. Email, instant messaging, and video calls
      have revolutionized how we interact professionally and personally.
      
      Education systems have also evolved significantly. Online learning platforms provide
      accessible education to millions of students worldwide, breaking geographical barriers.
      
      Healthcare innovations continue to improve patient outcomes. Telemedicine and AI diagnostics
      are making healthcare more efficient and accessible to underserved populations.
    ";

        // Segments that only cover first two topics (missing healthcare)
        var incompleteSegments = new List<DocumentSegment>
        {
            CreateSegment(
                "1",
                "Technology has transformed modern communication. Email and messaging changed interactions.",
                0
            ),
            CreateSegment(
                "2",
                "Education systems evolved with online learning platforms for global access.",
                1
            ),
        };

        System.Diagnostics.Debug.WriteLine(
            $"Testing incomplete coverage with {incompleteSegments.Count} segments"
        );

        // Act
        var validation = await _service.ValidateTopicSegmentsAsync(
            incompleteSegments,
            completeDocument
        );

        // Assert
        validation.Should().NotBeNull();

        // Should detect incomplete coverage
        validation
            .TopicCoverage.Should()
            .BeLessThan(0.9, "Incomplete segmentation should have low topic coverage");

        // Should identify content gaps
        var gapIssues = validation
            .Issues.Where(i => i.Type == ValidationIssueType.MissingContext)
            .ToList();
        gapIssues.Should().NotBeEmpty("Should identify content gaps in incomplete segmentation");

        System.Diagnostics.Debug.WriteLine($"Coverage score: {validation.TopicCoverage:F2}");
        System.Diagnostics.Debug.WriteLine($"Gap issues found: {gapIssues.Count}");
    }

    #endregion

    #region Quality Benchmarking Suite Tests

    [Theory]
    [MemberData(nameof(QualityBenchmarkTestCases))]
    public async Task QualityBenchmarkSuite_WithKnownGoodBadExamples_ProducesExpectedScores(
        string benchmarkName,
        string content,
        SegmentationStrategy strategy,
        double expectedMinQuality,
        double expectedMaxQuality,
        string qualityDescription
    )
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine($"Running benchmark: {benchmarkName}");
        System.Diagnostics.Debug.WriteLine($"Strategy: {strategy}");
        System.Diagnostics.Debug.WriteLine($"Quality description: {qualityDescription}");

        var options = new TopicSegmentationOptions
        {
            UseLlmEnhancement = false,
            MinSegmentSize = 50,
        };

        // Act
        var segments = await _service.SegmentByTopicsAsync(content, DocumentType.Generic, options);
        var validation = await _service.ValidateTopicSegmentsAsync(segments, content);

        // Assert
        validation.Should().NotBeNull();
        validation
            .OverallQuality.Should()
            .BeInRange(
                expectedMinQuality,
                expectedMaxQuality,
                $"Quality for {benchmarkName} should be between {expectedMinQuality:F2} and {expectedMaxQuality:F2}"
            );

        System.Diagnostics.Debug.WriteLine($"Segments created: {segments.Count()}");
        System.Diagnostics.Debug.WriteLine($"Overall quality: {validation.OverallQuality:F2}");
        System.Diagnostics.Debug.WriteLine(
            $"Topic coherence: {validation.AverageTopicCoherence:F2}"
        );
        System.Diagnostics.Debug.WriteLine($"Boundary accuracy: {validation.BoundaryAccuracy:F2}");

        _output.WriteLine(
            $"{benchmarkName}: Quality={validation.OverallQuality:F2}, Segments={segments.Count()}"
        );
    }

    [Fact]
    public async Task QualityBenchmark_WithExcellentSegmentation_AchievesHighScores()
    {
        // Arrange - Well-structured content with clear topic boundaries
        var excellentContent =
            @"
      Introduction to Machine Learning
      
      Machine learning is a subset of artificial intelligence that enables computers to learn
      and improve from experience without being explicitly programmed. This field has gained
      tremendous importance in recent years due to advances in computing power and data availability.
      
      Types of Machine Learning Algorithms
      
      There are three primary categories of machine learning algorithms. Supervised learning uses
      labeled training data to learn a mapping function. Unsupervised learning finds hidden patterns
      in data without labeled examples. Reinforcement learning learns through interaction with an
      environment using rewards and penalties.
      
      Applications in Industry
      
      Machine learning applications span across numerous industries. In healthcare, ML helps with
      medical diagnosis and drug discovery. In finance, algorithms detect fraud and automate trading.
      In technology, ML powers recommendation systems and natural language processing.
      
      Future Prospects
      
      The future of machine learning looks promising with emerging technologies like quantum computing
      and neuromorphic chips. Ethical considerations and explainable AI will become increasingly
      important as ML systems become more prevalent in decision-making processes.
    ";

        System.Diagnostics.Debug.WriteLine(
            $"Testing excellent content with length: {excellentContent.Length}"
        );

        // Act
        var segments = await _service.SegmentByTopicsAsync(
            excellentContent,
            DocumentType.Technical
        );
        var validation = await _service.ValidateTopicSegmentsAsync(segments, excellentContent);

        // Assert
        validation.Should().NotBeNull();

        // Excellent content should achieve high quality scores
        validation
            .OverallQuality.Should()
            .BeGreaterThan(0.7, "Well-structured content should achieve high overall quality");

        validation
            .AverageTopicCoherence.Should()
            .BeGreaterThan(0.75, "Clear topics should have high coherence scores");

        validation
            .TopicCoverage.Should()
            .BeGreaterThan(0.8, "Comprehensive segmentation should have high topic coverage");

        System.Diagnostics.Debug.WriteLine($"Excellent content results:");
        System.Diagnostics.Debug.WriteLine($"  Overall quality: {validation.OverallQuality:F2}");
        System.Diagnostics.Debug.WriteLine(
            $"  Topic coherence: {validation.AverageTopicCoherence:F2}"
        );
        System.Diagnostics.Debug.WriteLine($"  Topic coverage: {validation.TopicCoverage:F2}");
        System.Diagnostics.Debug.WriteLine(
            $"  Boundary accuracy: {validation.BoundaryAccuracy:F2}"
        );
    }

    [Fact]
    public async Task QualityBenchmark_WithPoorSegmentation_IdentifiesIssues()
    {
        // Arrange - Poorly structured content with mixed topics
        var poorContent =
            @"
      Random thoughts about various topics. Technology is important but also cooking
      is great. I like pizza and machine learning algorithms. The weather today is
      nice and database optimization requires indexing. My cat likes to play with
      quantum computing concepts while I'm debugging JavaScript code. Economic
      markets fluctuate based on artificial intelligence trends and my favorite
      color is blue which reminds me of deep learning neural networks.
    ";

        System.Diagnostics.Debug.WriteLine(
            $"Testing poor content with length: {poorContent.Length}"
        );

        // Act
        var segments = await _service.SegmentByTopicsAsync(poorContent, DocumentType.Generic);
        var validation = await _service.ValidateTopicSegmentsAsync(segments, poorContent);

        // Assert
        validation.Should().NotBeNull();

        // Poor content should result in quality issues
        validation.Issues.Should().NotBeEmpty("Poor content should generate quality issues");

        // Should identify coherence problems
        var coherenceIssues = validation
            .Issues.Where(i => i.Type == ValidationIssueType.PoorCoherence)
            .ToList();
        coherenceIssues.Should().NotBeEmpty("Should identify poor coherence in mixed content");

        System.Diagnostics.Debug.WriteLine($"Poor content results:");
        System.Diagnostics.Debug.WriteLine($"  Overall quality: {validation.OverallQuality:F2}");
        System.Diagnostics.Debug.WriteLine($"  Issues found: {validation.Issues.Count}");
        System.Diagnostics.Debug.WriteLine($"  Coherence issues: {coherenceIssues.Count}");

        foreach (var issue in validation.Issues.Take(3))
        {
            System.Diagnostics.Debug.WriteLine($"  Issue: {issue.Type} - {issue.Description}");
        }
    }

    #endregion

    #region Test Data Providers

    public static IEnumerable<object[]> TopicCoherenceTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "High Coherence - Single Topic",
                @"Machine learning algorithms have revolutionized data analysis in recent years. These 
        computational methods enable pattern recognition and predictive modeling across various 
        industries. Deep learning, a subset of machine learning, uses neural networks to process 
        complex data structures. The applications of these technologies continue to expand as 
        computing power increases and datasets become more sophisticated.",
                0.75,
                1.0,
                "Focused content about machine learning should have high coherence",
            },
            new object[]
            {
                "Medium Coherence - Related Topics",
                @"Software development requires careful planning and systematic approaches. Programming 
        languages provide the tools for building applications and systems. Testing methodologies 
        ensure code quality and reliability. Project management coordinates these activities to 
        deliver successful software products on time and within budget.",
                0.6,
                0.85,
                "Related software development topics should have medium-high coherence",
            },
            new object[]
            {
                "Low Coherence - Mixed Topics",
                @"The weather is beautiful today with sunny skies. Database optimization requires proper 
        indexing strategies for performance. My favorite recipe includes fresh ingredients and 
        careful preparation. Economic markets show volatility due to various global factors.",
                0.0,
                0.5,
                "Unrelated mixed topics should have low coherence",
            },
            new object[]
            {
                "Very Low Coherence - Random Content",
                @"Purple elephants dance while eating computational algorithms. The stock market tastes 
        like JavaScript functions mixed with automotive engineering principles. Quantum physics 
        smells like database normalization procedures on a Tuesday afternoon.",
                0.0,
                0.3,
                "Nonsensical random content should have very low coherence",
            },
        };

    public static IEnumerable<object[]> SimilarityTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "High Similarity - Same Topic",
                "Machine learning algorithms process data to identify patterns and make predictions.",
                "Data processing using machine learning helps identify patterns for predictive analysis.",
                0.7,
                1.0,
                "Segments about the same topic should have high similarity",
            },
            new object[]
            {
                "Medium Similarity - Related Topics",
                "Software development requires systematic planning and organized approaches to coding.",
                "Project management in technology involves coordinating development teams and resources.",
                0.4,
                0.7,
                "Related but distinct topics should have medium similarity",
            },
            new object[]
            {
                "Low Similarity - Different Topics",
                "Artificial intelligence transforms how we process and analyze large datasets.",
                "Cooking techniques vary significantly across different cultural traditions and regions.",
                0.0,
                0.4,
                "Completely different topics should have low similarity",
            },
        };

    public static IEnumerable<object[]> TopicCoverageTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "Complete Coverage",
                @"Technology advances rapidly. Education systems evolve. Healthcare improves continuously.",
                new List<string>
                {
                    "Technology advances rapidly in various fields.",
                    "Education systems evolve with new methodologies.",
                    "Healthcare improves continuously with innovations.",
                },
                0.85,
                1.0,
                "Complete segmentation should achieve high coverage",
            },
            new object[]
            {
                "Partial Coverage",
                @"Technology advances rapidly. Education systems evolve. Healthcare improves continuously.",
                new List<string>
                {
                    "Technology advances rapidly in various fields.",
                    "Education systems evolve with new methodologies.",
                },
                0.5,
                0.8,
                "Partial segmentation should achieve medium coverage",
            },
            new object[]
            {
                "Poor Coverage",
                @"Technology advances rapidly. Education systems evolve. Healthcare improves continuously.",
                new List<string> { "Technology advances rapidly." },
                0.2,
                0.6,
                "Minimal segmentation should achieve low coverage",
            },
        };

    public static IEnumerable<object[]> QualityBenchmarkTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "Excellent Structure",
                @"Introduction: This document covers three main areas.
        
        Section 1: Technology
        Modern technology has transformed our daily lives through automation and connectivity.
        
        Section 2: Education  
        Educational systems have adapted to include digital learning platforms and resources.
        
        Conclusion: These changes represent significant progress in human development.",
                SegmentationStrategy.TopicBased,
                0.7,
                1.0,
                "Well-structured content with clear sections should achieve high quality",
            },
            new object[]
            {
                "Good Structure",
                @"Technology continues to advance rapidly in various fields. Artificial intelligence and 
        machine learning are revolutionizing data analysis. These innovations impact multiple 
        industries from healthcare to finance, creating new opportunities and challenges.",
                SegmentationStrategy.TopicBased,
                0.6,
                0.85,
                "Coherent content should achieve good quality scores",
            },
            new object[]
            {
                "Poor Structure",
                @"Random thoughts about stuff. Technology is good but cooking is better. I like cats and 
        databases need optimization. The weather affects machine learning somehow. Economic 
        principles relate to quantum physics in my opinion.",
                SegmentationStrategy.TopicBased,
                0.2,
                0.6,
                "Incoherent content should achieve lower quality scores",
            },
        };

    #endregion

    #region Helper Methods

    private void SetupDefaultMocks()
    {
        _mockPromptManager
            .Setup(x =>
                x.GetPromptAsync(
                    It.IsAny<SegmentationStrategy>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new PromptTemplate
                {
                    SystemPrompt = "You are an advanced topic analysis expert.",
                    UserPrompt =
                        "Analyze the following content for quality assessment: {DocumentContent}",
                    ExpectedFormat = "json",
                }
            );

        _mockLlmService
            .Setup(x => x.TestConnectivityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private DocumentSegment CreateSegment(string id, string content, int sequenceNumber)
    {
        return new DocumentSegment
        {
            Id = id,
            Content = content,
            SequenceNumber = sequenceNumber,
            Metadata = new Dictionary<string, object>
            {
                ["start_position"] = sequenceNumber * 100,
                ["end_position"] = (sequenceNumber * 100) + content.Length,
                ["segmentation_strategy"] = SegmentationStrategy.TopicBased.ToString(),
            },
        };
    }

    #endregion
}
