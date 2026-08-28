using CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// Direct tests for the function that turns a PR author into a directory name.
/// <para>
/// <b>Why these exist.</b> Phase 1 shipped <see cref="DeveloperIdentity"/> with no tests of its own. Its only
/// guard was <see cref="DeveloperIdentitySlugParityTests"/>, which pins it against the copy in
/// <c>ReviewFeedbackAgent</c> and is scheduled for deletion with that class — so the moment the original goes,
/// the coverage goes with it. A mutation changing <c>"[bot]"</c> to <c>"[robot]"</c> in this file was killed
/// only by those parity cases and by nothing else in the suite.
/// </para>
/// <para>
/// <b>What this function actually is.</b> The output is a PATH SEGMENT. The character class is not
/// formatting — it is what makes <c>../</c> unconstructible rather than filtered, and a filter that is only
/// asserted through another implementation's behaviour is not asserted at all.
/// </para>
/// </summary>
public sealed class DeveloperIdentityTests
{
    /// <summary>
    /// Slug shape: lowercase alphanumerics and hyphens, never leading with a hyphen, always ending in the
    /// twelve-hex-character fingerprint. Anything outside this cannot name a parent directory.
    /// </summary>
    private const string SafeSegment = "^[a-z0-9][a-z0-9-]*-[0-9a-f]{12}$";

    [Theory]
    [InlineData("dependabot[bot]")]
    [InlineData("Dependabot[BOT]")]
    [InlineData("renovate[Bot]")]
    [InlineData("github-actions[bot]")]
    [InlineData("some-service[bot]   ")]
    public void A_bot_identity_gets_no_record(string author) =>
        // §3: automation is not a developer, and a bot's learnings file would be a file nobody can act on.
        // This is the assertion the [bot] -> [robot] mutation must fail, and it is deliberately separate from
        // the substring test below so neither can be satisfied by the other's implementation.
        DeveloperIdentity.SlugifyAuthor(author).Should().BeNull();

    [Theory]
    [InlineData("robot-jane")]
    [InlineData("abbot")]
    [InlineData("jane[bot]smith")]
    [InlineData("bot")]
    public void A_name_that_merely_contains_bot_is_a_person(string author) =>
        // The check is a SUFFIX, and it has to stay one. A substring check would silently erase every
        // developer whose name happens to contain those three letters, and the erasure would look exactly
        // like "this developer has no findings yet".
        DeveloperIdentity.SlugifyAuthor(author).Should().NotBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void An_absent_author_gets_no_record(string? author) =>
        DeveloperIdentity.SlugifyAuthor(author).Should().BeNull();

    [Theory]
    [InlineData("...")]
    [InlineData("-")]
    [InlineData("///")]
    [InlineData("@@@")]
    [InlineData("../..")]
    public void A_name_with_no_alphanumeric_content_gets_no_record(string author) =>
        // Not an empty slug — no record at all. An empty slug would be a directory name of "" or of the bare
        // fingerprint, and the first of those is the current directory.
        DeveloperIdentity.SlugifyAuthor(author).Should().BeNull();

    [Theory]
    [InlineData("../../.git/hooks/pre-commit")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("a/b/c")]
    [InlineData("jane\0doe")]
    [InlineData("Ünïcödé Nàme")]
    [InlineData("jane.doe@contoso.com")]
    [InlineData("~root")]
    [InlineData(".hidden")]
    [InlineData("name with\nnewline")]
    public void Every_slug_this_can_produce_is_a_safe_path_segment(string author)
    {
        var slug = DeveloperIdentity.SlugifyAuthor(author);

        // Stated as a property over hostile inputs rather than as golden values, because the guarantee that
        // matters is "nothing outside the character class can ever come out", and a golden value only ever
        // proves that one input behaved.
        _ = slug.Should().MatchRegex(SafeSegment);
        _ = slug.Should().NotContain("/").And.NotContain("\\").And.NotContain("..");
    }

    [Fact]
    public void Case_does_not_split_one_developer_across_two_directories()
    {
        // Two providers reporting the same person with different casing must land in one directory. If they
        // did not, each half would show a shorter history and a lower rate than the truth, and both would
        // look internally consistent.
        var lower = DeveloperIdentity.SlugifyAuthor("jane.doe@contoso.com");
        var upper = DeveloperIdentity.SlugifyAuthor("Jane.Doe@CONTOSO.com");

        upper.Should().Be(lower);
    }

    [Fact]
    public void Two_authors_whose_names_slugify_alike_stay_distinct()
    {
        // "jane doe" and "jane-doe" both slugify to jane-doe. Without the fingerprint they would share a
        // directory, and one person's record would carry another's findings.
        var spaced = DeveloperIdentity.SlugifyAuthor("jane doe");
        var hyphenated = DeveloperIdentity.SlugifyAuthor("jane-doe");

        _ = spaced.Should().StartWith("jane-doe-");
        _ = hyphenated.Should().StartWith("jane-doe-");
        spaced.Should().NotBe(hyphenated);
    }

    [Fact]
    public void Separator_runs_collapse_and_never_reach_an_edge()
    {
        // A leading or trailing hyphen would make the slug fail its own shape check, and a run of separators
        // would produce "jane--doe", which is a different directory from "jane-doe" for no reason a human
        // would predict.
        DeveloperIdentity
            .SlugifyAuthor("--Jane   ...   Doe--")
            .Should()
            .StartWith("jane-doe-")
            .And.MatchRegex(SafeSegment);
    }

    [Fact]
    public void The_fingerprint_is_twelve_hex_characters()
    {
        // Six bytes of SHA-256. Pinned because shortening it silently raises the collision rate between two
        // real developers, and a collision here merges two people's records without any error.
        var slug = DeveloperIdentity.SlugifyAuthor("jane.doe@contoso.com");

        slug.Should().NotBeNull();
        slug![^13..].Should().MatchRegex("^-[0-9a-f]{12}$");
    }
}
