using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

public class ToolHandlerResultTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesResolved()
    {
        ToolHandlerResult result = "hello";

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        ((ToolHandlerResult.Resolved)result).Result.Result.Should().Be("hello");
    }

    [Fact]
    public void FromString_FactoryHelper_ProducesResolved()
    {
        var result = ToolHandlerResult.FromString("payload");

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        result.Result.Result.Should().Be("payload");
    }

    [Fact]
    public void ImplicitConversion_FromToolCallResult_ProducesResolved()
    {
        var inner = new ToolCallResult(null, "complex", new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "rich" },
        });
        ToolHandlerResult result = inner;

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Result.Result.Should().Be("complex");
        resolved.Result.ContentBlocks.Should().HaveCount(1);
    }

    [Fact]
    public void Deferred_CarriesPlaceholderAndMetadata()
    {
        var metadata = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["correlation_id"] = "abc-123",
            ["expected_wait_seconds"] = "60",
        });

        var deferred = new ToolHandlerResult.Deferred("PENDING approval", metadata);

        deferred.Placeholder.Should().Be("PENDING approval");
        deferred.Metadata.Should().NotBeNull();
        deferred.Metadata!["correlation_id"].Should().Be("abc-123");
    }

    [Fact]
    public void ResultText_OnResolved_ReturnsValue()
    {
        ToolHandlerResult resolved = new ToolHandlerResult.Resolved(new ToolCallResult(null, "payload"));
        resolved.ResultText.Should().Be("payload");
    }

    [Fact]
    public void ResultText_OnDeferred_Throws()
    {
        ToolHandlerResult deferred = new ToolHandlerResult.Deferred("pending");

        var act = () => _ = deferred.ResultText;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*deferred*");
    }

    [Fact]
    public void PatternMatch_DistinguishesVariantsExhaustively()
    {
        ToolHandlerResult resolved = new ToolHandlerResult.Resolved(new ToolCallResult(null, "ok"));
        ToolHandlerResult deferred = new ToolHandlerResult.Deferred("pending");

        string Match(ToolHandlerResult r) => r switch
        {
            ToolHandlerResult.Resolved x => $"resolved:{x.Result.Result}",
            ToolHandlerResult.Deferred x => $"deferred:{x.Placeholder}",
            _ => throw new InvalidOperationException("unreachable"),
        };

        Match(resolved).Should().Be("resolved:ok");
        Match(deferred).Should().Be("deferred:pending");
    }

    [Fact]
    public void ToolCallResultMessage_RoundTrips_DeferredFields()
    {
        var original = new ToolCallResultMessage
        {
            ToolCallId = "tc_1",
            Result = "PENDING",
            ToolName = "approval_tool",
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
            DeferralMetadata = ImmutableDictionary.CreateRange(new Dictionary<string, string>
            {
                ["correlation"] = "abc",
            }),
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ToolCallResultMessage>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.ToolCallId.Should().Be("tc_1");
        roundTripped.IsDeferred.Should().BeTrue();
        roundTripped.DeferredAt.Should().Be(1_700_000_000_000);
        roundTripped.DeferralMetadata.Should().NotBeNull();
        roundTripped.DeferralMetadata!["correlation"].Should().Be("abc");
    }

    [Fact]
    public void ToolCallResultMessage_Resolved_Has_ResolvedAt_And_ClearedDeferredFlag()
    {
        var resolved = new ToolCallResultMessage
        {
            ToolCallId = "tc_1",
            Result = "real",
            IsDeferred = false,
            ResolvedAt = 1_700_000_001_000,
        };

        resolved.IsDeferred.Should().BeFalse();
        resolved.ResolvedAt.Should().Be(1_700_000_001_000);
    }
}
