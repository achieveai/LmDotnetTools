using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

public class ToolHandlerResultTests
{
    [Fact]
    public void FromText_ProducesResolvedWithTextOnlyPayload()
    {
        var result = ToolHandlerResult.FromText("hello");

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Payload.Text.Should().Be("hello");
        resolved.Payload.ContentBlocks.Should().BeNull();
        resolved.Payload.IsError.Should().BeFalse();
        resolved.Payload.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void FromError_ProducesResolvedWithIsErrorTrue()
    {
        var result = ToolHandlerResult.FromError("bad input", errorCode: "E_INPUT");

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Payload.Text.Should().Be("bad input");
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be("E_INPUT");
    }

    [Fact]
    public void FromMultiModal_ProducesResolvedWithContentBlocks()
    {
        var blocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "rich" },
            new ImageToolResultBlock { Data = "data", MimeType = "image/png" },
        };

        var result = ToolHandlerResult.FromMultiModal("complex", blocks);

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Payload.Text.Should().Be("complex");
        resolved.Payload.ContentBlocks.Should().BeSameAs(blocks);
    }

    [Fact]
    public void Deferred_IsEmptyRecord()
    {
        var d1 = new ToolHandlerResult.Deferred();
        var d2 = new ToolHandlerResult.Deferred();

        // Empty record — value equality is structural, both instances are equal.
        d1.Should().Be(d2);
    }

    [Fact]
    public void PatternMatch_DistinguishesVariantsExhaustively()
    {
        ToolHandlerResult resolved = ToolHandlerResult.FromText("ok");
        ToolHandlerResult deferred = new ToolHandlerResult.Deferred();

        string Match(ToolHandlerResult r) => r switch
        {
            ToolHandlerResult.Resolved x => $"resolved:{x.Payload.Text}",
            ToolHandlerResult.Deferred => "deferred",
            _ => throw new InvalidOperationException("unreachable"),
        };

        Match(resolved).Should().Be("resolved:ok");
        Match(deferred).Should().Be("deferred");
    }

    [Fact]
    public void ToolCallResultMessage_RoundTrips_DeferredFields()
    {
        var original = new ToolCallResultMessage
        {
            ToolCallId = "tc_1",
            Result = string.Empty,
            ToolName = "approval_tool",
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ToolCallResultMessage>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.ToolCallId.Should().Be("tc_1");
        roundTripped.IsDeferred.Should().BeTrue();
        roundTripped.DeferredAt.Should().Be(1_700_000_000_000);
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
        var initial = new ToolCallResult("tc_1", string.Empty);

        var deferred = initial with
        {
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
        };

        deferred.IsDeferred.Should().BeTrue();
        deferred.DeferredAt.Should().Be(1_700_000_000_000);
        deferred.ResolvedAt.Should().BeNull();
        // Existing fields preserved.
        deferred.ToolCallId.Should().Be("tc_1");
        deferred.Result.Should().Be(string.Empty);
    }

    [Fact]
    public void ToolCallResult_Struct_DefaultValues_AreNotDeferred()
    {
        var plain = new ToolCallResult("tc_2", "ok");

        plain.IsDeferred.Should().BeFalse();
        plain.DeferredAt.Should().BeNull();
        plain.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public void ToolCallResult_Struct_JsonRoundTrip_PreservesDeferredFields()
    {
        var original = new ToolCallResult("tc_3", string.Empty)
        {
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
        };

        var json = JsonSerializer.Serialize(original);
        json.Should().Contain("\"is_deferred\":true");
        json.Should().Contain("\"deferred_at\":1700000000000");
        // Resolved_at omitted on null.
        json.Should().NotContain("resolved_at");

        var roundTripped = JsonSerializer.Deserialize<ToolCallResult>(json);
        roundTripped.IsDeferred.Should().BeTrue();
        roundTripped.DeferredAt.Should().Be(1_700_000_000_000);
        roundTripped.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public void ToolCallResult_Struct_JsonRoundTrip_OmitsNullableDeferredFieldsWhenDefault()
    {
        var plain = new ToolCallResult("tc_4", "result");

        var json = JsonSerializer.Serialize(plain);

        // Nullable deferral fields are omitted via [JsonIgnore(WhenWritingNull)].
        json.Should().NotContain("deferred_at");
        json.Should().NotContain("resolved_at");
        // `is_deferred` is a non-nullable bool and always serializes (mirrors `is_error`),
        // matching the singular ToolCallResultMessage record's behavior. Default is false.
        json.Should().Contain("\"is_deferred\":false");
    }

    [Fact]
    public void ToToolCallResult_PreservesDeferralFields()
    {
        var record = new ToolCallResultMessage
        {
            ToolCallId = "tc_5",
            Result = string.Empty,
            ToolName = "send_email",
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
        };

        var asStruct = record.ToToolCallResult();

        asStruct.IsDeferred.Should().BeTrue();
        asStruct.DeferredAt.Should().Be(1_700_000_000_000);
        asStruct.ResolvedAt.Should().BeNull();
        asStruct.ToolCallId.Should().Be("tc_5");
        asStruct.Result.Should().Be(string.Empty);
    }

    [Fact]
    public void FromToolCallResult_PreservesDeferralFields()
    {
        var asStruct = new ToolCallResult("tc_6", string.Empty)
        {
            ToolName = "send_email",
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
        };

        var record = ToolCallResultMessage.FromToolCallResult(asStruct);

        record.IsDeferred.Should().BeTrue();
        record.DeferredAt.Should().Be(1_700_000_000_000);
        record.ResolvedAt.Should().BeNull();
        record.ToolCallId.Should().Be("tc_6");
        record.Result.Should().Be(string.Empty);
    }

    [Fact]
    public void RecordToStructToRecord_RoundTrip_IsLossless_ForDeferralFields()
    {
        var original = new ToolCallResultMessage
        {
            ToolCallId = "tc_7",
            Result = string.Empty,
            ToolName = "approve",
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
            ResolvedAt = null,
        };

        var roundTripped = ToolCallResultMessage.FromToolCallResult(original.ToToolCallResult());

        roundTripped.IsDeferred.Should().Be(original.IsDeferred);
        roundTripped.DeferredAt.Should().Be(original.DeferredAt);
        roundTripped.ResolvedAt.Should().Be(original.ResolvedAt);
        roundTripped.ToolCallId.Should().Be(original.ToolCallId);
        roundTripped.Result.Should().Be(original.Result);
        roundTripped.ToolName.Should().Be(original.ToolName);
    }

    [Fact]
    public void ToolCallResultBuilder_FromHandlerResult_StampsToolCallIdAndToolName()
    {
        // Locks in the bug fix: the builder must always pull tool_call_id from context, never
        // leave it null. Previously FunctionCallMiddleware.cs:106 hardcoded null.
        var result = ToolHandlerResult.FromText("ok");

        var tcr = ToolCallResultBuilder.FromHandlerResult(result, "tc_xyz", "my_tool");

        tcr.ToolCallId.Should().Be("tc_xyz");
        tcr.ToolName.Should().Be("my_tool");
        tcr.Result.Should().Be("ok");
        tcr.IsDeferred.Should().BeFalse();
        tcr.IsError.Should().BeFalse();
    }

    [Fact]
    public void ToolCallResultBuilder_FromHandlerResult_DeferredProducesEmptyResultWithFlag()
    {
        var result = new ToolHandlerResult.Deferred();

        var tcr = ToolCallResultBuilder.FromHandlerResult(result, "tc_def", "my_tool");

        tcr.ToolCallId.Should().Be("tc_def");
        tcr.ToolName.Should().Be("my_tool");
        tcr.Result.Should().Be(string.Empty);
        tcr.IsDeferred.Should().BeTrue();
        tcr.DeferredAt.Should().NotBeNull();
        tcr.DeferredAt!.Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ToolCallResultBuilder_FromHandlerResult_PropagatesErrorPayload()
    {
        var result = ToolHandlerResult.FromError("oops", errorCode: "E_X");

        var tcr = ToolCallResultBuilder.FromHandlerResult(result, "tc_err", "my_tool");

        tcr.IsError.Should().BeTrue();
        tcr.ErrorCode.Should().Be("E_X");
        tcr.Result.Should().Be("oops");
    }
}
