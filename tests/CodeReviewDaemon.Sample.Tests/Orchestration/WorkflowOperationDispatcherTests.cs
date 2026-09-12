using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class WorkflowOperationDispatcherTests
{
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
        bundle.Should().Contain("original").And.NotContain("secret").And.NotContain("FrozenContext");
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
        bundle.Should().Contain("original");
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
        await fixture
            .Dispatcher.Invoking(value =>
                value.DispatchAsync("retain-artifacts", fixture.Context, fixture.RetentionInput, default)
            )
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*zero artifacts*");
        fixture.Runner.Commands.Should().BeEmpty();
        fixture.Store.GetOutboxForRun(fixture.Run.Id).Should().BeEmpty();
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
        public FakeSandboxCommandRunner Runner { get; } = new();
        public string Directory { get; }
        public JsonObject Context { get; }
        public JsonObject Admission { get; } =
            new()
            {
                ["Route"] = "merged",
                ["PrId"] = "7",
                ["HeadSha"] = "head",
                ["WindowId"] = "window",
            };
        public JsonObject RetentionInput =>
            new() { ["Admission"] = Admission.DeepClone(), ["Extractions"] = new JsonArray() };
        public JsonObject Scope { get; }
        public WorkflowOperationDispatcher Dispatcher { get; }
        public int FactoryCalls { get; private set; }

        public Fixture()
        {
            Store = new ReviewStore(_db.ConnectionString);
            var repo = new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "example",
                RepoName = "widgets",
            };
            Run = Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = Store.EnsureRepo(repo),
                    PrId = "7",
                    HeadSha = "head",
                    BaseSha = "base",
                    TriggerWatermark = "1",
                    ReviewKind = "merged",
                    VariantId = "primary",
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
            Dispatcher = new WorkflowOperationDispatcher(
                Store,
                null!,
                new CodeReviewDaemonOptions(),
                _root,
                (_, _) =>
                {
                    FactoryCalls++;
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

        public void Dispose()
        {
            Store.Dispose();
            _db.Dispose();
            _gate.Dispose();
            System.IO.Directory.Delete(_root, recursive: true);
        }
    }
}
