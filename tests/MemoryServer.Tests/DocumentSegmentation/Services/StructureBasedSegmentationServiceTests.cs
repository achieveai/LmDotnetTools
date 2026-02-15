using FluentAssertions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace MemoryServer.DocumentSegmentation.Tests.Services;

/// <summary>
///     Tests for StructureBasedSegmentationService functionality.
/// </summary>
public class StructureBasedSegmentationServiceTests
{
    private readonly ILogger<StructureBasedSegmentationService> _logger;
    private readonly Mock<ILlmProviderIntegrationService> _mockLlmService;
    private readonly Mock<ISegmentationPromptManager> _mockPromptManager;
    private readonly StructureBasedSegmentationService _service;

    public StructureBasedSegmentationServiceTests()
    {
        _mockLlmService = new Mock<ILlmProviderIntegrationService>();
        _mockPromptManager = new Mock<ISegmentationPromptManager>();
        _logger = new LoggerFactory().CreateLogger<StructureBasedSegmentationService>();

        _service = new StructureBasedSegmentationService(_mockLlmService.Object, _mockPromptManager.Object, _logger);

        SetupDefaultMocks();
    }

    private void SetupDefaultMocks()
    {
        // Setup default prompt manager response
        _ = _mockPromptManager
            .Setup(x =>
                x.GetPromptAsync(It.IsAny<SegmentationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new PromptTemplate
                {
                    SystemPrompt = "You are a structure analysis expert.",
                    UserPrompt = "Analyze the following content for structural boundaries: {DocumentContent}",
                    ExpectedFormat = "json",
                    Metadata = new Dictionary<string, object>
                    {
                        ["strategy"] = SegmentationStrategy.StructureBased.ToString(),
                        ["language"] = "en",
                    },
                }
            );

        // Setup default LLM service responses
        _ = _mockLlmService.Setup(x => x.TestConnectivityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Setup strategy analysis mock
        _ = _mockLlmService
            .Setup(x =>
                x.AnalyzeOptimalStrategyAsync(
                    It.IsAny<string>(),
                    It.IsAny<DocumentType>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new StrategyRecommendation
                {
                    Strategy = SegmentationStrategy.StructureBased,
                    Confidence = 0.8,
                    Reasoning = "Mock reasoning",
                }
            );
    }

    #region AnalyzeHierarchicalStructureAsync Tests

    [Fact]
    public async Task AnalyzeHierarchicalStructureAsync_WithWellStructuredDocument_ReturnsAccurateAnalysis()
    {
        // Arrange
        var content =
            @"
# Introduction
This document is well structured.

## Background
Some background information.

## Methodology
Description of methods.

### Data Collection
How data was collected.

### Analysis
How analysis was performed.

## Results
The results section.

# Conclusion
Final thoughts.
";

        // Act
        var result = await _service.AnalyzeHierarchicalStructureAsync(content);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.MaxHeadingDepth.Should().Be(3);
        _ = result.TotalHeadings.Should().BeGreaterThan(5);
        _ = result.HasClearHierarchy.Should().BeTrue();
        _ = result.DocumentOutline.Should().NotBeEmpty();
        _ = result.StructuralPatterns.Should().Contain("markdown_headings");
    }

    #endregion

    #region ValidateStructureSegmentsAsync Tests

    [Fact]
    public async Task ValidateStructureSegmentsAsync_WithGoodSegments_ReturnsHighQuality()
    {
        // Arrange
        var segments = new List<DocumentSegment>
        {
            new()
            {
                Id = "seg1",
                Content =
                    "# Introduction\nThis is a well-structured introduction section with clear heading and substantial content.",
                SequenceNumber = 0,
                Metadata = new Dictionary<string, object>
                {
                    ["structural_element_type"] = StructuralElementType.Heading.ToString(),
                    ["heading_level"] = 1,
                },
            },
            new()
            {
                Id = "seg2",
                Content =
                    "## Methodology\nThis section describes the methodology used in detail with proper structure and formatting.",
                SequenceNumber = 1,
                Metadata = new Dictionary<string, object>
                {
                    ["structural_element_type"] = StructuralElementType.Heading.ToString(),
                    ["heading_level"] = 2,
                },
            },
        };

        // Act
        var result = await _service.ValidateStructureSegmentsAsync(segments, "Original content");

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.OverallQuality.Should().BeGreaterThan(0.5);
        _ = result.StructuralClarity.Should().BeGreaterThan(0.5);
        _ = result.SegmentResults.Should().HaveCount(2);
    }

    #endregion

    #region Helper Methods

    private static string CreateStructuredDocument()
    {
        return @"
# Document Title
This is a well-structured document with clear hierarchical organization.

## Section 1: Introduction
This section introduces the main topic and provides necessary background information for understanding the content that follows.

### Subsection 1.1: Background
Here we provide detailed background information about the topic, including historical context and relevant prior work.

### Subsection 1.2: Objectives
This subsection outlines the main objectives and goals of this document.

## Section 2: Main Content
This is the main content section where we present the core information and analysis.

### Subsection 2.1: Analysis
Detailed analysis and discussion of the main points.

### Subsection 2.2: Examples
Concrete examples and case studies to illustrate the concepts.

## Section 3: Conclusion
This section summarizes the key points and provides final thoughts on the topic.
";
    }

    #endregion

    #region SegmentByStructureAsync Tests

    [Fact]
    public async Task SegmentByStructureAsync_WithValidContent_ReturnsStructureSegments()
    {
        // Arrange
        var content = CreateStructuredDocument();
        var options = new StructureSegmentationOptions
        {
            MinSegmentSize = 50,
            MaxHeadingDepth = 3,
            UseLlmEnhancement = false, // Disable for simpler testing
        };

        // Act
        var result = await _service.SegmentByStructureAsync(content, DocumentType.Generic, options);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.Should().NotBeEmpty();

        foreach (var segment in result)
        {
            _ = segment.Content.Length.Should().BeGreaterOrEqualTo(options.MinSegmentSize);
            _ = segment.Metadata.Should().ContainKey("segmentation_strategy");
            _ = segment.Metadata["segmentation_strategy"].Should().Be(SegmentationStrategy.StructureBased.ToString());
        }
    }

    [Fact]
    public async Task SegmentByStructureAsync_WithMarkdownHeadings_DetectsStructuralBoundaries()
    {
        // Arrange
        var content =
            @"
# Introduction
This is the introduction section with some content.

## Background
This section provides background information about the topic.

### Methodology
Here we describe the methodology used in this study.

## Results
This section presents the results of our analysis.

# Conclusion
This is the conclusion section.
";

        var options = new StructureSegmentationOptions
        {
            MinSegmentSize = 20,
            MaxHeadingDepth = 3,
            UseLlmEnhancement = false,
            MergeSmallSections = false, // Disable merging to test pure structural detection
        };

        // Act
        var result = await _service.SegmentByStructureAsync(content, DocumentType.ResearchPaper, options);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().HaveCountGreaterOrEqualTo(3); // Should detect major structural sections

        // Check that segments have appropriate structural metadata
        foreach (var segment in result)
        {
            _ = segment.Metadata.Should().ContainKey("structure_based");
            _ = segment.Metadata["structure_based"].Should().Be(true);
        }
    }

    [Fact]
    public async Task SegmentByStructureAsync_WithSmallSections_MergesSections()
    {
        // Arrange
        var content =
            @"
# Title
Short.

## Section 1
Tiny.

## Section 2
Also tiny.

# Main Content
This is a longer section with substantial content that should remain as its own segment.
";

        var options = new StructureSegmentationOptions
        {
            MinSegmentSize = 50,
            MergeSmallSections = true,
            MinSectionSizeForMerging = 30,
        };

        // Act
        var result = await _service.SegmentByStructureAsync(content, DocumentType.Generic, options);

        // Assert
        _ = result.Should().NotBeEmpty();

        // Check that small sections were merged or filtered out
        foreach (var segment in result)
        {
            _ = segment.Content.Length.Should().BeGreaterOrEqualTo(options.MinSegmentSize);
        }
    }

    #endregion

    #region DetectStructuralBoundariesAsync Tests

    [Fact]
    public async Task DetectStructuralBoundariesAsync_WithMarkdownHeadings_DetectsHeadingBoundaries()
    {
        // Arrange
        var content =
            @"
# Level 1 Heading
Content here.

## Level 2 Heading
More content.

### Level 3 Heading
Even more content.
";

        // Act
        var result = await _service.DetectStructuralBoundariesAsync(content);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().Contain(b => b.ElementType == StructuralElementType.Heading);
        _ = result.Should().Contain(b => b.HeadingLevel == 1);
        _ = result.Should().Contain(b => b.HeadingLevel == 2);
        _ = result.Should().Contain(b => b.HeadingLevel == 3);
    }

    [Fact]
    public async Task DetectStructuralBoundariesAsync_WithSectionBreaks_DetectsSectionBreaks()
    {
        // Arrange
        var content =
            @"
First section content here.

---

Second section content here.

===

Third section content here.
";

        // Act
        var result = await _service.DetectStructuralBoundariesAsync(content);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().Contain(b => b.ElementType == StructuralElementType.SectionBreak);
    }

    #endregion
}
