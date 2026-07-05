using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.3 — the versioned idempotency key is the <c>UNIQUE</c> guard that makes posting exactly-once
/// (plan §11). These tests pin the canonical shape: the <c>v1:</c> version prefix, a fixed segment
/// count regardless of provider (GitHub has no project layer → an empty segment, not a missing one),
/// case-folded human identity so casing drift never mints a second key, and rejection of components
/// that would corrupt the layout.
/// </summary>
public sealed class IdempotencyKeyTests
{
    private static IdempotencyKeyComponents GithubComponents() =>
        new(
            Provider: "github",
            OrgOrOwner: "acme",
            Project: null,
            RepoStableId: "R_node_123",
            PrId: "7",
            Operation: "post-review-comment",
            ArtifactKind: "review",
            ArtifactSubject: "summary",
            HeadSha: "wm-1",
            VariantId: "primary");

    [Fact]
    public void Build_renders_the_canonical_v1_shape()
    {
        var key = IdempotencyKey.Build(GithubComponents());

        key.Should().Be("v1:github:acme::R_node_123:7:post-review-comment:review:summary:wm-1:primary");
    }

    [Fact]
    public void Build_emits_an_empty_segment_for_a_null_project_keeping_the_segment_count_fixed()
    {
        var github = IdempotencyKey.Build(GithubComponents());
        var ado = IdempotencyKey.Build(GithubComponents() with
        {
            Provider = "azure-devops",
            Project = "Platform",
        });

        // Both keys have exactly the same number of segments — the project slot is present either way.
        github.Split(':').Should().HaveCount(11);
        ado.Split(':').Should().HaveCount(11);
        github.Split(':')[3].Should().BeEmpty("a null project still occupies its segment");
        ado.Split(':')[3].Should().Be("platform");
    }

    [Fact]
    public void Build_case_folds_the_human_identity_but_not_the_opaque_stable_id()
    {
        var key = IdempotencyKey.Build(GithubComponents() with
        {
            Provider = "GitHub",
            OrgOrOwner = "Acme",
            RepoStableId = "R_NoDe_123",
        });

        key.Should().Contain(":github:acme:");
        key.Should().Contain(":R_NoDe_123:", "the opaque provider id is preserved verbatim");
    }

    [Fact]
    public void Build_is_deterministic_for_the_same_components()
    {
        IdempotencyKey.Build(GithubComponents()).Should().Be(IdempotencyKey.Build(GithubComponents()));
    }

    [Fact]
    public void Build_distinguishes_variant_and_subject()
    {
        var primary = IdempotencyKey.Build(GithubComponents());
        var variantB = IdempotencyKey.Build(GithubComponents() with { VariantId = "b" });
        var otherSubject = IdempotencyKey.Build(GithubComponents() with { ArtifactSubject = "finding-42" });

        variantB.Should().NotBe(primary);
        otherSubject.Should().NotBe(primary);
    }

    [Fact]
    public void Build_rejects_a_blank_required_component()
    {
        var act = () => IdempotencyKey.Build(GithubComponents() with { PrId = "  " });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_rejects_a_component_containing_the_separator()
    {
        var act = () => IdempotencyKey.Build(GithubComponents() with { ArtifactSubject = "a:b" });

        act.Should().Throw<ArgumentException>("a stray ':' would shift every following segment");
    }
}
