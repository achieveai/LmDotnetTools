using FluentAssertions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace MemoryServer.DocumentSegmentation.Tests.Services;

/// <summary>
/// Tests for NarrativeBasedSegmentationService functionality.
/// </summary>
public class NarrativeBasedSegmentationServiceTests
{
    private readonly Mock<ILlmProviderIntegrationService> _mockLlmService;
    private readonly Mock<ISegmentationPromptManager> _mockPromptManager;
    private readonly ILogger<NarrativeBasedSegmentationService> _logger;
    private readonly NarrativeBasedSegmentationService _service;

    public NarrativeBasedSegmentationServiceTests()
    {
        _mockLlmService = new Mock<ILlmProviderIntegrationService>();
        _mockPromptManager = new Mock<ISegmentationPromptManager>();
        _logger = new LoggerFactory().CreateLogger<NarrativeBasedSegmentationService>();

        _service = new NarrativeBasedSegmentationService(_mockLlmService.Object, _mockPromptManager.Object, _logger);

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
                    SystemPrompt = "You are a narrative analysis expert.",
                    UserPrompt = "Analyze the following content for narrative flow: {DocumentContent}",
                    ExpectedFormat = "json",
                    Metadata = new Dictionary<string, object>
                    {
                        ["strategy"] = SegmentationStrategy.NarrativeBased.ToString(),
                        ["language"] = "en",
                    },
                }
            );

        // Setup default LLM service responses
        _ = _mockLlmService.Setup(x => x.TestConnectivityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    #region SegmentByNarrativeAsync Tests

    [Fact]
    public async Task SegmentByNarrativeAsync_WithValidContent_ReturnsNarrativeSegments()
    {
        // Arrange
        var content = CreateNarrativeDocument();
        var options = new NarrativeSegmentationOptions
        {
            MinSegmentSize = 50,
            UseLlmEnhancement = false, // Disable for simpler testing
        };

        // Act
        var result = await _service.SegmentByNarrativeAsync(content, DocumentType.Generic, options);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.Should().NotBeEmpty();

        foreach (var segment in result)
        {
            _ = segment.Content.Length.Should().BeGreaterOrEqualTo(options.MinSegmentSize);
            _ = segment.Metadata.Should().ContainKey("segmentation_strategy");
            _ = segment.Metadata["segmentation_strategy"].Should().Be(SegmentationStrategy.NarrativeBased.ToString());
        }
    }

    [Fact]
    public async Task SegmentByNarrativeAsync_WithTemporalMarkers_DetectsNarrativeBoundaries()
    {
        // Arrange
        var content =
            @"
First, we need to understand the problem. The issue began last month when users started reporting errors.

Then, we investigated the root cause. After careful analysis, we discovered the problem was in the database connection.

Finally, we implemented a solution. The fix involved updating the connection string and restarting the service.
";

        var options = new NarrativeSegmentationOptions { MinSegmentSize = 20, DetectTemporalSequences = true };

        // Act
        var result = await _service.SegmentByNarrativeAsync(content, DocumentType.Documentation, options);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().HaveCountGreaterOrEqualTo(2); // Should detect temporal progression

        // Check that segments have appropriate narrative metadata
        foreach (var segment in result)
        {
            _ = segment.Metadata.Should().ContainKey("narrative_based");
            _ = segment.Metadata["narrative_based"].Should().Be(true);
        }
    }

    [Fact]
    public async Task SegmentByNarrativeAsync_WithCausalRelationships_DetectsCausalBoundaries()
    {
        // Arrange
        var content =
            @"
The server was running slowly because the database queries were inefficient.

As a result, users experienced long loading times and timeouts.

Therefore, we decided to optimize the database indexes.

Consequently, the performance improved significantly after the changes.
";

        var options = new NarrativeSegmentationOptions { MinSegmentSize = 20, AnalyzeCausalRelationships = true };

        // Act
        var result = await _service.SegmentByNarrativeAsync(content, DocumentType.Documentation, options);

        // Assert
        _ = result.Should().NotBeEmpty();

        // Check for causal relationship metadata
        var segmentWithCausal = result.FirstOrDefault(s =>
            s.Metadata.ContainsKey("transition_type") && s.Metadata["transition_type"].ToString() == "Causal"
        );

        _ = segmentWithCausal.Should().NotBeNull();
    }

    #endregion

    #region DetectNarrativeTransitionsAsync Tests

    [Fact]
    public async Task DetectNarrativeTransitionsAsync_WithTemporalMarkers_DetectsTemporalBoundaries()
    {
        // Arrange
        var content =
            @"
Initially, the project was just an idea. Then, we started planning the implementation.
Next, we began coding the solution. Finally, we tested and deployed the application.
";

        // Act
        var result = await _service.DetectNarrativeTransitionsAsync(content, DocumentType.Generic);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().Contain(b => b.TransitionType == NarrativeTransitionType.Temporal);
        _ = result
            .Should()
            .Contain(b => b.TriggerPhrases.Any(p => p.Equals("initially", StringComparison.OrdinalIgnoreCase)));
        _ = result.Should().Contain(b => b.TriggerPhrases.Any(p => p.Equals("then", StringComparison.OrdinalIgnoreCase)));
        _ = result
            .Should()
            .Contain(b => b.TriggerPhrases.Any(p => p.Equals("finally", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DetectNarrativeTransitionsAsync_WithCausalMarkers_DetectsCausalBoundaries()
    {
        // Arrange
        var content =
            @"
The application crashed because of a memory leak.
Therefore, we had to restart the server.
As a result, all user sessions were lost.
";

        // Act
        var result = await _service.DetectNarrativeTransitionsAsync(content, DocumentType.Documentation);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().Contain(b => b.TransitionType == NarrativeTransitionType.Causal);
        _ = result
            .Should()
            .Contain(b => b.TriggerPhrases.Any(p => p.Equals("because", StringComparison.OrdinalIgnoreCase)));
        _ = result
            .Should()
            .Contain(b => b.TriggerPhrases.Any(p => p.Equals("therefore", StringComparison.OrdinalIgnoreCase)));
    }

    #endregion

    #region AnalyzeLogicalFlowAsync Tests

    [Fact]
    public async Task AnalyzeLogicalFlowAsync_WithSequentialContent_ReturnsSequentialNarrativeType()
    {
        // Arrange
        var content =
            @"
First, we analyze the requirements.
Then, we design the solution.
Next, we implement the code.
Finally, we test and deploy.
";

        // Act
        var result = await _service.AnalyzeLogicalFlowAsync(content);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.OverallNarrativeType.Should().Be(NarrativeType.Sequential);
        _ = result.TemporalProgression.Should().Be(TemporalProgression.Linear);
        _ = result.FlowCoherence.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public async Task AnalyzeLogicalFlowAsync_WithCausalContent_ReturnsCausalNarrativeType()
    {
        // Arrange
        var content =
            @"
The bug occurred because of invalid input validation.
This caused the application to crash unexpectedly.
As a result, users lost their work.
Therefore, we need to improve error handling.
";

        // Act
        var result = await _service.AnalyzeLogicalFlowAsync(content);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.OverallNarrativeType.Should().Be(NarrativeType.Causal);
        _ = result.CausalChain.Should().NotBeEmpty();
        _ = result.LogicalConsistency.Should().BeGreaterThan(0.5);
    }

    #endregion

    #region IdentifyTemporalSequencesAsync Tests

    [Fact]
    public async Task IdentifyTemporalSequencesAsync_WithTemporalMarkers_ReturnsTemporalSequences()
    {
        // Arrange
        var content =
            @"
First, we gather requirements. Then, we create designs.
Next, we write code. Finally, we perform testing.
";

        // Act
        var result = await _service.IdentifyTemporalSequencesAsync(content);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().Contain(s => s.TemporalMarkers.Contains("first"));
        _ = result.Should().Contain(s => s.TemporalMarkers.Contains("then"));
        _ = result.Should().Contain(s => s.TemporalMarkers.Contains("next"));
        _ = result.Should().Contain(s => s.TemporalMarkers.Contains("finally"));

        // Sequences should be in order
        _ = result.Should().BeInAscendingOrder(s => s.SequentialOrder);
    }

    #endregion

    #region DetectCausalRelationshipsAsync Tests

    [Fact]
    public async Task DetectCausalRelationshipsAsync_WithCausalMarkers_ReturnsCausalRelations()
    {
        // Arrange
        var content =
            @"
The server failed because of insufficient memory.
Therefore, the application became unresponsive.
As a result, users experienced timeouts.
";

        // Act
        var result = await _service.DetectCausalRelationshipsAsync(content);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().Contain(r => r.CausalIndicator.Equals("because", StringComparison.OrdinalIgnoreCase));
        _ = result.Should().Contain(r => r.CausalIndicator.Equals("therefore", StringComparison.OrdinalIgnoreCase));
        _ = result.Should().Contain(r => r.CausalIndicator.Equals("as a result", StringComparison.OrdinalIgnoreCase));

        foreach (var relation in result)
        {
            _ = relation.Strength.Should().BeGreaterThan(0);
            _ = relation.Strength.Should().BeLessOrEqualTo(1);
        }
    }

    #endregion

    #region IdentifyNarrativeArcElementsAsync Tests

    [Fact]
    public async Task IdentifyNarrativeArcElementsAsync_WithNarrativeStructure_ReturnsArcElements()
    {
        // Arrange
        var content =
            @"
Introduction: This document explains the problem we faced.
Background: The issue started when our server capacity reached its limits.
Development: We explored several solutions to address the scaling challenges.
Resolution: Finally, we implemented a load balancing solution that resolved the issue.
";

        // Act
        var result = await _service.IdentifyNarrativeArcElementsAsync(content, DocumentType.Documentation);

        // Assert
        _ = result.Should().NotBeEmpty();
        _ = result.Should().ContainKey(NarrativeFunction.Setup);
        _ = result.Should().ContainKey(NarrativeFunction.Background);
        _ = result.Should().ContainKey(NarrativeFunction.Development);
        _ = result.Should().ContainKey(NarrativeFunction.Resolution);

        foreach (var element in result)
        {
            _ = element.Value.Should().NotBeEmpty();
        }
    }

    #endregion

    #region ValidateNarrativeSegmentsAsync Tests

    [Fact]
    public async Task ValidateNarrativeSegmentsAsync_WithGoodSegments_ReturnsHighQuality()
    {
        // Arrange
        var segments = new List<DocumentSegment>
        {
            new DocumentSegment
            {
                Id = "seg1",
                Content =
                    "First, we need to understand the problem. The issue began when users started reporting errors.",
                SequenceNumber = 0,
                Metadata = new Dictionary<string, object>
                {
                    ["transition_type"] = NarrativeTransitionType.Temporal.ToString(),
                    ["narrative_function"] = NarrativeFunction.Setup.ToString(),
                },
            },
            new DocumentSegment
            {
                Id = "seg2",
                Content =
                    "Then, we investigated the cause. After analysis, we found the root issue was in the database.",
                SequenceNumber = 1,
                Metadata = new Dictionary<string, object>
                {
                    ["transition_type"] = NarrativeTransitionType.Temporal.ToString(),
                    ["narrative_function"] = NarrativeFunction.Development.ToString(),
                },
            },
        };

        // Act
        var result = await _service.ValidateNarrativeSegmentsAsync(segments, "Original content");

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.OverallQuality.Should().BeGreaterThan(0.4);
        _ = result.FlowCoherence.Should().BeGreaterThan(0.3);
        _ = result.SegmentResults.Should().HaveCount(2);
    }

    #endregion

    #region Helper Methods

    private static string CreateNarrativeDocument()
    {
        return @"
Introduction: This document describes our journey to solve a critical performance issue.

First, we noticed that user complaints were increasing. The application was becoming slower each day.

Then, we began investigating the problem. We analyzed server logs and discovered unusual patterns.

Next, we identified the root cause. The database queries were inefficient due to missing indexes.

However, implementing the fix required careful planning. We couldn't afford downtime during business hours.

Therefore, we scheduled the maintenance for the weekend. The team worked together to implement the solution.

Finally, the performance improved dramatically. Users reported faster loading times and better responsiveness.

In conclusion, this experience taught us the importance of proactive monitoring and regular performance reviews.
";
    }

    #endregion
}
