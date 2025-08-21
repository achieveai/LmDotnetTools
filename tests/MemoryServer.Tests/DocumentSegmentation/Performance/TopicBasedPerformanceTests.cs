using FluentAssertions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace MemoryServer.DocumentSegmentation.Tests.Performance;

/// <summary>
/// Performance and scalability tests for TopicBasedSegmentationService.
/// These tests ensure the service can handle production-scale workloads efficiently.
/// </summary>
public class TopicBasedPerformanceTests
{
    private readonly Mock<ILlmProviderIntegrationService> _mockLlmService;
    private readonly Mock<ISegmentationPromptManager> _mockPromptManager;
    private readonly ILogger<TopicBasedSegmentationService> _logger;
    private readonly TopicBasedSegmentationService _service;
    private readonly ITestOutputHelper _output;

    public TopicBasedPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _mockLlmService = new Mock<ILlmProviderIntegrationService>();
        _mockPromptManager = new Mock<ISegmentationPromptManager>();

        // Create logger with test output
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug().SetMinimumLevel(LogLevel.Information);
        });
        _logger = loggerFactory.CreateLogger<TopicBasedSegmentationService>();

        _service = new TopicBasedSegmentationService(
            _mockLlmService.Object,
            _mockPromptManager.Object,
            _logger);

        SetupDefaultMocks();
    }

    #region Large Document Testing

    [Fact]
    public async Task SegmentByTopicsAsync_WithLargeDocument_ProcessesEfficiently()
    {
        // Arrange - Create a 10k+ word document
        var largeDocument = GenerateLargeMultiTopicDocument(10000);
        var options = new TopicSegmentationOptions
        {
            MinSegmentSize = 200,
            MaxSegmentSize = 2000,
            UseLlmEnhancement = false // Disable LLM for consistent timing
        };

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await _service.SegmentByTopicsAsync(largeDocument, DocumentType.Generic, options);

        stopwatch.Stop();
        var processingTimeMs = stopwatch.ElapsedMilliseconds;
        var wordsPerSecond = (largeDocument.Split(' ').Length / (processingTimeMs / 1000.0));

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();

        // Performance requirements: Should process at least 1000 words/second
        wordsPerSecond.Should().BeGreaterThan(1000,
            $"Processing speed was {wordsPerSecond:F2} words/second, should be >1000");

        // Should complete within reasonable time (< 30 seconds for 10k words)
        processingTimeMs.Should().BeLessThan(30000,
            $"Processing took {processingTimeMs}ms, should be <30000ms");

        // Memory validation - each segment should have reasonable content
        result.All(s => s.Content.Length >= options.MinSegmentSize).Should().BeTrue();
        result.All(s => s.Content.Length <= options.MaxSegmentSize * 1.2).Should().BeTrue(); // Allow 20% variance

        _output.WriteLine($"Large Document Performance:");
        _output.WriteLine($"  Document size: {largeDocument.Length:N0} characters");
        _output.WriteLine($"  Word count: {largeDocument.Split(' ').Length:N0} words");
        _output.WriteLine($"  Processing time: {processingTimeMs:N0}ms ({processingTimeMs / 1000.0:F2}s)");
        _output.WriteLine($"  Processing speed: {wordsPerSecond:F2} words/second");
        _output.WriteLine($"  Segments created: {result.Count}");
        _output.WriteLine($"  Average segment size: {result.Average(s => s.Content.Length):F0} characters");
    }

    [Theory]
    [InlineData(5000)]   // 5k words
    [InlineData(15000)]  // 15k words  
    [InlineData(25000)]  // 25k words
    public async Task SegmentByTopicsAsync_WithVariousDocumentSizes_ScalesLinearly(int wordCount)
    {
        // Arrange
        var document = GenerateLargeMultiTopicDocument(wordCount);
        var options = new TopicSegmentationOptions { UseLlmEnhancement = false };

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await _service.SegmentByTopicsAsync(document, DocumentType.Generic, options);

        stopwatch.Stop();
        var processingTimeMs = stopwatch.ElapsedMilliseconds;
        var wordsPerSecond = (document.Split(' ').Length / (processingTimeMs / 1000.0));

        // Assert
        result.Should().NotBeEmpty();

        // Processing should scale reasonably with document size
        // Larger documents may be slightly less efficient due to complexity
        if (wordCount <= 10000)
        {
            wordsPerSecond.Should().BeGreaterThan(800);
        }
        else if (wordCount <= 20000)
        {
            wordsPerSecond.Should().BeGreaterThan(600);
        }
        else
        {
            wordsPerSecond.Should().BeGreaterThan(400);
        }

        _output.WriteLine($"Document Size Scaling Test ({wordCount} words):");
        _output.WriteLine($"  Processing time: {processingTimeMs:N0}ms");
        _output.WriteLine($"  Processing speed: {wordsPerSecond:F2} words/second");
        _output.WriteLine($"  Segments: {result.Count}");
    }

    #endregion

    #region Concurrent Processing Tests

    [Fact]
    public async Task SegmentByTopicsAsync_WithConcurrentRequests_HandlesParallelProcessing()
    {
        // Arrange
        var documentCount = 5;
        var documents = Enumerable.Range(0, documentCount)
            .Select(i => GenerateMultiTopicDocument($"Document{i}"))
            .ToList();

        var options = new TopicSegmentationOptions { UseLlmEnhancement = false };
        var stopwatch = Stopwatch.StartNew();

        // Act - Process documents concurrently
        var tasks = documents.Select(doc =>
            _service.SegmentByTopicsAsync(doc, DocumentType.Generic, options)
        ).ToList();

        var results = await Task.WhenAll(tasks);

        stopwatch.Stop();
        var totalProcessingTime = stopwatch.ElapsedMilliseconds;

        // Assert
        results.Should().HaveCount(documentCount);
        results.All(r => r != null && r.Any()).Should().BeTrue();

        // Concurrent processing should be more efficient than sequential
        // Each document would take ~200-500ms sequentially, so 5 documents = ~1000-2500ms
        // Concurrent processing should be significantly faster
        totalProcessingTime.Should().BeLessThan(2000,
            $"Concurrent processing took {totalProcessingTime}ms, should be <2000ms");

        // Verify no resource contention issues
        var allSegments = results.SelectMany(r => r).ToList();
        allSegments.Should().NotBeEmpty();
        allSegments.All(s => !string.IsNullOrWhiteSpace(s.Content)).Should().BeTrue();

        _output.WriteLine($"Concurrent Processing Test:");
        _output.WriteLine($"  Documents processed: {documentCount}");
        _output.WriteLine($"  Total time: {totalProcessingTime:N0}ms");
        _output.WriteLine($"  Average per document: {totalProcessingTime / (double)documentCount:F0}ms");
        _output.WriteLine($"  Total segments: {allSegments.Count}");
    }

    [Fact]
    public async Task SegmentByTopicsAsync_WithHighConcurrency_MaintainsPerformance()
    {
        // Arrange
        var documentCount = 10;
        var documents = Enumerable.Range(0, documentCount)
            .Select(i => GenerateMediumDocument(i))
            .ToList();

        var options = new TopicSegmentationOptions { UseLlmEnhancement = false };

        // Act - Process with high concurrency
        var stopwatch = Stopwatch.StartNew();

        var semaphore = new SemaphoreSlim(5); // Limit to 5 concurrent operations
        var tasks = documents.Select(async doc =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await _service.SegmentByTopicsAsync(doc, DocumentType.Generic, options);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert
        results.Should().HaveCount(documentCount);
        results.All(r => r != null && r.Any()).Should().BeTrue();

        var totalProcessingTime = stopwatch.ElapsedMilliseconds;
        var averageTimePerDocument = totalProcessingTime / (double)documentCount;

        // Should maintain reasonable performance under high concurrency
        averageTimePerDocument.Should().BeLessThan(1000,
            $"Average processing time was {averageTimePerDocument:F0}ms, should be <1000ms");

        _output.WriteLine($"High Concurrency Test:");
        _output.WriteLine($"  Documents: {documentCount}");
        _output.WriteLine($"  Total time: {totalProcessingTime:N0}ms");
        _output.WriteLine($"  Average per document: {averageTimePerDocument:F0}ms");
        _output.WriteLine($"  Total segments: {results.Sum(r => r.Count())}");
    }

    #endregion

    #region Memory Optimization Tests

    [Fact]
    public async Task SegmentByTopicsAsync_WithLargeDocument_HasReasonableMemoryUsage()
    {
        // Arrange
        var largeDocument = GenerateLargeMultiTopicDocument(20000);
        var options = new TopicSegmentationOptions { UseLlmEnhancement = false };

        // Measure memory before
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memoryBefore = GC.GetTotalMemory(false);

        // Act
        var result = await _service.SegmentByTopicsAsync(largeDocument, DocumentType.Generic, options);

        // Measure memory after
        var memoryAfter = GC.GetTotalMemory(false);
        var memoryUsed = memoryAfter - memoryBefore;

        // Assert
        result.Should().NotBeEmpty();

        // Memory usage should be reasonable (< 50MB for a 20k word document)
        var memoryUsedMB = memoryUsed / (1024.0 * 1024.0);
        memoryUsedMB.Should().BeLessThan(50,
            $"Memory usage was {memoryUsedMB:F2}MB, should be <50MB");

        // Memory per word should be reasonable
        var memoryPerWord = memoryUsed / (double)largeDocument.Split(' ').Length;
        memoryPerWord.Should().BeLessThan(1000, // Less than 1KB per word
            $"Memory per word was {memoryPerWord:F0} bytes, should be <1000 bytes");

        _output.WriteLine($"Memory Usage Test:");
        _output.WriteLine($"  Document size: {largeDocument.Length:N0} characters");
        _output.WriteLine($"  Memory used: {memoryUsedMB:F2}MB");
        _output.WriteLine($"  Memory per word: {memoryPerWord:F0} bytes");
        _output.WriteLine($"  Segments created: {result.Count()}");
    }

    [Fact]
    public async Task SegmentByTopicsAsync_WithMultipleIterations_DoesNotLeakMemory()
    {
        // Arrange
        var document = GenerateMultiTopicDocument("MemoryLeakTest");
        var options = new TopicSegmentationOptions { UseLlmEnhancement = false };
        var iterations = 10;

        // Baseline memory measurement
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baselineMemory = GC.GetTotalMemory(false);

        // Act - Multiple iterations
        for (int i = 0; i < iterations; i++)
        {
            var result = await _service.SegmentByTopicsAsync(document, DocumentType.Generic, options);
            result.Should().NotBeEmpty();
        }

        // Final memory measurement
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var finalMemory = GC.GetTotalMemory(false);

        var memoryIncrease = finalMemory - baselineMemory;
        var memoryIncreaseMB = memoryIncrease / (1024.0 * 1024.0);

        // Assert - Memory should not significantly increase
        memoryIncreaseMB.Should().BeLessThan(10,
            $"Memory increased by {memoryIncreaseMB:F2}MB after {iterations} iterations, should be <10MB");

        _output.WriteLine($"Memory Leak Test:");
        _output.WriteLine($"  Iterations: {iterations}");
        _output.WriteLine($"  Baseline memory: {baselineMemory / (1024.0 * 1024.0):F2}MB");
        _output.WriteLine($"  Final memory: {finalMemory / (1024.0 * 1024.0):F2}MB");
        _output.WriteLine($"  Memory increase: {memoryIncreaseMB:F2}MB");
    }

    #endregion

    #region Performance Benchmarking

    [Theory]
    [InlineData(1000, 100)]   // Small: 1k words, <100ms
    [InlineData(5000, 500)]   // Medium: 5k words, <500ms
    [InlineData(10000, 1000)] // Large: 10k words, <1000ms
    public async Task SegmentByTopicsAsync_PerformanceBenchmarks_MeetsTargets(int wordCount, int maxTimeMs)
    {
        // Arrange
        var document = GenerateLargeMultiTopicDocument(wordCount);
        var options = new TopicSegmentationOptions { UseLlmEnhancement = false };

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await _service.SegmentByTopicsAsync(document, DocumentType.Generic, options);
        stopwatch.Stop();

        // Assert
        result.Should().NotBeEmpty();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(maxTimeMs,
            $"Processing {wordCount} words took {stopwatch.ElapsedMilliseconds}ms, should be <{maxTimeMs}ms");

        var wordsPerSecond = (wordCount / (stopwatch.ElapsedMilliseconds / 1000.0));

        _output.WriteLine($"Benchmark ({wordCount} words):");
        _output.WriteLine($"  Time: {stopwatch.ElapsedMilliseconds}ms (target: <{maxTimeMs}ms)");
        _output.WriteLine($"  Speed: {wordsPerSecond:F0} words/second");
        _output.WriteLine($"  Segments: {result.Count()}");
    }

    [Fact]
    public async Task DetectTopicBoundariesAsync_PerformanceBenchmark_MeetsTargets()
    {
        // Arrange
        var document = GenerateLargeMultiTopicDocument(5000);
        var iterations = 5;
        var times = new List<long>();

        // Act - Multiple runs for consistent measurement
        for (int i = 0; i < iterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var boundaries = await _service.DetectTopicBoundariesAsync(document, DocumentType.Generic);
            stopwatch.Stop();

            boundaries.Should().NotBeNull();
            times.Add(stopwatch.ElapsedMilliseconds);
        }

        // Assert
        var averageTime = times.Average();
        var maxTime = times.Max();
        var minTime = times.Min();

        // Boundary detection should be fast (< 200ms average for 5k words)
        averageTime.Should().BeLessThan(200,
            $"Average boundary detection time was {averageTime:F0}ms, should be <200ms");

        _output.WriteLine($"Boundary Detection Benchmark (5k words, {iterations} runs):");
        _output.WriteLine($"  Average: {averageTime:F0}ms");
        _output.WriteLine($"  Min: {minTime}ms, Max: {maxTime}ms");
        _output.WriteLine($"  Std Dev: {Math.Sqrt(times.Select(t => Math.Pow(t - averageTime, 2)).Average()):F0}ms");
    }

    #endregion

    #region Helper Methods

    private void SetupDefaultMocks()
    {
        _mockPromptManager.Setup(x => x.GetPromptAsync(
                It.IsAny<SegmentationStrategy>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptTemplate
            {
                SystemPrompt = "You are a topic analysis expert.",
                UserPrompt = "Analyze the following content: {DocumentContent}",
                ExpectedFormat = "json"
            });

        _mockLlmService.Setup(x => x.TestConnectivityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private string GenerateLargeMultiTopicDocument(int approximateWordCount)
    {
        var topics = new[]
        {
            "technology and artificial intelligence development in modern software engineering",
            "environmental sustainability and climate change mitigation strategies worldwide",
            "economic market analysis and financial investment trends in global markets",
            "healthcare innovation and medical research breakthrough discoveries recently",
            "educational technology transformation and digital learning platform adoption",
            "transportation infrastructure and urban planning development initiatives",
            "renewable energy systems and sustainable power generation technologies",
            "social media impact and digital communication transformation in society"
        };

        var sb = new StringBuilder();
        var wordsAdded = 0;
        var topicIndex = 0;

        while (wordsAdded < approximateWordCount)
        {
            var topic = topics[topicIndex % topics.Length];
            var paragraph = GenerateTopicParagraph(topic, 150); // ~150 words per paragraph

            sb.AppendLine(paragraph);
            sb.AppendLine(); // Add spacing

            wordsAdded += paragraph.Split(' ').Length;
            topicIndex++;
        }

        return sb.ToString().Trim();
    }

    private string GenerateMultiTopicDocument(string identifier)
    {
        return $@"
            Technology Advances in {identifier}
            
            The field of technology has seen remarkable advances in recent years. Machine learning
            and artificial intelligence are transforming how we approach complex problems. Cloud
            computing platforms enable scalable solutions for businesses of all sizes.
            
            Environmental Considerations for {identifier}
            
            Climate change remains a critical challenge requiring immediate attention. Renewable
            energy sources are becoming more cost-effective and widely adopted. Sustainable
            practices in manufacturing and transportation are essential for our future.
            
            Economic Implications of {identifier}
            
            Market volatility affects investment strategies across all sectors. Digital currencies
            and blockchain technologies are reshaping financial systems. International trade
            policies influence global economic stability and growth patterns.
            
            Healthcare Innovation in {identifier}
            
            Medical research continues to produce breakthrough treatments and diagnostic tools.
            Telemedicine has revolutionized patient care delivery, especially in remote areas.
            Precision medicine approaches are personalizing treatment plans for better outcomes.
        ";
    }

    private string GenerateMediumDocument(int index)
    {
        return $@"
            Document {index}: Comprehensive Analysis
            
            This document explores various aspects of modern technological development.
            The integration of artificial intelligence into everyday applications has
            created new opportunities for innovation and efficiency improvements.
            
            Market trends indicate significant growth in the technology sector.
            Investment in research and development continues to drive breakthrough
            discoveries that benefit society as a whole.
            
            Environmental impact remains a key consideration in all development
            initiatives. Sustainable practices and green technologies are becoming
            standard requirements for modern projects and implementations.
        ";
    }

    private string GenerateTopicParagraph(string topic, int approximateWordCount)
    {
        var sentences = new[]
        {
            $"The field of {topic} has experienced significant developments recently.",
            $"Research in {topic} continues to yield important insights and breakthroughs.",
            $"Industry experts agree that {topic} will play a crucial role in future innovations.",
            $"Current trends in {topic} suggest continued growth and advancement opportunities.",
            $"The application of {topic} across various sectors demonstrates its versatility.",
            $"Challenges in {topic} require collaborative efforts from multiple disciplines.",
            $"Investment in {topic} infrastructure supports long-term strategic objectives.",
            $"The integration of {topic} with existing systems creates synergistic benefits.",
            $"Future developments in {topic} depend on continued research and development.",
            $"Stakeholders in {topic} emphasize the importance of sustainable practices."
        };

        var sb = new StringBuilder();
        var wordsAdded = 0;
        var sentenceIndex = 0;

        while (wordsAdded < approximateWordCount)
        {
            var sentence = sentences[sentenceIndex % sentences.Length];
            sb.Append(sentence).Append(" ");
            wordsAdded += sentence.Split(' ').Length;
            sentenceIndex++;
        }

        return sb.ToString().Trim();
    }

    #endregion
}
