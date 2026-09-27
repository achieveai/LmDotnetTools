using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmAgentInfra.Security;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class WorkflowOperationDispatcherTests
{
    [Fact]
    public async Task Private_invocation_sentinels_are_never_exported_even_from_an_artifacts_filename()
    {
        using var fixture = new Fixture();
        const string token = "private-token-SENTINEL-0123456789";
        const string email = "private-person-SENTINEL@example.com";
        File.WriteAllText(
            Path.Combine(fixture.Directory, "artifacts", "review.json"),
            new JsonObject
            {
                ["Input"] = token,
                ["Output"] = email,
                ["Error"] = token,
            }.ToJsonString()
        );
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default);
        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle.Should().NotContain(token).And.NotContain(email).And.NotContain("Input").And.NotContain("Error");
        bundle.Should().Contain("summary.json");
    }

    [Fact]
    public async Task Valid_scope_dispatches_authoritative_statistics()
    {
        using var fixture = new Fixture();
        var output = await fixture.Dispatcher.DispatchAsync(
            "collect-statistics",
            fixture.Context,
            fixture.Admission,
            default
        );
        output["RunId"]!.GetValue<string>().Should().Be(fixture.Run.Id.ToString(CultureInfo.InvariantCulture));
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("run")]
    [InlineData("directory")]
    [InlineData("head")]
    [InlineData("input")]
    public async Task Scope_mismatch_is_rejected_before_operations(string mismatch)
    {
        using var fixture = new Fixture();
        var input = fixture.RetentionInput;
        if (mismatch == "run")
            fixture.Context["RunId"] = "9999";
        if (mismatch == "directory")
            fixture.Context["RunDirectory"] = Path.GetDirectoryName(fixture.Directory);
        if (mismatch == "head")
        {
            fixture.Scope["Admission"]!["HeadSha"] = "foreign";
            fixture.SaveScope();
        }
        if (mismatch == "input")
            input["Admission"]!["WindowId"] = "other";
        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        fixture.FactoryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Retention_freezes_allowlisted_files_and_reconciliation_does_not_push_again()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Directory, "artifacts", "review.json"), "{\"Raw\":\"original\"}");
        File.WriteAllText(Path.Combine(fixture.Directory, "secret.json"), "secret");
        (await fixture.Dispatcher.ReconcileAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default))
            .Should()
            .BeNull();
        var first = await fixture.Dispatcher.DispatchAsync(
            "retain-artifacts",
            fixture.Context,
            fixture.RetentionInput,
            default
        );
        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle
            .Should()
            .Contain("summary.json")
            .And.NotContain("original")
            .And.NotContain("secret")
            .And.NotContain("FrozenContext");
        File.WriteAllText(Path.Combine(fixture.Directory, "artifacts", "review.json"), "{\"Raw\":\"changed\"}");
        var count = fixture.Runner.Commands.Count;
        JsonNode
            .DeepEquals(
                await fixture.Dispatcher.ReconcileAsync(
                    "retain-artifacts",
                    fixture.Context,
                    fixture.RetentionInput,
                    default
                ),
                first
            )
            .Should()
            .BeTrue();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default);
        fixture.Runner.Commands.Should().HaveCount(count);
    }

    [Fact]
    public async Task Closure_reconciliation_never_merges_or_pushes()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Directory, "artifacts", "review.json"), "{}");
        var retained = await fixture.Dispatcher.DispatchAsync(
            "retain-artifacts",
            fixture.Context,
            fixture.RetentionInput,
            default
        );
        fixture.Runner.Commands.Clear();
        var closed = await fixture.Dispatcher.ReconcileAsync(
            "close-artifact-branch",
            fixture.Context,
            retained,
            default
        );
        closed!["Closed"]!.GetValue<bool>().Should().BeTrue();
        fixture
            .Runner.Commands.Should()
            .NotContain(command => command.Argv.Contains("push") || command.Argv.Contains("merge"));
    }

    [Fact]
    public async Task Closure_rejects_another_retention_receipt()
    {
        using var fixture = new Fixture();
        await fixture
            .Dispatcher.Invoking(value =>
                value.DispatchAsync(
                    "close-artifact-branch",
                    fixture.Context,
                    new JsonObject { ["ArtifactBranch"] = "review/other-7", ["RetainedSha"] = "foreign" },
                    default
                )
            )
            .Should()
            .ThrowAsync<InvalidOperationException>();
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_retention_freezes_inputs_before_any_git_commit()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Directory, "artifacts", "review.json"), "{\"Raw\":\"original\"}");
        fixture.Runner.OnArgvContainsFirst("push", new SandboxCommandResult(1, "", "rejected"));
        await fixture
            .Dispatcher.Invoking(value =>
                value.DispatchAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default)
            )
            .Should()
            .ThrowAsync<InvalidOperationException>();
        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle.Should().Contain("summary.json").And.NotContain("original");
        File.WriteAllText(Path.Combine(fixture.Directory, "artifacts", "review.json"), "{\"Raw\":\"changed\"}");
        (await fixture.Dispatcher.ReconcileAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default))
            .Should()
            .BeNull();
        fixture
            .Store.GetOutboxForRun(fixture.Run.Id)
            .Should()
            .ContainSingle()
            .Which.Status.Should()
            .Be(OutboxStatus.Pending);
    }

    [Fact]
    public async Task Empty_allowlist_never_retains_scope_or_other_files()
    {
        using var fixture = new Fixture();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default);
        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle.Should().Contain("summary.json").And.NotContain("FrozenContext").And.NotContain("scope.json");
    }

    [Fact]
    public async Task Validated_knowledge_edits_and_generated_indexes_share_the_retention_receipt()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Directory, "artifacts", "raw.json"),
            "{\"Raw\":\"unvalidated Edits are never executed\"}"
        );
        var input = fixture.RetentionInput;
        input["Extractions"] = new JsonArray(
            new JsonObject
            {
                ["Edits"] = new JsonArray(
                    new JsonObject
                    {
                        ["Path"] = "KnowledgeBase/widgets/contracts.md",
                        ["Content"] = "---\ntitle: Contracts\nscope: widgets\n---\nEvidence",
                    }
                ),
                ["Description"] = "Supported lesson",
            }
        );
        input["KnowledgeReview"] = ReviewedKnowledgeTestExtensions.Approved((JsonArray)input["Extractions"]!);
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle.Should().Contain("KnowledgeBase/widgets/contracts.md").And.Contain("KnowledgeBase/_index.jsonl");
        fixture
            .Store.GetOutboxForRun(fixture.Run.Id)
            .Should()
            .ContainSingle()
            .Which.Status.Should()
            .Be(OutboxStatus.Posted);
        fixture
            .Runner.Commands.Should()
            .Contain(command =>
                command.Argv.Contains("add") && command.Argv.Contains("KnowledgeBase/widgets/contracts.md")
            );
        var changed = fixture.RetentionInput;
        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, changed, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*bundle*");
    }

    [Fact]
    public async Task Changed_bundle_content_cannot_prove_retention_or_authorize_closure()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Directory, "artifacts", "review.json"), "{}");
        var retained = await fixture.Dispatcher.DispatchAsync(
            "retain-artifacts",
            fixture.Context,
            fixture.RetentionInput,
            default
        );
        var path = Path.Combine(fixture.Directory, "retention-bundle.json");
        var bundle = JsonNode.Parse(File.ReadAllText(path))!;
        bundle["Files"]![0]!["Content"] = "changed";
        File.WriteAllText(path, bundle.ToJsonString());
        var count = fixture.Runner.Commands.Count;
        (await fixture.Dispatcher.ReconcileAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default))
            .Should()
            .BeNull();
        (await fixture.Dispatcher.ReconcileAsync("close-artifact-branch", fixture.Context, retained, default))
            .Should()
            .BeNull();
        fixture.Runner.Commands.Should().HaveCount(count);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("missing")]
    [InlineData("changed")]
    public async Task Knowledge_review_failure_stops_before_any_repository_operation(string failure)
    {
        using var fixture = new Fixture();
        var input = fixture.RetentionInput;
        if (failure == "missing")
            input.Remove("KnowledgeReview");
        else if (failure == "rejected")
            input["KnowledgeReview"]!["Verdict"] = "rejected";
        else
            input["Extractions"]![0]!["Description"] = "Changed after review";
        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*safety review*");
        fixture.FactoryCalls.Should().Be(0);
        fixture.Runner.Commands.Should().BeEmpty();
        fixture.Store.GetOutboxForRun(fixture.Run.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_unrestricted_bundle_is_rejected_before_retention()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Directory, "retention-bundle.json"),
            "{\"WorkflowInstanceId\":\"instance-1\",\"ReviewRunId\":1,\"ExtractionHash\":\"legacy\",\"Files\":[]}"
        );
        await fixture
            .Dispatcher.Invoking(value =>
                value.DispatchAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default)
            )
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*bundle*");
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Canonical_draft_and_judge_are_private_and_retry_idempotent()
    {
        using var fixture = new Fixture();
        fixture.Admission["Route"] = "new_head";
        fixture.Scope["Admission"] = fixture.Admission.DeepClone();
        fixture.SaveScope();
        var canonical = new JsonObject
        {
            ["Review"] = new JsonObject { ["Findings"] = new JsonArray(), ["ReviewText"] = "private-draft-SENTINEL" },
            ["Grade"] = new JsonObject
            {
                ["Assessments"] = new JsonArray(),
                ["Description"] = "private-grade-SENTINEL",
            },
            ["Publication"] = new JsonObject
            {
                ["Outcome"] = "no_op",
                ["Actions"] = new JsonArray(),
                ["Description"] = "None",
            },
        };
        var input = new JsonObject
        {
            ["Admission"] = fixture.Admission.DeepClone(),
            ["Extractions"] = new JsonArray(),
            ["Canonical"] = canonical,
        };
        fixture.Store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = fixture.Run.Id,
                ArtifactKind = "workflow-diff",
                ArtifactSchemaVersion = 1,
                Provider = "github",
                Payload = $"{{\"BaseSha\":\"base\",\"HeadSha\":\"{Fixture.Head}\",\"Diff\":\"private-diff-SENTINEL\"}}",
            }
        );
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        var review = fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "review")!;
        review.Payload.Should().Contain("private-draft-SENTINEL").And.Contain("validated-draft");
        var judge = JsonNode.Parse(fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "judge")!.Payload)!;
        judge["Score"].Should().BeNull();
        judge["GradeKind"]!.GetValue<string>().Should().Be("per-finding-support");
        fixture
            .Store.TryGetLatestArtifact(fixture.Run.Id, "review-context")!
            .Payload.Should()
            .Contain("private-diff-SENTINEL");
        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle.Should().NotContain("SENTINEL");
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "review")!.Id.Should().Be(review.Id);
    }

    /// <summary>
    /// Task #82, requirement 2 — under the pilot grant the branch carries the run's complete review output
    /// as ordinary files, derived from the SAME canonical node the private store gets, with the reviewed
    /// diff body deliberately left behind.
    /// </summary>
    [Fact]
    public async Task The_pilot_grant_exports_the_whole_review_output_as_files_but_never_the_diff_body()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle
            .Should()
            .Contain("summary.json")
            .And.Contain("review.md")
            .And.Contain("findings.json")
            .And.Contain("grade.json")
            .And.Contain("context.json")
            .And.Contain("comments.json")
            .And.Contain("discussion.json")
            .And.Contain("publication.json")
            .And.Contain("identities.json")
            .And.Contain("receipts.json");
        // The review output IS on the branch…
        bundle.Should().Contain("private-draft-SENTINEL").And.Contain("private-grade-SENTINEL");
        // …and the reviewed source is not. Only its digest travels.
        bundle.Should().NotContain("private-diff-SENTINEL");
        // The frozen discussion travels as data.
        bundle.Should().Contain("existing-comment-SENTINEL");
        // Counts only: no idempotency key, no provider response id, no header, no environment value.
        bundle.Should().NotContain("IdempotencyKey").And.NotContain("ProviderResponseId");
    }

    [Fact]
    public async Task Without_the_export_grant_the_branch_still_receives_only_the_metadata_summary()
    {
        using var fixture = new Fixture();
        var input = fixture.SeedNewHeadReview();

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle.Should().Contain("summary.json").And.NotContain("review.md").And.NotContain("SENTINEL");
    }

    [Fact]
    public async Task Capability_is_resolved_when_retention_runs_instead_of_when_dispatcher_is_constructed()
    {
        var capability = ReviewArtifactBranchCapability.Denied;
        using var fixture = new Fixture(() => capability);
        var input = fixture.SeedNewHeadReview();
        capability = ReviewArtifactBranchCapability.Grant("github/example/widgets", "7", Fixture.Head);

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle.Should().Contain("\"ExportsFullReviewOutput\":true").And.Contain("review.md");
    }

    [Fact]
    public async Task Summary_only_cached_bundle_is_rejected_after_full_export_is_authorized()
    {
        var capability = Fixture.Capability;
        using var fixture = new Fixture(() => capability);
        var input = fixture.SeedNewHeadReview();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        fixture.Runner.Commands.Clear();
        capability = ReviewArtifactBranchCapability.Grant("github/example/widgets", "7", Fixture.Head);

        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*export authority*");

        fixture.Runner.Commands.Should().BeEmpty();
    }

    /// <summary>
    /// Shape coverage for the redaction of review prose. Each of these is a credential shape a model can
    /// plausibly emit while discussing the code it is reviewing; none of them may reach the branch, and
    /// none of them may stop the review being retained.
    /// </summary>
    [Theory]
    // Vendor prefixes.
    [InlineData("leaked ghp_0123456789abcdefghijklmnopqrstuvwx", "github-token")]
    [InlineData("leaked github_pat_11ABCDEFG0abcdefghijklmnop", "github-fine-grained-pat")]
    [InlineData("leaked xoxb-1234567890-abcdefghij", "slack-token")]
    [InlineData("leaked AKIAIOSFODNN7EXAMPLE", "aws-access-key-id")]
    // A TEMPORARY AWS key is just as live as a permanent one; keying only on AKIA missed it.
    [InlineData("leaked ASIAIOSFODNN7EXAMPLE", "aws-access-key-id")]
    [InlineData("leaked sk-abcdefghijklmnopqrstuvwx", "openai-secret-key")]
    [InlineData("leaked rk_live_abcdefghijklmnopqrst", "stripe-key")]
    [InlineData("leaked npm_abcdefghijklmnopqrstuvwxyz0123456789", "npm-token")]
    [InlineData("leaked AIzaSyA1234567890abcdefghijklmnopqrstuv", "google-api-key")]
    // Shapes with no vendor prefix at all, which a prefix-only scanner cannot see.
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r", "jwt")]
    [InlineData("clone https://deploy-user:s3cr3tvalue@github.com/example/widgets.git", "uri-userinfo")]
    [InlineData("Server=db;Shared Access Key=abc123def456;", "connection-string-field")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "pem-private-key")]
    // Generic labelled assignments, including the JSON-shaped form a model is most likely to emit.
    [InlineData("config had api_key = 9f8e7d6c5b4a3210", "labelled-secret-assignment")]
    [InlineData("{\"password\": \"hunter2\"}", "labelled-secret-assignment")]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "labelled-secret-assignment")]
    [InlineData("client_secret='abcdefghijklmnop'", "labelled-secret-assignment")]
    public async Task Every_credential_shape_in_review_prose_is_replaced_by_its_named_placeholder(
        string reviewText,
        string expectedShape
    )
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview(reviewText);

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var markdown = fixture.ExportedText("review.md");
        markdown.Should().Contain(CredentialPatterns.PlaceholderFor(expectedShape));
        markdown.Should().NotContain(reviewText);
        // The placeholder names the SHAPE and never the match: a redactor that echoes what it found has
        // copied the secret into the very output that was supposed to remove it.
        File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json")).Should().NotContain(reviewText);
    }

    /// <summary>
    /// The exported inventory has to mean the same thing whenever it is read. Retention enqueues its own
    /// outbox row and writes its own branch-receipt artifact as part of the operation being counted, so a
    /// snapshot taken before the push and one taken after it disagree — and the bundle is cached, which
    /// freezes whichever happened first. Excluding retention's own rows by name is what makes the numbers
    /// invariant instead of merely better-timed.
    /// </summary>
    [Fact]
    public async Task The_exported_inventory_counts_the_same_before_and_after_the_retention_it_describes()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();

        var beforeArtifacts = fixture.Store.GetArtifacts(fixture.Run.Id).Count;
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        // Retention really did add rows of both kinds — so an inventory that counted them WOULD have moved.
        fixture.Store.GetArtifacts(fixture.Run.Id).Count.Should().BeGreaterThan(beforeArtifacts);
        fixture
            .Store.GetOutboxForRun(fixture.Run.Id)
            .Should()
            .Contain(entry => entry.Operation == WorkflowArtifactOperations.RetentionOperation);
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, ReviewArtifactKinds.ArtifactBranchKind).Should().NotBeNull();

        var receipts = fixture.ExportedJson("receipts.json");
        receipts["SnapshotSemantics"]!.GetValue<string>().Should().Be("retention-independent");
        receipts["ExcludedOperations"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .Should()
            .Contain(WorkflowArtifactOperations.RetentionOperation);
        receipts["ExcludedArtifactKinds"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .Should()
            .Contain(ReviewArtifactKinds.ArtifactBranchKind);

        // The numeric claim, not just the labels: no retention receipt is counted, and no branch-receipt
        // artifact is counted — which is exactly why re-deriving the inventory now reproduces the file.
        receipts["ReceiptCount"]!.GetValue<int>().Should().Be(0);
        receipts["ReceiptCountsByStatus"]!.AsObject().Should().BeEmpty();
        receipts["ArtifactCountsByKind"]!
            .AsObject()
            .Select(property => property.Key)
            .Should()
            .NotContain(ReviewArtifactKinds.ArtifactBranchKind);
        receipts["ArtifactCount"]!
            .GetValue<int>()
            .Should()
            .Be(receipts["ArtifactCountsByKind"]!.AsObject().Sum(property => property.Value!.GetValue<int>()));
        receipts["ArtifactCount"]!
            .GetValue<int>()
            .Should()
            .Be(
                fixture
                    .Store.GetArtifacts(fixture.Run.Id)
                    .Count(artifact => artifact.ArtifactKind != ReviewArtifactKinds.ArtifactBranchKind)
            );
    }

    /// <summary>
    /// Untrusted free text is REDACTED, not refused. A reviewer pasting a token into a PR comment must not
    /// silently stop the daemon retaining anything for that pull request — the branch gets a stable,
    /// shape-naming placeholder and the review is captured.
    /// </summary>
    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r", "jwt")]
    [InlineData("https://deploy-user:s3cr3tvalue@github.com/example/widgets.git", "uri-userinfo")]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwx", "github-token")]
    public async Task A_secret_in_untrusted_free_text_is_redacted_on_the_branch_rather_than_aborting(
        string secret,
        string shape
    )
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        // Every untrusted surface at once: model prose, a finding description, and third-party discussion
        // on both the baseline and the window.
        var input = fixture.SeedNewHeadReview(
            reviewText: $"the config contained {secret} which is unsafe",
            findingDescription: $"hard-coded credential {secret} at line 12",
            discussionComment: $"I already rotated {secret}, ignore it"
        );

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        var placeholder = CredentialPatterns.PlaceholderFor(shape);
        // Not one byte of the original survives anywhere on the branch…
        bundle.Should().NotContain(secret);
        // …and the placeholder names what was removed, in every file that carried it.
        bundle.Should().Contain(placeholder);
        fixture.ExportedText("review.md").Should().Contain(placeholder).And.NotContain(secret);
        fixture.ExportedJson("findings.json").ToJsonString().Should().Contain(placeholder).And.NotContain(secret);
        fixture.ExportedJson("comments.json").ToJsonString().Should().Contain(placeholder).And.NotContain(secret);
        var discussion = fixture.ExportedJson("discussion.json").ToJsonString();
        discussion.Should().Contain(placeholder).And.NotContain(secret);
        // The surrounding prose is untouched — redaction removes the value, not the sentence.
        fixture.ExportedText("review.md").Should().Contain("which is unsafe");
    }

    /// <summary>
    /// The redaction is only on the way OUT. The private artifacts are the daemon's record of what was
    /// actually said and reviewed; they are never published, and rewriting them to protect a channel they
    /// do not travel on would destroy evidence.
    /// </summary>
    [Fact]
    public async Task Redaction_applies_to_the_branch_export_only_and_never_to_the_private_artifacts()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        const string secret = "ghp_0123456789abcdefghijklmnopqrstuvwx";
        var input = fixture.SeedNewHeadReview(reviewText: $"leaked {secret} here");

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "review")!.Payload.Should().Contain(secret);
        File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json")).Should().NotContain(secret);
    }

    [Fact]
    public async Task Redaction_is_deterministic_across_runs()
    {
        const string secret = "ghp_0123456789abcdefghijklmnopqrstuvwx";
        var rendered = new List<string>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var fixture = new Fixture(exportFullReviewOutput: true);
            var input = fixture.SeedNewHeadReview(reviewText: $"leaked {secret} here");
            await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
            rendered.Add(fixture.ExportedText("review.md"));
        }

        rendered[0].Should().Be(rendered[1]);
    }

    /// <summary>
    /// Daemon-authored structured values must never contain a credential, so those still abort. The
    /// universal gate is what enforces it: free text was already redacted by the time it runs, so the only
    /// thing left that can match is a trusted field.
    /// </summary>
    [Fact]
    public async Task A_secret_in_a_daemon_authored_identifier_still_aborts_the_export()
    {
        // VariantId is daemon configuration written into identities.json — it is not model output and not
        // third-party text, so it is never redacted and the universal gate is the only thing between it
        // and the branch.
        using var fixture = new Fixture(
            exportFullReviewOutput: true,
            variantId: "ghp_0123456789abcdefghijklmnopqrstuvwx"
        );
        var input = fixture.SeedNewHeadReview();

        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*github-token*");
        fixture.Runner.Commands.Should().NotContain(command => command.Argv.Contains("push"));
    }

    /// <summary>
    /// The export is gated on the ROUTE, not only on the capability. A <c>merged</c> or <c>discussion</c>
    /// run has no canonical review in the private store — <c>PersistCanonical</c> refuses to write one off
    /// that route — so a Canonical node arriving in the operation input is malformed or someone else's.
    /// Publishing it would put content on the branch that the daemon never validated and never recorded.
    /// </summary>
    [Theory]
    [InlineData("merged")]
    [InlineData("discussion")]
    public async Task The_full_review_export_cannot_activate_off_the_new_head_route(string route)
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview(route: route);
        // Only `merged` carries knowledge extractions, and only then does the operation require their
        // safety review; on `discussion` a non-empty extraction list is itself rejected.
        if (route == "merged")
        {
            var extractions = new JsonArray(new JsonObject { ["Edits"] = new JsonArray(), ["Description"] = "None" });
            input["Extractions"] = extractions.DeepClone();
            input["KnowledgeReview"] = ReviewedKnowledgeTestExtensions.Approved(extractions);
        }

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var bundle = File.ReadAllText(Path.Combine(fixture.Directory, "retention-bundle.json"));
        bundle.Should().Contain("summary.json");
        foreach (var name in new[] { "review.md", "findings.json", "grade.json", "comments.json", "discussion.json" })
        {
            bundle.Should().NotContain(name);
        }
        bundle.Should().NotContain("private-draft-SENTINEL").And.NotContain("private-grade-SENTINEL");
    }

    /// <summary>
    /// A replayed retention must not append a second branch receipt. The redo path reads the LATEST one to
    /// prove ownership before it deletes a remote branch, so duplicates at different SHAs would be a
    /// deletion decided by row order.
    /// </summary>
    [Fact]
    public async Task A_replayed_retention_records_exactly_one_branch_receipt()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        _ = await fixture.Dispatcher.ReconcileAsync("retain-artifacts", fixture.Context, input, default);
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        fixture.Store.ListArtifacts(fixture.Run.Id, ReviewArtifactKinds.ArtifactBranchKind).Should().ContainSingle();
    }

    /// <summary>
    /// A scan or redaction that cannot COMPLETE still aborts: partially-redacted text is indistinguishable
    /// from clean text. And the refusal must not carry the input — <see cref="RegexMatchTimeoutException"/>
    /// keeps the text it was matching in its <c>Input</c> property, so retaining it as an InnerException
    /// would write the unscanned bytes into every log that renders the exception chain.
    /// </summary>
    [Fact]
    public void A_scan_timeout_aborts_without_putting_the_content_in_the_exception()
    {
        const string content = "ghp_0123456789abcdefghijklmnopqrstuvwx and more secret prose";
        var file = new ReviewArtifactFile("PRs/widgets-7/inst/review.md", content);

        var refuse = () =>
            ReviewArtifactExport.ValidateNoSecrets(
                file,
                text => throw new RegexMatchTimeoutException(text, "pattern", TimeSpan.FromMilliseconds(1))
            );

        var thrown = refuse.Should().Throw<InvalidOperationException>().Which;
        thrown.InnerException.Should().BeNull();
        thrown.ToString().Should().NotContain(content).And.NotContain("ghp_");
        thrown.Message.Should().Contain("review.md").And.Contain("match timeout");
    }

    [Fact]
    public void A_redaction_timeout_aborts_without_putting_the_content_in_the_exception()
    {
        const string content = "ghp_0123456789abcdefghijklmnopqrstuvwx and more secret prose";

        var refuse = () =>
            ReviewArtifactExport.Redact(
                "PRs/widgets-7/inst/discussion.json",
                content,
                text => throw new RegexMatchTimeoutException(text, "pattern", TimeSpan.FromMilliseconds(1))
            );

        var thrown = refuse.Should().Throw<InvalidOperationException>().Which;
        thrown.InnerException.Should().BeNull();
        thrown.ToString().Should().NotContain(content).And.NotContain("ghp_");
    }

    /// <summary>
    /// <c>review.md</c> is prose, so its binding is a trailer rather than a JSON property — and it is
    /// checked on replay exactly as every other file's is.
    /// </summary>
    [Fact]
    public async Task The_review_prose_carries_a_machine_readable_binding_trailer()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var markdown = fixture.ExportedText("review.md");
        markdown.Should().Contain(ReviewArtifactExport.BindingMarkerPrefix);
        var trailer = markdown[
            markdown.LastIndexOf(ReviewArtifactExport.BindingMarkerPrefix, StringComparison.Ordinal)..
        ];
        var json = trailer[
            ReviewArtifactExport.BindingMarkerPrefix.Length..trailer.LastIndexOf(
                ReviewArtifactExport.BindingMarkerSuffix,
                StringComparison.Ordinal
            )
        ];
        var parsed = JsonNode.Parse(json)!.AsObject();
        parsed["ReviewRunId"]!.GetValue<long>().Should().Be(fixture.Run.Id);
        parsed["ArtifactBranch"]!.GetValue<string>().Should().Be("review/widgets-7");
    }

    [Fact]
    public async Task A_replayed_review_prose_with_a_tampered_binding_trailer_is_refused()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        fixture.RewriteExportedText("review.md", markdown => markdown.Replace("\"PrId\":\"7\"", "\"PrId\":\"8\""));

        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to a different review run*");
    }

    [Fact]
    public async Task A_replayed_review_prose_whose_trailer_was_stripped_is_refused()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        fixture.RewriteExportedText(
            "review.md",
            markdown =>
                markdown[..markdown.LastIndexOf(ReviewArtifactExport.BindingMarkerPrefix, StringComparison.Ordinal)]
        );

        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to a different review run*");
    }

    /// <summary>
    /// A model can write anything into the review body, including something that looks exactly like a
    /// binding trailer for another run. It is harmless because the exporter appends the real trailer
    /// afterwards and validation requires the content to END with it.
    /// </summary>
    [Fact]
    public async Task A_forged_binding_trailer_inside_the_review_text_does_not_become_the_binding()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var forged =
            ReviewArtifactExport.BindingMarkerPrefix
            + "{\"ReviewRunId\":999,\"PrId\":\"8\",\"RepoKey\":\"example/other\",\"BaseSha\":\"x\","
            + "\"HeadSha\":\"y\",\"ArtifactBranch\":\"review/other-8\"}"
            + ReviewArtifactExport.BindingMarkerSuffix;
        var input = fixture.SeedNewHeadReview(reviewText: "findings follow\n\n" + forged);

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var markdown = fixture.ExportedText("review.md");
        markdown.Should().Contain("999");
        markdown.Should().EndWith(ReviewArtifactExport.BindingTrailer(fixture.Binding));
    }

    [Fact]
    public async Task The_exported_context_states_that_it_carries_a_diff_digest_and_not_the_source_body()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        var context = fixture.ExportedJson("context.json");
        context["DiffBodyExported"]!.GetValue<bool>().Should().BeFalse();
        context["Notice"]!.GetValue<string>().Should().Be(ReviewArtifactExport.DiffBodyNotice);
        context["Diff"]!["Sha256"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        context.ToJsonString().Should().NotContain("private-diff-SENTINEL");
    }

    /// <summary>
    /// Every exported JSON file carries the run identity it describes, and the cached bundle is re-validated
    /// against the CURRENT scope on replay. The bundle envelope's own run check is not enough on its own: it
    /// proves the envelope names this run, not that the files inside were not lifted from another one.
    /// </summary>
    [Fact]
    public async Task Every_exported_file_is_bound_to_the_run_pr_and_branch_it_describes()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        foreach (var name in new[] { "findings.json", "grade.json", "context.json", "identities.json" })
        {
            var binding = fixture.ExportedJson(name)["Binding"]!.AsObject();
            binding["ReviewRunId"]!.GetValue<long>().Should().Be(fixture.Run.Id);
            binding["PrId"]!.GetValue<string>().Should().Be("7");
            binding["HeadSha"]!.GetValue<string>().Should().Be(Fixture.Head);
            binding["ArtifactBranch"]!.GetValue<string>().Should().Be("review/widgets-7");
        }
    }

    [Theory]
    [InlineData("ReviewRunId", 987654)]
    [InlineData("PrId", "8")]
    [InlineData("RepoKey", "example/other")]
    [InlineData("HeadSha", "cccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("ArtifactBranch", "review/widgets-8")]
    public async Task A_replayed_file_bound_to_a_different_run_is_refused(string field, object tampered)
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        // Rewrite ONE binding field in the cached bundle and replay it, exactly as a resume would.
        fixture.RewriteExported(
            "findings.json",
            payload =>
                payload["Binding"]!.AsObject()[field] = tampered is int number
                    ? JsonValue.Create(number)
                    : JsonValue.Create((string)tampered)
        );

        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to a different review run*");
    }

    [Fact]
    public async Task A_replayed_file_that_dropped_its_binding_entirely_is_refused()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        fixture.RewriteExported("identities.json", payload => payload.Remove("Binding"));

        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to a different review run*");
    }

    [Fact]
    public async Task A_replayed_file_that_had_a_credential_added_to_it_is_refused()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);

        // Valid binding, tampered body: the binding check and the secret scan catch different things and
        // neither subsumes the other.
        fixture.RewriteExported(
            "grade.json",
            payload => payload["Description"] = "ghp_0123456789abcdefghijklmnopqrstuvwx"
        );

        await fixture
            .Dispatcher.Invoking(value => value.DispatchAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*github-token*");
    }

    [Fact]
    public async Task Full_export_captures_bound_notes_and_replay_does_not_capture_or_push_again()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        var note = new JsonObject
        {
            ["RelativePath"] = "Memory/tasks/pr-7-review.md",
            ["Sha256"] = Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Finding: retry drops the note")))
                .ToLowerInvariant(),
            ["Content"] = "Finding: retry drops the note",
        };
        fixture.SetCapturedNotes(new JsonArray(note.DeepClone()).ToJsonString());

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        var notes = fixture.ExportedJson("review-notes.json");
        notes["Binding"]!["ReviewRunId"]!.GetValue<long>().Should().Be(fixture.Run.Id);
        notes["Files"]![0]!.ToJsonString().Should().Be(note.ToJsonString());
        fixture
            .Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-review-notes")!
            .Payload.Should()
            .Contain("retry drops");
        var commands = fixture.Runner.Commands;
        commands
            .Count(command => command.Argv.Any(arg => arg.Contains("notes.append", StringComparison.Ordinal)))
            .Should()
            .Be(1);
        commands.Count(command => command.Argv.Contains("push")).Should().Be(1);
        commands
            .FindIndex(command => command.Argv.Any(arg => arg.Contains("notes.append", StringComparison.Ordinal)))
            .Should()
            .BeLessThan(commands.FindIndex(command => command.Argv.Contains("push")));

        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        commands
            .Count(command => command.Argv.Any(arg => arg.Contains("notes.append", StringComparison.Ordinal)))
            .Should()
            .Be(1);
        commands.Count(command => command.Argv.Contains("push")).Should().Be(1);
    }

    [Fact]
    public async Task Empty_capture_still_exports_bound_notes()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        fixture.ExportedJson("review-notes.json")["Files"]!.AsArray().Should().BeEmpty();
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-review-notes")!.Payload.Should().Be("[]");
    }

    [Fact]
    public async Task Failed_capture_prevents_bundle_and_retention()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        fixture.SetCapturedNotes("", exitCode: 1);
        await fixture
            .Dispatcher.Invoking(dispatcher =>
                dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default)
            )
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*capture failed*");
        File.Exists(Path.Combine(fixture.Directory, "retention-bundle.json")).Should().BeFalse();
        fixture.Store.GetOutboxForRun(fixture.Run.Id).Should().BeEmpty();
        fixture.Runner.Commands.Should().NotContain(command => command.Argv.Contains("push"));
    }

    [Fact]
    public async Task Cached_full_export_without_notes_is_refused_without_another_push()
    {
        using var fixture = new Fixture(exportFullReviewOutput: true);
        var input = fixture.SeedNewHeadReview();
        await fixture.Dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default);
        fixture.RemoveExported("review-notes.json");
        var count = fixture.Runner.Commands.Count;
        await fixture
            .Dispatcher.Invoking(dispatcher =>
                dispatcher.DispatchAsync("retain-artifacts", fixture.Context, input, default)
            )
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing its bound review notes*");
        (await fixture.Dispatcher.ReconcileAsync("retain-artifacts", fixture.Context, input, default))
            .Should()
            .BeNull();
        fixture.Runner.Commands.Should().HaveCount(count);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _db = new();
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "workflow-dispatch-" + Guid.NewGuid().ToString("N")
        );
        private readonly SemaphoreSlim _gate = new(1, 1);
        public ReviewStore Store { get; }
        public ReviewRun Run { get; }
        public RepoIdentity Repo { get; private set; } = null!;
        public FakeSandboxCommandRunner Runner { get; } = new();
        public WorkflowWorkspace Workspace { get; }
        public string Directory { get; }
        private bool _manualCapture;
        public const string Head = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private string BundlePath => Path.Combine(Directory, "retention-bundle.json");

        /// <summary>Reads one exported file back out of the cached retention bundle.</summary>
        public JsonObject ExportedJson(string fileName) => JsonNode.Parse(ExportedText(fileName))!.AsObject();

        /// <summary>The raw text of one exported file, for the prose file that is not JSON.</summary>
        public string ExportedText(string fileName) =>
            ExportedFiles().Single(file => Name(file) == fileName)["Content"]!.GetValue<string>();

        /// <summary>The identity every exported file for this fixture's run must be bound to.</summary>
        public ReviewArtifactExportBinding Binding => ReviewArtifactExportBinding.Build(Run, Repo, "review/widgets-7");

        public void SetCapturedNotes(string content, int exitCode = 0) =>
            Runner.OnArgvContainsFirst("notes.append", new SandboxCommandResult(exitCode, content, ""));

        public void RemoveExported(string fileName)
        {
            var bundle = JsonNode.Parse(File.ReadAllText(BundlePath))!.AsObject();
            var files = bundle["Files"]!.AsArray();
            files.Remove(files.Single(entry => Name(entry!.AsObject()) == fileName));
            File.WriteAllText(BundlePath, bundle.ToJsonString());
        }

        /// <summary>Rewrites one exported file's raw text in the cached bundle.</summary>
        public void RewriteExportedText(string fileName, Func<string, string> mutate)
        {
            var bundle = JsonNode.Parse(File.ReadAllText(BundlePath))!.AsObject();
            var file = bundle["Files"]!.AsArray().Single(entry => Name(entry!.AsObject()) == fileName)!.AsObject();
            file["Content"] = mutate(file["Content"]!.GetValue<string>());
            File.WriteAllText(BundlePath, bundle.ToJsonString());
        }

        /// <summary>
        /// Rewrites one exported file IN the cached bundle, which is what a resume then replays. This is
        /// the only way to reach the read-back validation: on the build path the file is whatever the
        /// exporter produced, so a tamper has to happen between the write and the replay.
        /// </summary>
        public void RewriteExported(string fileName, Action<JsonObject> mutate)
        {
            var bundle = JsonNode.Parse(File.ReadAllText(BundlePath))!.AsObject();
            var file = bundle["Files"]!.AsArray().Single(entry => Name(entry!.AsObject()) == fileName)!.AsObject();
            var payload = JsonNode.Parse(file["Content"]!.GetValue<string>())!.AsObject();
            mutate(payload);
            file["Content"] = payload.ToJsonString();
            File.WriteAllText(BundlePath, bundle.ToJsonString());
        }

        private IEnumerable<JsonObject> ExportedFiles() =>
            JsonNode.Parse(File.ReadAllText(BundlePath))!.AsObject()["Files"]!
                .AsArray()
                .Select(node => node!.AsObject());

        private static string Name(JsonObject file)
        {
            var path = file["RelativePath"]!.GetValue<string>();
            return path[(path.LastIndexOf('/') + 1)..];
        }

        /// <summary>
        /// Push authorized, export NOT widened — the daemon's own posture. The pilot fixture below flips
        /// only the export flag, so every difference in the retained bundle is attributable to it.
        /// </summary>
        public static readonly ReviewArtifactBranchCapability Capability = ReviewArtifactBranchCapability.Grant(
            "github/example/widgets",
            "7",
            Head,
            exportsFullReviewOutput: false
        );
        public JsonObject Context { get; }
        public JsonObject Admission { get; } =
            new()
            {
                ["Route"] = "merged",
                ["PrId"] = "7",
                ["HeadSha"] = Head,
                ["WindowId"] = "window",
            };
        public JsonObject RetentionInput =>
            new()
            {
                ["Admission"] = Admission.DeepClone(),
                ["Extractions"] = new JsonArray(
                    new JsonObject { ["Edits"] = new JsonArray(), ["Description"] = "None" }
                ),
                ["KnowledgeReview"] = ReviewedKnowledgeTestExtensions.Approved(
                    new JsonArray(new JsonObject { ["Edits"] = new JsonArray(), ["Description"] = "None" })
                ),
            };
        public JsonObject Scope { get; }
        public WorkflowOperationDispatcher Dispatcher { get; }
        public int FactoryCalls { get; private set; }

        public Fixture(bool exportFullReviewOutput = false, string variantId = "primary")
            : this(
                () =>
                    exportFullReviewOutput
                        ? ReviewArtifactBranchCapability.Grant("github/example/widgets", "7", Head)
                        : Capability,
                variantId
            ) { }

        public Fixture(Func<ReviewArtifactBranchCapability> capability, string variantId = "primary")
        {
            Store = new ReviewStore(_db.ConnectionString);
            var repo = new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "example",
                RepoName = "widgets",
            };
            Repo = repo;
            Run = Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = Store.EnsureRepo(repo),
                    PrId = "7",
                    HeadSha = Head,
                    BaseSha = "base",
                    TriggerWatermark = "1",
                    ReviewKind = "merged",
                    VariantId = variantId,
                    Mode = "auto",
                    Stage = ReviewStage.Discovered,
                    WorkflowStatus = WorkflowStatus.Pending,
                    PrLifecycleState = PrLifecycleState.Merged,
                }
            );
            Directory = Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("instance-1"))));
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "artifacts"));
            Scope = new JsonObject
            {
                ["ReviewRunId"] = Run.Id,
                ["WorkflowInstanceId"] = "instance-1",
                ["Admission"] = Admission.DeepClone(),
                ["FrozenContext"] = new JsonObject(),
            };
            SaveScope();
            Context = new JsonObject
            {
                ["RunId"] = Run.Id.ToString(CultureInfo.InvariantCulture),
                ["StepId"] = "retain",
                ["Attempt"] = 1,
                ["RunDirectory"] = Directory,
            };
            Runner.OnArgvContains("rev-parse", new SandboxCommandResult(0, new string('a', 40), ""));
            Runner.OnArgvContains("ls-remote", new SandboxCommandResult(2, "", ""));
            var slot = new ReviewSlot("widgets", 0);
            Workspace = new WorkflowWorkspace(
                Store,
                new CodeReviewDaemonOptions(),
                new ReviewSlotWorkspace(new ReviewSlotPool("widgets", 1, NullLogger<ReviewSlotPool>.Instance)),
                new FakeReviewSessionProvisioner(
                    new ReviewRunSession("session-1", Runner, new FakeSandboxFileSystem())
                ),
                (assignedSlot, run, _) =>
                    Task.FromResult(
                        new PreparedReviewWorkspace(
                            "nova-reviews",
                            "workspace-1",
                            run.PrId,
                            assignedSlot.WorktreeRelativePath,
                            assignedSlot.SourceRelativePath
                        )
                    ),
                NullLoggerFactory.Instance
            );
            var assignment = new WorkflowWorkspaceAssignment(slot, true, "assignment-1", "instance-1");
            Store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = Run.Id,
                    ArtifactKind = WorkflowWorkspace.AssignmentArtifactKind,
                    ArtifactSchemaVersion = 1,
                    Provider = "github",
                    Payload = System.Text.Json.JsonSerializer.Serialize(assignment),
                }
            );
            Dispatcher = new WorkflowOperationDispatcher(
                Store,
                Workspace,
                new CodeReviewDaemonOptions(),
                _root,
                // The daemon's steady-state posture: push is authorized for this exact run, but the wider
                // pilot export is NOT, so public git still receives only the fixed metadata summary.
                capability,
                (run, _) =>
                {
                    FactoryCalls++;
                    var currentCapability = capability();
                    return Task.FromResult(
                        new WorkflowArtifactOperations(
                            Store,
                            Run,
                            repo,
                            Path.Combine(_root, "store"),
                            "main",
                            new ReviewBranchManager(
                                new GitRunner(Runner),
                                new FakeSandboxFileSystem(),
                                NullLogger<ReviewBranchManager>.Instance
                            ),
                            _gate,
                            currentCapability,
                            new WorkflowKnowledgeEdits(
                                Path.Combine(_root, "store"),
                                repo,
                                new FakeSandboxFileSystem(),
                                NullLogger.Instance
                            )
                        )
                    );
                }
            );
        }

        public void SaveScope() => File.WriteAllText(Path.Combine(Directory, "scope.json"), Scope.ToJsonString());

        /// <summary>
        /// Puts the fixture on the <c>new_head</c> route with a validated canonical review, a frozen
        /// discussion and a private diff, and returns the matching retention input.
        /// </summary>
        /// <param name="reviewText">The model's prose — untrusted free text.</param>
        /// <param name="findingDescription">One finding's description — untrusted free text.</param>
        /// <param name="discussionComment">
        /// A third party's existing PR comment, seeded onto both the baseline and the window — the least
        /// controlled text in the whole export.
        /// </param>
        /// <param name="route">The admitted route, so the route guard on the export can be driven.</param>
        public JsonObject SeedNewHeadReview(
            string reviewText = "private-draft-SENTINEL",
            string? findingDescription = null,
            string? discussionComment = null,
            string route = "new_head"
        )
        {
            Admission["Route"] = route;
            var assignment = Workspace.ReadAssignment(Run);
            var slot = assignment.Slot;
            Store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = Run.Id,
                    ArtifactKind = WorkflowWorkspace.PreparationArtifactKind,
                    ArtifactSchemaVersion = 1,
                    Provider = "github",
                    Payload = System.Text.Json.JsonSerializer.Serialize(
                        new WorkflowWorkspacePreparation(
                            slot,
                            new PreparedCheckout(
                                slot.WorktreeRoot,
                                slot.SourceRoot,
                                $"{slot.WorktreeRoot}/PRs/{Run.PrId}",
                                "review/widgets-7",
                                CheckoutSha: new string('a', 40)
                            ),
                            new PreparedReviewWorkspace(
                                "nova-reviews",
                                "workspace-1",
                                Run.PrId,
                                slot.WorktreeRelativePath,
                                slot.SourceRelativePath
                            ),
                            [],
                            assignment.AssignmentId,
                            (JsonObject)Admission.DeepClone()
                        )
                    ),
                }
            );
            _manualCapture = Environment.GetEnvironmentVariable("REVIEW_DISPATCH_MANUAL_OUTPUT") is not null;
            var capturedNotes = !_manualCapture
                ? "[]"
                : new JsonArray(
                    new JsonObject
                    {
                        ["RelativePath"] = "Memory/tasks/pr-7-review.md",
                        ["Sha256"] = Convert
                            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Finding: retry drops the note")))
                            .ToLowerInvariant(),
                        ["Content"] = "Finding: retry drops the note",
                    }
                ).ToJsonString();
            Runner.OnArgvContainsFirst("notes.append", new SandboxCommandResult(0, capturedNotes, ""));
            Scope["Admission"] = Admission.DeepClone();
            Scope["FrozenContext"] = new JsonObject
            {
                ["PullRequest"] = new JsonObject { ["PrId"] = "7", ["HeadSha"] = Head },
                ["CommentBaseline"] = new JsonArray(
                    new JsonObject { ["Body"] = discussionComment ?? "existing-comment-SENTINEL" }
                ),
                ["CommentWindow"] = new JsonArray(
                    new JsonObject { ["Body"] = discussionComment ?? "window-comment-SENTINEL" }
                ),
            };
            SaveScope();
            Store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = Run.Id,
                    ArtifactKind = "workflow-diff",
                    ArtifactSchemaVersion = 1,
                    Provider = "github",
                    Payload = $"{{\"BaseSha\":\"base\",\"HeadSha\":\"{Head}\",\"Diff\":\"private-diff-SENTINEL\"}}",
                }
            );
            var findings = new JsonArray();
            if (findingDescription is not null)
            {
                findings.Add(
                    new JsonObject
                    {
                        ["Id"] = "finding-1",
                        ["Description"] = findingDescription,
                        ["Path"] = "src/Widget.cs",
                        ["Line"] = 12,
                        ["Severity"] = "minor",
                    }
                );
            }
            return new JsonObject
            {
                ["Admission"] = Admission.DeepClone(),
                ["Extractions"] = new JsonArray(),
                ["Canonical"] = new JsonObject
                {
                    ["Review"] = new JsonObject { ["Findings"] = findings, ["ReviewText"] = reviewText },
                    ["Grade"] = new JsonObject
                    {
                        ["Assessments"] = new JsonArray(),
                        ["Description"] = "private-grade-SENTINEL",
                    },
                    ["Publication"] = new JsonObject { ["Outcome"] = "no_op", ["Actions"] = new JsonArray() },
                },
            };
        }

        public void Dispose()
        {
            var manualOutput = Environment.GetEnvironmentVariable("REVIEW_DISPATCH_MANUAL_OUTPUT");
            if (_manualCapture && manualOutput is not null)
            {
                System.IO.Directory.CreateDirectory(manualOutput);
                var target = Path.Combine(manualOutput, Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(target);
                var trace = new JsonObject
                {
                    ["ReviewRunId"] = Run.Id,
                    ["Commands"] = new JsonArray([
                        .. Runner.Commands.Select(command =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["Argv"] = new JsonArray([
                                        .. command.Argv.Select(arg => (JsonNode)JsonValue.Create(arg)!),
                                    ]),
                                    ["WorkingDirectory"] = command.WorkingDirectory,
                                }
                        ),
                    ]),
                    ["Artifacts"] = new JsonArray([
                        .. Store
                            .GetArtifacts(Run.Id)
                            .Select(artifact =>
                                (JsonNode)
                                    new JsonObject { ["Kind"] = artifact.ArtifactKind, ["Payload"] = artifact.Payload }
                            ),
                    ]),
                    ["Outbox"] = new JsonArray([
                        .. Store
                            .GetOutboxForRun(Run.Id)
                            .Select(entry =>
                                (JsonNode)
                                    new JsonObject
                                    {
                                        ["Operation"] = entry.Operation,
                                        ["Status"] = entry.Status.ToString(),
                                    }
                            ),
                    ]),
                };
                File.WriteAllText(Path.Combine(target, "trace.json"), trace.ToJsonString());
                if (File.Exists(BundlePath))
                    File.Copy(BundlePath, Path.Combine(target, "retention-bundle.json"));
            }
            Store.Dispose();
            _db.Dispose();
            _gate.Dispose();
            System.IO.Directory.Delete(_root, recursive: true);
        }
    }
}
