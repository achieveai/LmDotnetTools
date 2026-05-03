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

    [Fact]
    public void ToolCallResult_Struct_DeferredFields_AreSettableViaWith()
    {
        var initial = new ToolCallResult("tc_1", "PENDING");
        var metadata = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["correlation"] = "xyz",
        });

        var deferred = initial with
        {
            IsDeferred = true,
            DeferralMetadata = metadata,
            DeferredAt = 1_700_000_000_000,
        };

        deferred.IsDeferred.Should().BeTrue();
        deferred.DeferralMetadata.Should().NotBeNull();
        deferred.DeferralMetadata!["correlation"].Should().Be("xyz");
        deferred.DeferredAt.Should().Be(1_700_000_000_000);
        deferred.ResolvedAt.Should().BeNull();
        // Existing fields preserved.
        deferred.ToolCallId.Should().Be("tc_1");
        deferred.Result.Should().Be("PENDING");
    }

    [Fact]
    public void ToolCallResult_Struct_DefaultValues_AreNotDeferred()
    {
        var plain = new ToolCallResult("tc_2", "ok");

        plain.IsDeferred.Should().BeFalse();
        plain.DeferralMetadata.Should().BeNull();
        plain.DeferredAt.Should().BeNull();
        plain.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public void ToolCallResult_Struct_JsonRoundTrip_PreservesDeferredFields()
    {
        var original = new ToolCallResult("tc_3", "PENDING")
        {
            IsDeferred = true,
            DeferralMetadata = ImmutableDictionary.CreateRange(new Dictionary<string, string>
            {
                ["wait_ms"] = "60000",
            }),
            DeferredAt = 1_700_000_000_000,
        };

        var json = JsonSerializer.Serialize(original);
        json.Should().Contain("\"is_deferred\":true");
        json.Should().Contain("\"deferral_metadata\"");
        json.Should().Contain("\"deferred_at\":1700000000000");
        // Resolved_at omitted on null.
        json.Should().NotContain("resolved_at");

        var roundTripped = JsonSerializer.Deserialize<ToolCallResult>(json);
        roundTripped.IsDeferred.Should().BeTrue();
        roundTripped.DeferralMetadata.Should().NotBeNull();
        roundTripped.DeferralMetadata!["wait_ms"].Should().Be("60000");
        roundTripped.DeferredAt.Should().Be(1_700_000_000_000);
        roundTripped.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public void ToolCallResult_Struct_JsonRoundTrip_OmitsNullableDeferredFieldsWhenDefault()
    {
        var plain = new ToolCallResult("tc_4", "result");

        var json = JsonSerializer.Serialize(plain);

        // Nullable deferral fields are omitted via [JsonIgnore(WhenWritingNull)].
        json.Should().NotContain("deferral_metadata");
        json.Should().NotContain("deferred_at");
        json.Should().NotContain("resolved_at");
        // `is_deferred` is a non-nullable bool and always serializes (mirrors `is_error`),
        // matching the singular ToolCallResultMessage record's behavior. Default is false.
        json.Should().Contain("\"is_deferred\":false");
    }

    [Fact]
    public void ToToolCallResult_PreservesDeferralFields()
    {
        var metadata = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["ticket"] = "T-99",
        });
        var record = new ToolCallResultMessage
        {
            ToolCallId = "tc_5",
            Result = "PENDING",
            ToolName = "send_email",
            IsDeferred = true,
            DeferralMetadata = metadata,
            DeferredAt = 1_700_000_000_000,
        };

        var asStruct = record.ToToolCallResult();

        asStruct.IsDeferred.Should().BeTrue();
        asStruct.DeferralMetadata.Should().BeSameAs(metadata);
        asStruct.DeferredAt.Should().Be(1_700_000_000_000);
        asStruct.ResolvedAt.Should().BeNull();
        asStruct.ToolCallId.Should().Be("tc_5");
        asStruct.Result.Should().Be("PENDING");
    }

    [Fact]
    public void FromToolCallResult_PreservesDeferralFields()
    {
        var metadata = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["ticket"] = "T-100",
        });
        var asStruct = new ToolCallResult("tc_6", "PENDING")
        {
            ToolName = "send_email",
            IsDeferred = true,
            DeferralMetadata = metadata,
            DeferredAt = 1_700_000_000_000,
        };

        var record = ToolCallResultMessage.FromToolCallResult(asStruct);

        record.IsDeferred.Should().BeTrue();
        record.DeferralMetadata.Should().BeSameAs(metadata);
        record.DeferredAt.Should().Be(1_700_000_000_000);
        record.ResolvedAt.Should().BeNull();
        record.ToolCallId.Should().Be("tc_6");
        record.Result.Should().Be("PENDING");
    }

    [Fact]
    public void RecordToStructToRecord_RoundTrip_IsLossless_ForDeferralFields()
    {
        var metadata = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["ticket"] = "T-101",
            ["priority"] = "high",
        });
        var original = new ToolCallResultMessage
        {
            ToolCallId = "tc_7",
            Result = "PENDING",
            ToolName = "approve",
            IsDeferred = true,
            DeferralMetadata = metadata,
            DeferredAt = 1_700_000_000_000,
            ResolvedAt = null,
        };

        var roundTripped = ToolCallResultMessage.FromToolCallResult(original.ToToolCallResult());

        roundTripped.IsDeferred.Should().Be(original.IsDeferred);
        roundTripped.DeferralMetadata.Should().BeSameAs(original.DeferralMetadata);
        roundTripped.DeferredAt.Should().Be(original.DeferredAt);
        roundTripped.ResolvedAt.Should().Be(original.ResolvedAt);
        roundTripped.ToolCallId.Should().Be(original.ToolCallId);
        roundTripped.Result.Should().Be(original.Result);
        roundTripped.ToolName.Should().Be(original.ToolName);
    }
}
