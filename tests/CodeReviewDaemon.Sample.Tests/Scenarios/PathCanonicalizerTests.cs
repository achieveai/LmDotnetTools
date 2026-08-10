using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.1 — the path canonicalizer is the textual gate against traversal/UNC/absolute escapes in
/// attacker-influenced paths (submodule paths, repo-created paths). Fail-closed: anything not an
/// obviously-safe in-scope relative path is rejected.
/// </summary>
public sealed class PathCanonicalizerTests
{
    [Theory]
    [InlineData("a/b/c", "a/b/c")]
    [InlineData("a/./b", "a/b")]
    [InlineData("a/b/../c", "a/c")]
    [InlineData("a//", null)] // empty segment rejected
    [InlineData("vendor/libs/core", "vendor/libs/core")]
    public void Canonicalizes_safe_relative_paths(string raw, string? expected)
    {
        var ok = PathCanonicalizer.TryCanonicalizeRelative(raw, out var canonical, out _);

        if (expected is null)
        {
            ok.Should().BeFalse();
        }
        else
        {
            ok.Should().BeTrue();
            canonical.Should().Be(expected);
        }
    }

    [Theory]
    [InlineData("../etc/passwd")] // escapes root
    [InlineData("a/../../b")] // escapes after descending
    [InlineData("/etc/passwd")] // absolute
    [InlineData("C:/Windows/System32")] // drive-qualified
    [InlineData("a\\b")] // backslash / UNC vector
    [InlineData("\\\\server\\share")] // UNC
    [InlineData("")] // empty
    [InlineData("   ")] // whitespace
    [InlineData(".")] // resolves to root
    public void Rejects_escapes_and_absolute_and_windows_paths(string raw)
    {
        var ok = PathCanonicalizer.TryCanonicalizeRelative(raw, out var canonical, out var error);

        ok.Should().BeFalse();
        canonical.Should().BeEmpty();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Rejects_a_nul_byte()
    {
        var ok = PathCanonicalizer.TryCanonicalizeRelative("a/b\0c", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("NUL");
    }

    [Fact]
    public void NormalizeForComparison_is_case_insensitive_and_nfc()
    {
        // Guards the LmDotnetTools vs LmDotNetTools casing-drift hazard (plan §7).
        PathCanonicalizer
            .NormalizeForComparison("LmDotNetTools")
            .Should()
            .Be(PathCanonicalizer.NormalizeForComparison("lmdotnettools"));
    }

    [Fact]
    public void NormalizePathForComparison_makes_an_escaped_and_a_literal_space_compare_equal()
    {
        // An ADO project named "O365 Core" can only be spelled O365%20Core inside a URL, while the operator
        // configures the real name. Both must reduce to the same comparison key or the repo never matches
        // its own allow rule.
        PathCanonicalizer
            .NormalizePathForComparison("/o365exchange/O365%20Core/_git/WeveNova")
            .Should()
            .Be(PathCanonicalizer.NormalizePathForComparison("/o365exchange/O365 Core/_git/WeveNova"));
    }

    [Fact]
    public void NormalizePathForComparison_decodes_percent_encoded_traversal_so_callers_can_see_it()
    {
        // The whole point of decoding BEFORE the caller's '..' check: a raw '%2e%2e' hides the traversal
        // from a check run on the un-decoded text. After this call it is plainly visible.
        PathCanonicalizer
            .NormalizePathForComparison("/repo/%2e%2e/secrets")
            .Should()
            .Contain("..");
    }

    [Fact]
    public void NormalizePathForComparison_decodes_only_once_so_double_encoding_fails_closed()
    {
        // %252e%252e must NOT become '..' — one decode yields the literal text '%2e%2e', which matches no
        // allow rule. Decoding to a fixed point would instead resurrect the traversal.
        var normalized = PathCanonicalizer.NormalizePathForComparison("/repo/%252e%252e/secrets");

        normalized.Should().NotContain("..");
        normalized.Should().Be("/repo/%2e%2e/secrets");
    }

    [Fact]
    public void NormalizePathForComparison_leaves_a_malformed_escape_as_written()
    {
        // A dangling '%' is not decodable; comparing it as written is the fail-closed outcome (it matches no
        // real repo path) and must not fault the policy decision.
        PathCanonicalizer
            .NormalizePathForComparison("/acme/100%-coverage")
            .Should()
            .Be("/acme/100%-coverage");
    }
}
