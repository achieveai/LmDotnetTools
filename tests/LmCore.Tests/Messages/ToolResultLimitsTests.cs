using System.Text;
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
        var limits = new ToolResultLimits { MaxResultBytes = 32 };
        var original = MakeAscii(32);

        limits.BoundText(original).Should().BeSameAs(original);
    }

    [Fact]
    public void Truncation_counts_utf8_bytes_and_never_splits_a_multibyte_character()
    {
        // Each snowman is 3 UTF-8 bytes but 1 UTF-16 char, so a char-based limit would overshoot.
        var original = new string('☃', 200);
        var limits = new ToolResultLimits { MaxResultBytes = 128 };

        var bounded = limits.BoundText(original);

        var bytes = Encoding.UTF8.GetByteCount(bounded);
        bytes.Should().BeLessThanOrEqualTo(128);
        var markerStart = bounded.IndexOf(ToolResultLimits.TruncationMarkerPrefix, StringComparison.Ordinal);
        markerStart.Should().BeGreaterThan(0);
        var kept = bounded[..markerStart].TrimEnd('\n');
        kept.Should().MatchRegex("^☃+$", "the cut must land on a character boundary");
        bounded.Should().EndWith(" of 600 bytes]");
    }

    [Fact]
    public void Apply_bounds_result_text_and_flags_the_struct()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 96 };
        var oversized = new ToolCallResult("call-1", MakeAscii(1_000)) { ToolName = "dump" };

        var bounded = limits.Apply(oversized);

        bounded.IsTruncated.Should().BeTrue();
        bounded.ToolCallId.Should().Be("call-1");
        bounded.ToolName.Should().Be("dump");
        Encoding.UTF8.GetByteCount(bounded.Result).Should().BeLessThanOrEqualTo(96);
        bounded.Result.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
    }

    [Fact]
    public void Apply_leaves_a_small_result_unchanged_and_unflagged()
    {
        var small = new ToolCallResult("call-2", "ok") { IsError = true, ErrorCode = "E1" };

        var bounded = ToolResultLimits.Default.Apply(small);

        bounded.Should().Be(small);
        bounded.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public void Apply_bounds_text_content_blocks_and_leaves_image_blocks_alone()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 96 };
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
        var text = bounded.ContentBlocks![0].Should().BeOfType<TextToolResultBlock>().Subject;
        Encoding.UTF8.GetByteCount(text.Text).Should().BeLessThanOrEqualTo(96);
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
        var limits = new ToolResultLimits { MaxResultBytes = 96 };
        var bounded = limits.Apply(new ToolCallResult("call-4", MakeAscii(1_000)));

        var message = ToolCallResultMessage.FromToolCallResult(bounded);
        message.IsTruncated.Should().BeTrue();
        message.ToToolCallResult().IsTruncated.Should().BeTrue();
    }
}
