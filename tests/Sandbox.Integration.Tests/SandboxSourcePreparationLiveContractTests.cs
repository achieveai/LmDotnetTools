using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Live contract for source preparation and the guarded create (#1005; gateway PR #375, first released in v0.1.23):
/// the public SDK prepares, polls, creates at the prepared version and reads the mounted file at the recorded commit;
/// a <c>latest</c> ref moves underneath; a stale guard is refused, never retried unguarded; a cancelled wait is
/// resumed by its operation id; and a second app cannot read that operation.
/// </summary>
/// <remarks>
/// The marketplace is a local git work tree the gateway clones over <c>file://</c>
/// (<c>SANDBOX_MARKETPLACE_SRC_REPO</c>; the gateway needs <c>MARKETPLACE_STORE_PATH</c>,
/// <c>MARKETPLACE_ALLOW_FILE_URLS=true</c> and the same path mounted). It is registered through the older public
/// <c>RegisterAppMarketplaceAsync(alias, url)</c> overload, so that call is covered too (#1004). Every sandbox and
/// registration it makes is removed: the gateway may be shared.
/// </remarks>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxSourcePreparationLiveContractTests
{
    private readonly LiveGatewayFixture _fixture;

    public SandboxSourcePreparationLiveContractTests(LiveGatewayFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Prepare_Poll_GuardedCreate_MountsThePreparedCommit_AcrossAMovingRef()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
        var sourcePath = Environment.GetEnvironmentVariable("SANDBOX_MARKETPLACE_SRC_REPO")?.Trim();
        _fixture.RequirePrecondition(
            !string.IsNullOrEmpty(sourcePath) && Directory.Exists(Path.Combine(sourcePath, ".git")),
            "SANDBOX_MARKETPLACE_SRC_REPO does not name a git work tree the gateway can clone over file://."
        );
        _fixture.RequirePrecondition(
            _fixture.SecondClient is not null,
            "SANDBOX_APP_B_ID/SANDBOX_APP_B_KEY name no second app for the operation-isolation check."
        );

        var source = new GitMarketplaceRepo(sourcePath!);
        var recorder = new RecordingGatewayHandler();
        using var transport = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new SandboxClient(_fixture.Options, transport);
        var gateway = _fixture.Options.ServerAddress;

        var run = Guid.NewGuid().ToString("N")[..12];
        var alias = "lmprep-" + run;
        var firstMarker = "first-" + run;
        var secondMarker = "second-" + run;
        var firstCommit = await source.CommitVersionAsync(firstMarker);
        var sandboxes = new List<string>();

        // The older public overload: URL only, no ref, so the alias follows `latest` (the default branch).
        var registered = await client.RegisterAppMarketplaceAsync(alias, new Uri(sourcePath!).AbsoluteUri);
        try
        {
            registered.Ref.Should().Be("latest");
            registered.ResolvedSha.Should().Be(firstCommit);
            var selection = new SandboxSourcePreparationRequest([alias]);

            var prepared = await client.PrepareSourcesAsync(selection);
            prepared.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
            prepared.OperationId.Should().NotBeNull("a cold capture is accepted (202) and polled to ready");
            prepared.SourceVersion.Should().NotBeNullOrWhiteSpace();
            prepared.Sources.Should().Contain(s => s.Marketplace == alias && s.Sha == firstCommit);
            var firstVersion = prepared.SourceVersion!;

            // A saved operation id resumes with status reads only, on the configured gateway.
            var before = recorder.Requests.Count;
            var resumed = await client.ResumeSourcePreparationAsync(prepared.OperationId!);
            resumed.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
            resumed.SourceVersion.Should().Be(firstVersion);
            recorder
                .Since(before)
                .Should()
                .NotBeEmpty()
                .And.OnlyContain(r => r.Method == HttpMethod.Get && r.Uri.Authority == gateway.Authority);

            // The status route is app-bound: another app reads the same id as not found.
            var foreign = await Assert.ThrowsAsync<SandboxException>(() =>
                _fixture.SecondClient!.ResumeSourcePreparationAsync(prepared.OperationId!)
            );
            foreign.Kind.Should().Be(SandboxErrorKind.NotFound);
            foreign.ErrorCode.Should().Be("preparation_not_found");
            foreign.StatusCode.Should().Be(404);

            // The default branch moves; the guarded create still mounts the prepared commit.
            var secondCommit = await source.CommitVersionAsync(secondMarker);
            var atFirst = await CreateGuardedAsync(client, alias, firstVersion, sandboxes);
            atFirst.SourceVersion.Should().Be(firstVersion);
            atFirst.SourceReceiptSources.Should().Contain(s => s.Marketplace == alias && s.Sha == firstCommit);
            var mountedFirst = await ReadMarkerAsync(client, atFirst.SessionId, alias);
            mountedFirst.Should().Contain(firstMarker).And.NotContain(secondMarker);

            // The registration follows the moved branch only on a sync (the sync worker is off in the contract job);
            // a prepare never fetches it. Refresh then resolves the recipe again at the synced commit.
            var synced = await client.SyncAppMarketplaceAsync(alias);
            synced.ResolvedSha.Should().Be(secondCommit);

            // Refresh is consumed at prepare. Cancel the wait once the gateway accepted it (202), before the first
            // status poll; the work goes on at the gateway and the saved operation id resumes it.
            string? acceptedId = null;
            using (var cancel = new CancellationTokenSource())
            {
                recorder.OnPreparationAccepted = id =>
                {
                    acceptedId = id;
                    cancel.Cancel();
                };
                try
                {
                    _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        client.PrepareSourcesAsync(
                            new SandboxSourcePreparationRequest([alias]) { Refresh = true },
                            cancel.Token
                        )
                    );
                }
                finally
                {
                    recorder.OnPreparationAccepted = null;
                }
            }

            acceptedId.Should().NotBeNull("a refresh always starts a new operation (202)");
            before = recorder.Requests.Count;
            var refreshed = await client.ResumeSourcePreparationAsync(acceptedId!);
            refreshed.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
            refreshed.SourceVersion.Should().NotBeNullOrWhiteSpace().And.NotBe(firstVersion);
            refreshed.Sources.Should().Contain(s => s.Marketplace == alias && s.Sha == secondCommit);
            recorder.Since(before).Should().OnlyContain(r => r.Method == HttpMethod.Get);
            var secondVersion = refreshed.SourceVersion!;

            var atSecond = await CreateGuardedAsync(client, alias, secondVersion, sandboxes);
            atSecond.SourceVersion.Should().Be(secondVersion);
            atSecond.SourceReceiptSources.Should().Contain(s => s.Marketplace == alias && s.Sha == secondCommit);
            (await ReadMarkerAsync(client, atSecond.SessionId, alias)).Should().Contain(secondMarker);

            // The first version is stale now: refused with a typed conflict, in exactly one create request.
            before = recorder.Requests.Count;
            var stale = await Assert.ThrowsAsync<SandboxException>(() =>
                CreateGuardedAsync(client, alias, firstVersion, sandboxes)
            );
            stale.Kind.Should().Be(SandboxErrorKind.Conflict);
            stale.ErrorCode.Should().Be("sources_changed");
            stale.StatusCode.Should().Be(409);
            recorder
                .Since(before)
                .Count(r =>
                    r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/sandboxes", StringComparison.Ordinal)
                )
                .Should()
                .Be(1, "a guarded create is never retried unguarded");
        }
        finally
        {
            foreach (var sessionId in sandboxes)
            {
                await client.DeleteAsync(sessionId);
            }

            _ = await client.RemoveAppMarketplaceAsync(alias);
        }
    }

    private static async Task<SandboxInfo> CreateGuardedAsync(
        SandboxClient client,
        string alias,
        string version,
        List<string> sandboxes
    )
    {
        var info = await client.CreateAsync(
            new SandboxCreateRequest(workspace: "prep-" + Guid.NewGuid().ToString("N"), marketplaces: [alias])
            {
                ExpectedSourceVersion = version,
            }
        );
        sandboxes.Add(info.SessionId);
        return info;
    }

    private static async Task<string> ReadMarkerAsync(SandboxClient client, string sessionId, string alias)
    {
        var result = await client.ExecuteAsync(
            sessionId,
            new SandboxCommand(["cat", $"/marketplaces/{alias}/{GitMarketplaceRepo.MarkerPath}"])
        );
        result.ExitCode.Should().Be(0, result.StandardError);
        return result.StandardOutput;
    }
}
