using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

/// <summary>
///     Bounding rules for tool results before they enter history or a provider request (#694).
///     The reproduction size (15,231,668 bytes) is the exact payload the OpenAI Responses API
///     rejected with "string too long. Expected maximum length 10485760".
/// </summary>
public sealed class ToolResultLimitsTests
{
    private const int ReproducedOversizedLength = 15_231_668;

    private static string MakeAscii(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)('a' + (i % 26));
        }

        return new string(chars);
    }

    [Fact]
    public void Default_limit_is_well_under_the_responses_api_field_limit()
    {
        ToolResultLimits.Default.MaxResultBytes.Should().Be(4 * 1024 * 1024);
        ToolResultLimits.Default.MaxResultBytes.Should().BeLessThan(10_485_760);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ToolResultLimits.MinResultBytes - 1)]
    public void MaxResultBytes_below_the_minimum_is_rejected(int cap)
    {
        var act = () => new ToolResultLimits { MaxResultBytes = cap };

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should()
            .Be(nameof(ToolResultLimits.MaxResultBytes));
    }

    [Fact]
    public void MaxResultBytes_at_the_minimum_is_accepted_and_still_bounds_output_to_the_cap()
    {
        var limits = new ToolResultLimits { MaxResultBytes = ToolResultLimits.MinResultBytes };
        var original = MakeAscii(10_000);

        var bounded = limits.BoundText(original);

        Encoding.UTF8.GetByteCount(bounded).Should().BeLessThanOrEqualTo(ToolResultLimits.MinResultBytes);
        bounded.Should().StartWith(original[..64], "the smallest cap must still keep a real prefix");
        bounded.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
        bounded.Should().EndWith(" of 10,000 bytes]");
    }

    [Fact]
    public void Oversized_text_is_bounded_keeps_prefix_verbatim_and_ends_with_marker()
    {
        var original = MakeAscii(ReproducedOversizedLength);

        var bounded = ToolResultLimits.Default.BoundText(original);

        Encoding.UTF8.GetByteCount(bounded).Should().BeLessThanOrEqualTo(ToolResultLimits.Default.MaxResultBytes);
        bounded.Should().StartWith(original[..1024]);
        bounded.Should().EndWith(" of 15,231,668 bytes]");
        bounded.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
    }

    [Fact]
    public void Small_text_is_returned_byte_identical_without_marker()
    {
        var original = "Sunny, 72F";

        var bounded = ToolResultLimits.Default.BoundText(original);

        bounded.Should().BeSameAs(original);
        bounded.Should().NotContain(ToolResultLimits.TruncationMarkerPrefix);
    }

    [Fact]
    public void Text_exactly_at_the_limit_is_not_touched()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 256 };
        var original = MakeAscii(256);

        limits.BoundText(original).Should().BeSameAs(original);
    }

    [Fact]
    public void Truncation_counts_utf8_bytes_and_never_splits_a_multibyte_character()
    {
        // Each snowman is 3 UTF-8 bytes but 1 UTF-16 char, so a char-based limit would overshoot.
        var original = new string('☃', 200);
        var limits = new ToolResultLimits { MaxResultBytes = 256 };

        var bounded = limits.BoundText(original);

        var bytes = Encoding.UTF8.GetByteCount(bounded);
        bytes.Should().BeLessThanOrEqualTo(256);
        var markerStart = bounded.IndexOf(ToolResultLimits.TruncationMarkerPrefix, StringComparison.Ordinal);
        markerStart.Should().BeGreaterThan(0);
        var kept = bounded[..markerStart].TrimEnd('\n');
        kept.Should().MatchRegex("^☃+$", "the cut must land on a character boundary");
        bounded.Should().EndWith(" of 600 bytes]");
    }

    [Fact]
    public void Truncation_never_splits_a_four_byte_rune()
    {
        // U+1F600 is a surrogate pair in UTF-16 (2 chars) and 4 bytes in UTF-8: the cut must land
        // between whole emoji, never between the high and low surrogate.
        const string Emoji = "\U0001F600";
        var original = string.Concat(Enumerable.Repeat(Emoji, 200));
        var limits = new ToolResultLimits { MaxResultBytes = 256 };

        var bounded = limits.BoundText(original);

        Encoding.UTF8.GetByteCount(bounded).Should().BeLessThanOrEqualTo(256);
        var markerStart = bounded.IndexOf(ToolResultLimits.TruncationMarkerPrefix, StringComparison.Ordinal);
        var kept = bounded[..markerStart].TrimEnd('\n');
        (kept.Length % 2).Should().Be(0, "a split surrogate pair would leave an odd char count");
        kept.EnumerateRunes().Should().OnlyContain(r => r.Value == 0x1F600, "no rune may be split");
        kept.Length.Should().BeGreaterThan(0);
        bounded.Should().EndWith(" of 800 bytes]");
    }

    [Fact]
    public void A_lone_surrogate_is_counted_as_the_three_bytes_the_encoder_emits_for_it()
    {
        // An unpaired high surrogate cannot be encoded; UTF-8 emits U+FFFD (3 bytes) in its place,
        // and the byte accounting must agree with that so the cap still holds on the wire.
        var original = new string('\uD800', 500);
        var limits = new ToolResultLimits { MaxResultBytes = 256 };

        var bounded = limits.BoundText(original);

        Encoding.UTF8.GetByteCount(bounded).Should().BeLessThanOrEqualTo(256);
        bounded.Should().EndWith(" of 1,500 bytes]");
    }

    [Fact]
    public void Apply_bounds_result_text_and_flags_the_struct()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 256 };
        var oversized = new ToolCallResult("call-1", MakeAscii(1_000)) { ToolName = "dump" };

        var bounded = limits.Apply(oversized);

        bounded.IsTruncated.Should().BeTrue();
        bounded.OriginalBytes.Should().Be(1_000);
        bounded.ToolCallId.Should().Be("call-1");
        bounded.ToolName.Should().Be("dump");
        Encoding.UTF8.GetByteCount(bounded.Result).Should().BeLessThanOrEqualTo(256);
        bounded.Result.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
    }

    [Fact]
    public void Apply_leaves_a_small_result_unchanged_and_unflagged()
    {
        var small = new ToolCallResult("call-2", "ok") { IsError = true, ErrorCode = "E1" };

        var bounded = ToolResultLimits.Default.Apply(small);

        bounded.Should().Be(small);
        bounded.IsTruncated.Should().BeFalse();
        bounded.OriginalBytes.Should().BeNull();
    }

    [Fact]
    public void TryApply_reports_whether_anything_was_cut_independently_of_an_earlier_flag()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 256 };
        var alreadyBounded = limits.Apply(new ToolCallResult("call-5", MakeAscii(1_000)));

        // A second pass over an already-bounded result cuts nothing, so it must not report a cut
        // even though the input is flagged — that is what lets a history-ingress bound stay a no-op
        // for results the executor already bounded.
        limits.TryApply(alreadyBounded, out var second).Should().BeFalse();
        second.Should().Be(alreadyBounded);
        second.OriginalBytes.Should().Be(1_000);
        limits.TryApply(new ToolCallResult("call-6", MakeAscii(1_000)), out var cut).Should().BeTrue();
        cut.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public void Apply_bounds_text_content_blocks_and_leaves_image_blocks_alone()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 256 };
        var oversized = new ToolCallResult(
            "call-3",
            "short",
            [
                new TextToolResultBlock { Text = MakeAscii(1_000) },
                new ImageToolResultBlock { Data = "AAAA", MimeType = "image/png" },
            ]
        );

        var bounded = limits.Apply(oversized);

        bounded.IsTruncated.Should().BeTrue();
        bounded.Result.Should().Be("short");
        bounded.OriginalBytes.Should().Be(1_005);
        var text = bounded.ContentBlocks![0].Should().BeOfType<TextToolResultBlock>().Subject;
        Encoding.UTF8.GetByteCount(text.Text).Should().BeLessThanOrEqualTo(256);
        text.Text.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
        bounded.ContentBlocks[1].Should().BeSameAs(oversized.ContentBlocks![1]);
    }

    [Fact]
    public void Unbounded_never_truncates()
    {
        var original = MakeAscii(ReproducedOversizedLength);

        ToolResultLimits.Unbounded.BoundText(original).Should().BeSameAs(original);
    }

    [Fact]
    public void Truncated_flag_round_trips_through_the_singular_message()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 256 };
        var bounded = limits.Apply(new ToolCallResult("call-4", MakeAscii(1_000)));

        var message = ToolCallResultMessage.FromToolCallResult(bounded);
        message.IsTruncated.Should().BeTrue();
        message.OriginalBytes.Should().Be(1_000);
        message.ToToolCallResult().IsTruncated.Should().BeTrue();
        message.ToToolCallResult().OriginalBytes.Should().Be(1_000);
    }

    [Fact]
    public void OriginalBytes_counts_all_original_utf8_text_fields()
    {
        var original = new ToolCallResult(
            "utf8",
            new string('☃', 200),
            [
                new TextToolResultBlock { Text = "😀" },
                new TextToolResultBlock { Text = new string('é', 200) },
                new ImageToolResultBlock { Data = "AAAA", MimeType = "image/png" },
            ]
        );

        var bounded = new ToolResultLimits { MaxResultBytes = 256 }.Apply(original);

        bounded.OriginalBytes.Should().Be(1_004);
    }

    [Fact]
    public void A_stricter_second_bound_preserves_the_original_count()
    {
        var first = new ToolResultLimits { MaxResultBytes = 512 }.Apply(
            new ToolCallResult("repeat", new string('☃', 1_000))
        );

        var second = new ToolResultLimits { MaxResultBytes = 256 }.Apply(first);

        second.OriginalBytes.Should().Be(3_000);
        Encoding.UTF8.GetByteCount(second.Result).Should().BeLessThanOrEqualTo(256);
    }

    [Fact]
    public void A_stricter_bound_keeps_an_unknown_legacy_original_count_null()
    {
        var legacy = new ToolCallResult("legacy", new string('a', 512)) { IsTruncated = true };

        var bounded = new ToolResultLimits { MaxResultBytes = 256 }.Apply(legacy);

        bounded.OriginalBytes.Should().BeNull("the original size was not recorded before the earlier truncation");
        bounded.IsTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(bounded.Result).Should().BeLessThanOrEqualTo(256);
    }

    [Fact]
    public void OriginalBytes_saturates_when_text_fields_total_more_than_int_max()
    {
        // Reuse one block to exercise a >2 GiB logical payload without allocating it.
        var block = new TextToolResultBlock { Text = new string('a', 1024 * 1024) };
        var original = new ToolCallResult(
            "large-total",
            "x",
            [.. Enumerable.Repeat<ToolResultContentBlock>(block, 2048)]
        );

        var bounded = new ToolResultLimits { MaxResultBytes = 256 }.Apply(original);

        bounded.OriginalBytes.Should().Be(int.MaxValue);
    }

    [Fact]
    public void OriginalBytes_round_trips_in_result_and_message_json()
    {
        var bounded = new ToolResultLimits { MaxResultBytes = 256 }.Apply(
            new ToolCallResult("json", new string('é', 200))
        );

        var resultJson = JsonSerializer.Serialize(bounded);
        resultJson.Should().Contain("\"original_bytes\":400");
        JsonSerializer.Deserialize<ToolCallResult>(resultJson).OriginalBytes.Should().Be(400);
        var messageJson = JsonSerializer.Serialize(ToolCallResultMessage.FromToolCallResult(bounded));
        JsonSerializer.Deserialize<ToolCallResultMessage>(messageJson)!.OriginalBytes.Should().Be(400);
        JsonSerializer.Serialize(new ToolCallResult("small", "ok")).Should().NotContain("original_bytes");
    }

    [Fact]
    public void Legacy_server_result_truncation_metadata_survives_conversion_and_json_round_trip()
    {
        const string Json = """
            {"$type":"server_tool_result","tool_use_id":"server-1","result":"cut",
             "is_truncated":true,"original_bytes":1000}
            """;
        var options = new JsonSerializerOptions { Converters = { new IMessageJsonConverter() } };

        var message = JsonSerializer
            .Deserialize<IMessage>(Json, options)
            .Should()
            .BeOfType<ToolCallResultMessage>()
            .Subject;

        message.IsTruncated.Should().BeTrue();
        message.OriginalBytes.Should().Be(1_000);
        var roundTrip = JsonSerializer
            .Deserialize<IMessage>(JsonSerializer.Serialize<IMessage>(message, options), options)
            .Should()
            .BeOfType<ToolCallResultMessage>()
            .Subject;
        roundTrip.IsTruncated.Should().BeTrue();
        roundTrip.OriginalBytes.Should().Be(1_000);
    }
}
