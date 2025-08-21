using FluentAssertions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

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

        _service = new NarrativeBasedSegmentationService(
            _mockLlmService.Object,
            _mockPromptManager.Object,
            _logger);

        SetupDefaultMocks();
    }

    private void SetupDefaultMocks()
    {
        // Setup default prompt manager response
        _mockPromptManager.Setup(x => x.GetPromptAsync(
                It.IsAny<SegmentationStrategy>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptTemplate
            {
                SystemPrompt = "You are a narrative analysis expert.",
                UserPrompt = "Analyze the following content for narrative flow: {DocumentContent}",
                ExpectedFormat = "json",
                Metadata = new Dictionary<string, object>
                {
                    ["strategy"] = SegmentationStrategy.NarrativeBased.ToString(),
                    ["language"] = "en"
                }
            });

        // Setup default LLM service responses
        _mockLlmService.Setup(x => x.TestConnectivityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
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
            UseLlmEnhancement = false // Disable for simpler testing
        };

        // Act
        var result = await _service.SegmentByNarrativeAsync(content, DocumentType.Generic, options);

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();

        foreach (var segment in result)
        {
            segment.Content.Length.Should().BeGreaterOrEqualTo(options.MinSegmentSize);
            segment.Metadata.Should().ContainKey("segmentation_strategy");
            segment.Metadata["segmentation_strategy"].Should().Be(SegmentationStrategy.NarrativeBased.ToString());
        }
    }

    [Fact]
    public async Task SegmentByNarrativeAsync_WithTemporalMarkers_DetectsNarrativeBoundaries()
    {
        // Arrange
        var content = @"
First, we need to understand the problem. The issue began last month when users started reporting errors.

Then, we investigated the root cause. After careful analysis, we discovered the problem was in the database connection.

Finally, we implemented a solution. The fix involved updating the connection string and restarting the service.
";

        var options = new NarrativeSegmentationOptions
        {
            MinSegmentSize = 20,
            DetectTemporalSequences = true
        };

        // Act
        var result = await _service.SegmentByNarrativeAsync(content, DocumentType.Documentation, options);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().HaveCountGreaterOrEqualTo(2); // Should detect temporal progression

        // Check that segments have appropriate narrative metadata
        foreach (var segment in result)
        {
            segment.Metadata.Should().ContainKey("narrative_based");
            segment.Metadata["narrative_based"].Should().Be(true);
        }
    }

    [Fact]
    public async Task SegmentByNarrativeAsync_WithCausalRelationships_DetectsCausalBoundaries()
    {
        // Arrange
        var content = @"
The server was running slowly because the database queries were inefficient.

As a result, users experienced long loading times and timeouts.

Therefore, we decided to optimize the database indexes.

Consequently, the performance improved significantly after the changes.
";

        var options = new NarrativeSegmentationOptions
        {
            MinSegmentSize = 20,
            AnalyzeCausalRelationships = true
        };

        // Act
        var result = await _service.SegmentByNarrativeAsync(content, DocumentType.Documentation, options);

        // Assert
        result.Should().NotBeEmpty();

        // Check for causal relationship metadata
        var segmentWithCausal = result.FirstOrDefault(s =>
            s.Metadata.ContainsKey("transition_type") &&
            s.Metadata["transition_type"].ToString() == "Causal");

        segmentWithCausal.Should().NotBeNull();
    }

    #endregion

    #region DetectNarrativeTransitionsAsync Tests

    [Fact]
    public async Task DetectNarrativeTransitionsAsync_WithTemporalMarkers_DetectsTemporalBoundaries()
    {
        // Arrange
        var content = @"
Initially, the project was just an idea. Then, we started planning the implementation.
Next, we began coding the solution. Finally, we tested and deployed the application.
";

        // Act
        var result = await _service.DetectNarrativeTransitionsAsync(content, DocumentType.Generic);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(b => b.TransitionType == NarrativeTransitionType.Temporal);
        result.Should().Contain(b => b.TriggerPhrases.Any(p => p.Equals("initially", StringComparison.OrdinalIgnoreCase)));
        result.Should().Contain(b => b.TriggerPhrases.Any(p => p.Equals("then", StringComparison.OrdinalIgnoreCase)));
        result.Should().Contain(b => b.TriggerPhrases.Any(p => p.Equals("finally", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DetectNarrativeTransitionsAsync_WithCausalMarkers_DetectsCausalBoundaries()
    {
        // Arrange
        var content = @"
The application crashed because of a memory leak.
Therefore, we had to restart the server.
As a result, all user sessions were lost.
";

        // Act
        var result = await _service.DetectNarrativeTransitionsAsync(content, DocumentType.Documentation);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(b => b.TransitionType == NarrativeTransitionType.Causal);
        result.Should().Contain(b => b.TriggerPhrases.Any(p => p.Equals("because", StringComparison.OrdinalIgnoreCase)));
        result.Should().Contain(b => b.TriggerPhrases.Any(p => p.Equals("therefore", StringComparison.OrdinalIgnoreCase)));
    }

    #endregion

    #region AnalyzeLogicalFlowAsync Tests

    [Fact]
    public async Task AnalyzeLogicalFlowAsync_WithSequentialContent_ReturnsSequentialNarrativeType()
    {
        // Arrange
        var content = @"
First, we analyze the requirements.
Then, we design the solution.
Next, we implement the code.
Finally, we test and deploy.
";

        // Act
        var result = await _service.AnalyzeLogicalFlowAsync(content);

        // Assert
        result.Should().NotBeNull();
        result.OverallNarrativeType.Should().Be(NarrativeType.Sequential);
        result.TemporalProgression.Should().Be(TemporalProgression.Linear);
        result.FlowCoherence.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public async Task AnalyzeLogicalFlowAsync_WithCausalContent_ReturnsCausalNarrativeType()
    {
        // Arrange
        var content = @"
The bug occurred because of invalid input validation.
This caused the application to crash unexpectedly.
As a result, users lost their work.
Therefore, we need to improve error handling.
";

        // Act
        var result = await _service.AnalyzeLogicalFlowAsync(content);

        // Assert
        result.Should().NotBeNull();
        result.OverallNarrativeType.Should().Be(NarrativeType.Causal);
        result.CausalChain.Should().NotBeEmpty();
        result.LogicalConsistency.Should().BeGreaterThan(0.5);
    }

    #endregion

    #region IdentifyTemporalSequencesAsync Tests

    [Fact]
    public async Task IdentifyTemporalSequencesAsync_WithTemporalMarkers_ReturnsTemporalSequences()
    {
        // Arrange
        var content = @"
First, we gather requirements. Then, we create designs.
Next, we write code. Finally, we perform testing.
";

        // Act
        var result = await _service.IdentifyTemporalSequencesAsync(content);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(s => s.TemporalMarkers.Contains("first"));
        result.Should().Contain(s => s.TemporalMarkers.Contains("then"));
        result.Should().Contain(s => s.TemporalMarkers.Contains("next"));
        result.Should().Contain(s => s.TemporalMarkers.Contains("finally"));

        // Sequences should be in order
        result.Should().BeInAscendingOrder(s => s.SequentialOrder);
    }

    #endregion

    #region DetectCausalRelationshipsAsync Tests

    [Fact]
    public async Task DetectCausalRelationshipsAsync_WithCausalMarkers_ReturnsCausalRelations()
    {
        // Arrange
        var content = @"
The server failed because of insufficient memory.
Therefore, the application became unresponsive.
As a result, users experienced timeouts.
";

        // Act
        var result = await _service.DetectCausalRelationshipsAsync(content);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(r => r.CausalIndicator.Equals("because", StringComparison.OrdinalIgnoreCase));
        result.Should().Contain(r => r.CausalIndicator.Equals("therefore", StringComparison.OrdinalIgnoreCase));
        result.Should().Contain(r => r.CausalIndicator.Equals("as a result", StringComparison.OrdinalIgnoreCase));

        foreach (var relation in result)
        {
            relation.Strength.Should().BeGreaterThan(0);
            relation.Strength.Should().BeLessOrEqualTo(1);
        }
    }

    #endregion

    #region IdentifyNarrativeArcElementsAsync Tests

    [Fact]
    public async Task IdentifyNarrativeArcElementsAsync_WithNarrativeStructure_ReturnsArcElements()
    {
        // Arrange
        var content = @"
Introduction: This document explains the problem we faced.
Background: The issue started when our server capacity reached its limits.
Development: We explored several solutions to address the scaling challenges.
Resolution: Finally, we implemented a load balancing solution that resolved the issue.
";

        // Act
        var result = await _service.IdentifyNarrativeArcElementsAsync(content, DocumentType.Documentation);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().ContainKey(NarrativeFunction.Setup);
        result.Should().ContainKey(NarrativeFunction.Background);
        result.Should().ContainKey(NarrativeFunction.Development);
        result.Should().ContainKey(NarrativeFunction.Resolution);

        foreach (var element in result)
        {
            element.Value.Should().NotBeEmpty();
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
                Content = "First, we need to understand the problem. The issue began when users started reporting errors.",
                SequenceNumber = 0,
                Metadata = new Dictionary<string, object>
                {
                    ["transition_type"] = NarrativeTransitionType.Temporal.ToString(),
                    ["narrative_function"] = NarrativeFunction.Setup.ToString()
                }
            },
            new DocumentSegment
            {
                Id = "seg2",
                Content = "Then, we investigated the cause. After analysis, we found the root issue was in the database.",
                SequenceNumber = 1,
                Metadata = new Dictionary<string, object>
                {
                    ["transition_type"] = NarrativeTransitionType.Temporal.ToString(),
                    ["narrative_function"] = NarrativeFunction.Development.ToString()
                }
            }
        };

        // Act
        var result = await _service.ValidateNarrativeSegmentsAsync(segments, "Original content");

        // Assert
        result.Should().NotBeNull();
        result.OverallQuality.Should().BeGreaterThan(0.4);
        result.FlowCoherence.Should().BeGreaterThan(0.3);
        result.SegmentResults.Should().HaveCount(2);
    }

    #endregion

    #region Helper Methods

    private string CreateNarrativeDocument()
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
