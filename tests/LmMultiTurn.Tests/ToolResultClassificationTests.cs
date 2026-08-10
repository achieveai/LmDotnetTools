using AchieveAi.LmDotnetTools.LmMultiTurn;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins <see cref="MultiTurnAgentLoop.ClassifyResult"/>, the non-EUII half of the tool-result log.
/// <para>
/// The raw result preview is source code and PR content when the caller is a code-review agent, so it sits
/// at Trace, which release builds strip. Debug carries only the tool name, the result length and this
/// classification — which means every question the log has actually been asked ("how many reads failed",
/// "how many calls were denied", "how many came back empty") has to be answerable from the class alone.
/// These tests are that contract.
/// </para>
/// </summary>
public class ToolResultClassificationTests
{
    [Fact]
    public void A_missing_file_is_not_found_even_though_the_tool_reported_success()
    {
        // THE case this classification exists for, and the one a bare IsError check gets wrong. A sandbox
        // Read of a path that is missing, mistyped, or outside the agent's scope returns a SUCCESSFUL tool
        // result whose text happens to say the file is not there — isError is false, and the failure is
        // shaped exactly like real content. Measured: 167 such results for a single reference document
        // across 82 review threads, and nothing anywhere counted them.
        MultiTurnAgentLoop.ClassifyResult(
                "File does not exist yet: /marketplaces/gb/code-reviewer/reference/codebase-search-discipline.md",
                isError: false,
                isDeferred: false)
            .Should().Be("not-found");
    }

    [Fact]
    public void An_egress_denial_is_denied_and_is_distinguishable_from_an_empty_answer()
    {
        // "Denied" and "empty" are indistinguishable in a review's output and mean opposite things: one is a
        // capability the agent never had, the other is a real answer. They must never share a bucket.
        MultiTurnAgentLoop.ClassifyResult(
                """{"attempts":1,"body":"","http_status":502,"status":"upstream_error"}""",
                isError: false,
                isDeferred: false)
            .Should().Be("denied");

        MultiTurnAgentLoop.ClassifyResult("", isError: false, isDeferred: false)
            .Should().Be("empty");
        MultiTurnAgentLoop.ClassifyResult("   \n ", isError: false, isDeferred: false)
            .Should().Be("empty");
    }

    [Fact]
    public void A_missing_toolchain_is_an_error_not_a_denial()
    {
        // `dotnet` is absent from the review sandbox BY DESIGN — reviews are not meant to build. So a
        // 127 has to read as an error, never as a denial: nothing was refused, the tool simply is not there.
        MultiTurnAgentLoop.ClassifyResult(
                "/bin/sh: 1: dotnet: not found  [Exit code: 127]",
                isError: false,
                isDeferred: false)
            .Should().Be("error");
    }

    [Fact]
    public void A_successful_command_is_not_an_error_just_because_it_reports_its_exit_code()
    {
        // The marker's PRESENCE meant "error" until this test existed, and `[Exit code: 0]` is a real and
        // common value — every wrapped shell call that succeeded. Getting this wrong is worse than not
        // classifying shell calls at all: it manufactures a failure rate out of the tool the reviewer uses
        // most, and the rate would look alarming and move with usage.
        MultiTurnAgentLoop.ClassifyResult("all 14 tests passed\n\n[Exit code: 0]", isError: false, isDeferred: false)
            .Should().Be("ok");

        // Read from the END, because the shell appends the marker after the whole output — so on a verbose
        // command it is nowhere near the start and the leading window below cannot see it.
        MultiTurnAgentLoop.ClassifyResult(
                new string('x', 40_000) + "\n\n[Exit code: 7]", isError: false, isDeferred: false)
            .Should().Be("error");

        // The LAST marker wins: a captured-output envelope reports its own exit_code at the top while the
        // stdout it captured ends in the command's real verdict.
        MultiTurnAgentLoop.ClassifyResult(
                """{"exit_code":0,"status":"completed","stdout":"boom\n\n[Exit code: 2]"}""",
                isError: false,
                isDeferred: false)
            .Should().Be("error");

        // Unparseable says nothing about the command, so it must not be guessed into a class.
        MultiTurnAgentLoop.ClassifyResult("see [Exit code: unknown]", isError: false, isDeferred: false)
            .Should().Be("ok");
    }

    [Fact]
    public void A_marker_deep_inside_returned_content_is_content_not_a_failure()
    {
        // Without a leading window the classifier reads its own corpus. A tool result is arbitrary content —
        // including source files, review text, and this very test file — so "does not exist" appearing on
        // line 900 of a file the reviewer opened would be counted as a failed read. The count would then grow
        // with how much code the review examined rather than with how much broke, which is the one way a
        // failure metric becomes actively misleading rather than merely incomplete.
        var sourceFile = new string('/', 400) + "\n// the configured path does not exist on older hosts\n";
        MultiTurnAgentLoop.ClassifyResult(sourceFile, isError: false, isDeferred: false)
            .Should().Be("ok");

        // The same words at the top ARE the tool's own failure, and still classify.
        MultiTurnAgentLoop.ClassifyResult(
                "File does not exist yet: /workspace/notes/Knowledge.md", isError: false, isDeferred: false)
            .Should().Be("not-found");

        // git's phrasing, which shares no prefix with the sandbox reader's — 4 of 347 live hits are this
        // shape, so a marker tightened to "File does not exist" would have dropped every one of them.
        MultiTurnAgentLoop.ClassifyResult(
                "fatal: path 'data/Version.props' does not exist in '9fd937c6acc'\n",
                isError: false,
                isDeferred: false)
            .Should().Be("not-found");
    }

    [Fact]
    public void A_structured_error_flag_with_no_recognisable_marker_is_unclassified_not_error()
    {
        // "ok" must never absorb the unrecognised, or a failure shape this classifier has not seen arrives
        // looking like success — the exact defect the instrument exists to catch, reproduced inside the
        // instrument. When the handler signalled failure and no marker names it, say so: a rising
        // `unclassified` count is the signal that a new failure shape has appeared.
        MultiTurnAgentLoop.ClassifyResult("something went sideways", isError: true, isDeferred: false)
            .Should().Be("unclassified");

        // ...but a recognised marker still wins over the bare flag, so known shapes keep their own bucket.
        MultiTurnAgentLoop.ClassifyResult("File does not exist yet: /x", isError: true, isDeferred: false)
            .Should().Be("not-found");
    }

    [Fact]
    public void An_ordinary_result_is_ok_and_an_unrecognised_failure_is_not_invented_into_a_class()
    {
        // Deliberately conservative. An under-reported failure is a gap someone later closes; a fabricated
        // class is a wrong answer that reads exactly like a right one, and it would be trusted.
        MultiTurnAgentLoop.ClassifyResult(
                "public sealed class Foo { }", isError: false, isDeferred: false)
            .Should().Be("ok");

        // The honest limit, pinned so nobody mistakes `ok` for `succeeded`: a novel failure that ALSO
        // reports isError=false — the shape that produced #90 — is indistinguishable from real content by
        // text alone and lands in `ok`. ResultLength at Debug and the Trace preview are the backstop.
        MultiTurnAgentLoop.ClassifyResult(
                "the operation concluded unfavourably", isError: false, isDeferred: false)
            .Should().Be("ok");
    }

    [Fact]
    public void A_timeout_is_its_own_class_and_not_a_flavour_of_error()
    {
        // Both shapes were landing in "ok" — 302 of them across 22,564 live results, found by running this
        // classifier over the corpus rather than by reasoning about it. Timeout is separate from error on
        // purpose: an error says the attempt was wrong, a timeout says it may not have been, and that is the
        // one distinction that changes what an operator does next.
        MultiTurnAgentLoop.ClassifyResult(
                "Error: Error: Command timed out after 30 seconds\n", isError: false, isDeferred: false)
            .Should().Be("timeout");

        // The sub-agent wait envelope, reporting that what it waited on never reached terminal.
        MultiTurnAgentLoop.ClassifyResult(
                """{"status":"timeout","mode":"all","agents":{"requested":3,"running":3,"terminal":0}}""",
                isError: false,
                isDeferred: false)
            .Should().Be("timeout");
    }

    [Fact]
    public void A_transcript_visibility_refusal_is_a_denial()
    {
        // 223 occurrences in the live corpus, every one at offset 0 — the largest single denial shape there
        // is, and it shared no marker with any of the egress or approval refusals, so it was reported as a
        // successful tool call returning ordinary content.
        MultiTurnAgentLoop.ClassifyResult(
                "You cannot read that agent's transcript.", isError: false, isDeferred: false)
            .Should().Be("denied");
    }

    [Fact]
    public void The_egress_502_is_a_denial_in_every_rendering_it_arrives_in()
    {
        // One failure, three renderings, and only the first was recognised — so a count of egress denials
        // silently omitted 23 of them. This is the #50 population, which makes the omission the exact thing
        // #50 is trying to measure.
        MultiTurnAgentLoop.ClassifyResult(
                """{"attempts":1,"body":"","http_status":502,"status":"upstream_error"}""",
                isError: false,
                isDeferred: false)
            .Should().Be("denied");
        MultiTurnAgentLoop.ClassifyResult(
                "<class 'urllib.error.HTTPError'> HTTP Error 502: Bad Gateway", isError: false, isDeferred: false)
            .Should().Be("denied");
        MultiTurnAgentLoop.ClassifyResult("<HTTPError 502: 'Bad Gateway'>", isError: false, isDeferred: false)
            .Should().Be("denied");
    }

    [Fact]
    public void An_MCP_tool_failure_is_an_error_but_its_cancellation_is_a_timeout()
    {
        // The MCP envelope is one prefix covering every MCP-hosted tool, so the two must be separated by
        // what follows it rather than by which tool it was. 46 records, all previously "ok".
        MultiTurnAgentLoop.ClassifyResult(
                "Error executing MCP tool Skill: Request failed (remote): Skill not found",
                isError: false,
                isDeferred: false)
            .Should().Be("error");

        // Same prefix, and it must NOT come out as error: the call did not fail, it ran out of time.
        MultiTurnAgentLoop.ClassifyResult(
                "Error executing MCP tool Bash: The request was canceled due to the configured timeout",
                isError: false,
                isDeferred: false)
            .Should().Be("timeout");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("approval_denied", "approval_denied")]
    [InlineData("HTTP_502", "HTTP_502")]
    [InlineData("could not read /home/someone/secret.cs", null)]
    [InlineData("has\nnewline", null)]
    [InlineData("trailing ", null)]
    public void An_error_code_reaches_Debug_only_when_it_actually_looks_like_a_code(
        string? input,
        string? expected)
    {
        // ErrorCode is DOCUMENTED as a provider-specific code, but nothing enforces that, and this field is
        // logged at Debug where the EUII rule is absolute. Enforce the contract rather than trust it: a
        // provider that starts putting prose here costs a diagnostic, never a leak.
        MultiTurnAgentLoop.SafeErrorCode(input).Should().Be(expected);
    }

    [Fact]
    public void An_over_long_error_code_is_dropped_rather_than_truncated()
    {
        // Truncating would keep the first 64 characters of whatever prose a provider put there, which is
        // precisely the leak this guard exists to prevent. Drop it whole.
        MultiTurnAgentLoop.SafeErrorCode(new string('x', 65)).Should().BeNull();
        MultiTurnAgentLoop.SafeErrorCode(new string('x', 64)).Should().Be(new string('x', 64));
    }

    [Fact]
    public void A_deferred_placeholder_is_never_reported_as_empty()
    {
        // A deferred result carries an empty Result by construction and a real one arrives later. Bucketing
        // it as "empty" would manufacture a failure out of a tool that has not finished yet.
        MultiTurnAgentLoop.ClassifyResult("", isError: false, isDeferred: true)
            .Should().Be("deferred");
    }
}
