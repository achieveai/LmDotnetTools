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
}
