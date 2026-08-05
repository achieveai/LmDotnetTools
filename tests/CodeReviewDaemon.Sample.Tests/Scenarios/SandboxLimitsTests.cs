using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// PR #121 H4 — the bounds applied to sandbox command output and persisted artifacts. Output over the
/// configured limit is truncated and stamped with an explicit marker so a reader knows it was trimmed;
/// output at or under the limit passes through unchanged.
/// </summary>
public sealed class SandboxLimitsTests
{
    [Fact]
    public void Defaults_are_conservative_and_positive()
    {
        var limits = new SandboxLimits();

        limits.CommandTimeout.Should().BeGreaterThan(TimeSpan.Zero);
        limits.MaxOutputChars.Should().BeGreaterThan(0);
        limits.MaxArtifactPayloadChars.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CapOutput_passes_through_output_within_the_limit()
    {
        var limits = new SandboxLimits { MaxOutputChars = 100 };

        limits.CapOutput("short output").Should().Be("short output");
    }

    [Fact]
    public void CapOutput_truncates_and_marks_output_over_the_limit()
    {
        var limits = new SandboxLimits { MaxOutputChars = 10 };

        var result = limits.CapOutput(new string('x', 5_000));

        result.Should().StartWith(new string('x', 10));
        result.Should().Contain("truncated");
        result.Length.Should().BeLessThan(5_000, "the oversized output must actually be trimmed");
    }

    [Fact]
    public void CapArtifactPayload_truncates_and_marks_an_oversized_diff()
    {
        var limits = new SandboxLimits { MaxArtifactPayloadChars = 32 };

        var result = limits.CapArtifactPayload(new string('d', 10_000));

        result.Should().StartWith(new string('d', 32));
        result.Should().Contain("truncated");
    }

    [Fact]
    public void CapOutput_does_not_spend_the_budget_looking_for_a_line_boundary()
    {
        // Only SOME captured output is record-oriented, and the generic cap sees all of it: build logs, test
        // output, a diff of a minified file. A payload of one status line followed by a single enormous line
        // has its only boundary at index 6, so cutting there would discard essentially the whole configured
        // budget to protect records this output never had. The generic cap is therefore character-exact; the
        // record guarantee belongs to CapRecordListing, which is applied only where records exist.
        var limits = new SandboxLimits { MaxOutputChars = 200 };

        var result = limits.CapOutput("STATUS\n" + new string('x', 5_000));

        result.Should().StartWith("STATUS\n" + new string('x', 193));
        result.Should().Contain("truncated");
    }

    [Fact]
    public void CapOutput_with_no_line_boundary_below_the_limit_still_cuts_at_the_limit()
    {
        var limits = new SandboxLimits { MaxOutputChars = 10 };

        limits.CapOutput(new string('x', 5_000)).Should().StartWith(new string('x', 10));
    }

    [Fact]
    public void CapRecordListing_cuts_on_a_complete_line_boundary_so_no_record_is_halved()
    {
        // The changed-path listing is one record per line and the cap can land anywhere. Cutting mid-record
        // leaves a stump in front of the marker that reads exactly like a real path, and every consumer
        // downstream then ranks against a file git never reported. Whatever survives THIS cap has to be
        // whole.
        var limits = new SandboxLimits { MaxArtifactPayloadChars = 16 };

        var result = limits.CapRecordListing("aaa\nbbb\nsrc/VeryLongFileName.cs\n");

        result.Should().StartWith("aaa\nbbb\n");
        result.Should().NotContain("src/Very", "a halved record reads exactly like a real path");
        result.Should().Contain("truncated");
    }

    [Fact]
    public void CapRecordListing_keeps_the_terminating_newline_so_a_reader_can_tell_the_cut_was_clean()
    {
        // Load-bearing, not cosmetic. The marker opens with "\n", so keeping the record's own terminator puts
        // an EMPTY line between the last whole record and the marker. That emptiness is the only evidence a
        // parser has that the cut landed on a boundary: a character-exact cut leaves the stump there instead.
        // KnowledgeDigest reads exactly this distinction to decide whether to trust the last record.
        var limits = new SandboxLimits { MaxArtifactPayloadChars = 16 };

        limits.CapRecordListing("aaa\nbbb\nsrc/VeryLongFileName.cs\n")
            .Should().Be("aaa\nbbb\n" + SandboxLimits.TruncationMarker);
    }

    [Fact]
    public void CapRecordListing_with_no_line_boundary_below_the_limit_still_cuts_at_the_limit()
    {
        // One enormous line has no boundary to fall back to, so the hard cut stands rather than discarding
        // everything. The stump it leaves is the parser's problem, and the parser is told about it by the
        // absent blank line.
        var limits = new SandboxLimits { MaxArtifactPayloadChars = 10 };

        limits.CapRecordListing(new string('x', 5_000)).Should().StartWith(new string('x', 10));
    }
}
