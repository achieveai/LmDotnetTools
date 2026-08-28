using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// Pins <see cref="DeveloperIdentity.SlugifyAuthor"/> against the <see cref="ReviewFeedbackAgent"/>
/// implementation it was copied from, for as long as both exist.
/// <para>
/// The phase-1 patch describes the function as MOVED. It is copied: the original is still live at
/// <c>ReviewFeedbackAgent.SlugifyAuthor</c> and stays live until the phase-3 retirement deletes that class.
/// So the tree currently holds two implementations of the same security boundary — the character class is
/// what closes path traversal by construction, and a divergence between the two would be a traversal hole
/// in one of them that every test of the other still passes.
/// </para>
/// <para>
/// A comment saying "keep these in sync" is not a control; this test is. It exists to be DELETED together
/// with <see cref="ReviewFeedbackAgent"/> in phase 3, at which point there is one implementation and
/// nothing left to disagree. If you are here because it failed, the two have drifted — reconcile them
/// rather than updating the expectation, and check which one the traversal cases still hold for.
/// </para>
/// </summary>
public sealed class DeveloperIdentitySlugParityTests
{
    /// <summary>
    /// Cases chosen for what they discriminate, not for coverage: bot suppression, case folding (one
    /// developer must not split across two records), the traversal payloads the character class exists to
    /// neutralise, and inputs with no alphanumeric content at all, which must collapse to the same "no
    /// addressable identity" answer rather than to an empty directory name.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jane.doe@contoso.com")]
    [InlineData("Jane.Doe@Contoso.com")]
    [InlineData("JANE DOE")]
    [InlineData("dependabot[bot]")]
    [InlineData("Dependabot[BOT]")]
    [InlineData("../../.git/hooks/x")]
    [InlineData("../..")]
    [InlineData("a/b/c")]
    [InlineData("...")]
    [InlineData("-")]
    [InlineData("--leading-and-trailing--")]
    [InlineData("Ünïcödé Nàme")]
    [InlineData("user+tag@example.co.uk")]
    public void The_copied_slug_function_still_agrees_with_the_original_it_was_taken_from(string? author) =>
        DeveloperIdentity
            .SlugifyAuthor(author)
            .Should()
            .Be(
                ReviewFeedbackAgent.SlugifyAuthor(author),
                "two live implementations of one security boundary must not disagree; the copy in "
                    + "DeveloperLearnings was taken verbatim and nothing else pins it to its source"
            );
}
