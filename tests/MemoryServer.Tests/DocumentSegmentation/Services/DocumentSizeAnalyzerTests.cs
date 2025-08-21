using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using FluentAssertions;

namespace MemoryServer.DocumentSegmentation.Tests.Services;

/// <summary>
/// Unit tests for DocumentSizeAnalyzer service.
/// </summary>
public class DocumentSizeAnalyzerTests
{
    private readonly ILogger<DocumentSizeAnalyzer> _logger;
    private readonly DocumentSegmentationOptions _options;
    private readonly DocumentSizeAnalyzer _analyzer;

    public DocumentSizeAnalyzerTests()
    {
        _logger = new LoggerFactory().CreateLogger<DocumentSizeAnalyzer>();
        _options = new DocumentSegmentationOptions
        {
            Thresholds = new SegmentationThresholds
            {
                MinDocumentSizeWords = 1500,
                MaxDocumentSizeWords = 50000,
                TargetSegmentSizeWords = 1000,
                MaxSegmentSizeWords = 2000,
                MinSegmentSizeWords = 100
            }
        };

        var optionsWrapper = Options.Create(_options);
        _analyzer = new DocumentSizeAnalyzer(_logger, optionsWrapper);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_WithValidContent_ReturnsCorrectStatistics()
    {
        // Arrange
        var content = "This is a test document with multiple sentences. " +
                     "It contains several words and punctuation marks! " +
                     "We want to verify that our analysis works correctly? " +
                     "This should provide good test coverage for the analyzer.\n\n" +
                     "Here is a second paragraph to test paragraph counting. " +
                     "It also has multiple sentences for testing.";

        // Act
        var result = await _analyzer.AnalyzeDocumentAsync(content);

        // Assert
        result.Should().NotBeNull();
        result.CharacterCount.Should().Be(content.Length);
        result.WordCount.Should().BeGreaterThan(0);
        result.SentenceCount.Should().BeGreaterThan(0);
        result.ParagraphCount.Should().Be(2); // Two paragraphs separated by \n\n
        result.TokenCount.Should().BeGreaterThan(0);

        // Token count should be roughly 1/4 of character count
        result.TokenCount.Should().BeInRange(content.Length / 5, content.Length / 3);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_WithEmptyContent_ReturnsZeroStatistics()
    {
        // Arrange
        var content = "";

        // Act
        var result = await _analyzer.AnalyzeDocumentAsync(content);

        // Assert
        result.Should().NotBeNull();
        result.CharacterCount.Should().Be(0);
        result.WordCount.Should().Be(0);
        result.SentenceCount.Should().Be(0);
        result.ParagraphCount.Should().Be(1); // Default count
        result.TokenCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_WithWhitespaceOnly_ReturnsZeroStatistics()
    {
        // Arrange
        var content = "   \n\n\t  ";

        // Act
        var result = await _analyzer.AnalyzeDocumentAsync(content);

        // Assert
        result.Should().NotBeNull();
        result.CharacterCount.Should().Be(content.Length);
        result.WordCount.Should().Be(0);
        result.SentenceCount.Should().Be(0);
        result.TokenCount.Should().Be(0);
    }

    [Theory]
    [InlineData(500, DocumentType.Generic, false)] // Below threshold
    [InlineData(1500, DocumentType.Generic, true)] // At threshold
    [InlineData(2000, DocumentType.Generic, true)] // Above threshold
    [InlineData(300, DocumentType.Email, true)] // Email has lower threshold
    [InlineData(200, DocumentType.Chat, true)] // Chat has even lower threshold
    [InlineData(1000, DocumentType.ResearchPaper, false)] // Research paper has higher threshold
    public void ShouldSegmentDocument_WithVariousWordCounts_ReturnsExpectedResult(
      int wordCount, DocumentType documentType, bool expectedResult)
    {
        // Arrange
        var statistics = new DocumentStatistics { WordCount = wordCount };

        // Act
        var result = _analyzer.ShouldSegmentDocument(statistics, documentType);

        // Assert
        result.Should().Be(expectedResult);
    }

    [Theory]
    [InlineData(1000, 1000, 2000, 1)] // Small document
    [InlineData(2500, 1000, 2000, 3)] // Medium document
    [InlineData(5000, 1000, 2000, 5)] // Large document
    [InlineData(1500, 1000, 2000, 2)] // Edge case
    public void CalculateOptimalSegmentCount_WithVariousInputs_ReturnsValidCount(
      int wordCount, int targetSize, int maxSize, int expectedCount)
    {
        // Arrange
        var statistics = new DocumentStatistics { WordCount = wordCount };

        // Act
        var result = _analyzer.CalculateOptimalSegmentCount(statistics, targetSize, maxSize);

        // Assert
        result.Should().Be(expectedCount);
        result.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(SegmentationStrategy.TopicBased)]
    [InlineData(SegmentationStrategy.StructureBased)]
    [InlineData(SegmentationStrategy.NarrativeBased)]
    [InlineData(SegmentationStrategy.Hybrid)]
    [InlineData(SegmentationStrategy.Custom)]
    public void EstimateProcessingTime_WithDifferentStrategies_ReturnsPositiveTime(
      SegmentationStrategy strategy)
    {
        // Arrange
        var statistics = new DocumentStatistics { WordCount = 1000 };

        // Act
        var result = _analyzer.EstimateProcessingTime(statistics, strategy);

        // Assert
        result.Should().BeGreaterThan(0);
        result.Should().BeLessThan(60000); // Less than 1 minute for 1000 words
    }

    [Fact]
    public void EstimateProcessingTime_WithLlmEnabled_IncludesOverhead()
    {
        // Arrange
        var statistics = new DocumentStatistics { WordCount = 100 };
        _options.LlmOptions.EnableLlmSegmentation = true;

        // Act
        var result = _analyzer.EstimateProcessingTime(statistics, SegmentationStrategy.Hybrid);

        // Assert
        result.Should().BeGreaterThan(5000); // Should include LLM overhead
    }

    [Fact]
    public void EstimateProcessingTime_WithLlmDisabled_ExcludesOverhead()
    {
        // Arrange
        var statistics = new DocumentStatistics { WordCount = 100 };
        _options.LlmOptions.EnableLlmSegmentation = false;

        // Act
        var result = _analyzer.EstimateProcessingTime(statistics, SegmentationStrategy.StructureBased);

        // Assert
        result.Should().BeLessThan(1000); // Should not include LLM overhead
    }
}
