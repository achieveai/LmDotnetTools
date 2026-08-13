using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser-level regression coverage for WORKSPACE-SCOPED per-plugin selection: the tri-state
/// (<c>null</c> / <c>[]</c> / subset) contract, the four-state PUT, the optimistic-concurrency 409,
/// and the fail-closed capability gate.
/// </summary>
/// <remarks>
/// <para>
/// Derived from the two manual proof scripts that drive a live stack
/// (<c>samples/LmStreaming.Sample/playwright-scripts/plugin-selection-proof.mjs</c> and
/// <c>plugin-selection-wire-proof.mjs</c>), reduced to what a scripted, CI-safe harness can assert
/// deterministically: a <see cref="FakeMarketplaceCatalogClient"/> supplies the catalog AND the
/// gateway capability advertisement, and a <see cref="CapturingSandboxGatewayHandler"/> stands in for
/// the gateway so the workspace store is isolated to a temp directory. No live gateway, no real LLM.
/// </para>
/// <para>
/// The two states these tests exist to keep apart — <c>null</c> ("no preference", legacy all-plugins)
/// and <c>[]</c> ("explicitly no plugins") — are indistinguishable to any assertion that reads the
/// selection as a truthy value or through <c>?? []</c>. Every assertion here therefore goes through
/// <see cref="ReadWorkspaceAsync"/>, which classifies the wire value into an explicit
/// <c>null</c>/<c>empty</c>/<c>list:…</c> shape string BEFORE it reaches C#.
/// </para>
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class WorkspacePluginSelectionTests
{
    // Two marketplaces so a subset can span both, and the larger one can lose a plugin while staying
    // represented. Aliases and plugin names are fixed by the fake catalog, so every expectation below
    // is exact rather than derived at runtime the way the live-stack scripts must be.
    private const string MarketplaceA = "ClaudePlugins";
    private const string MarketplaceB = "community";
    private const string PluginA1 = "orleans-dev";
    private const string PluginA2 = "pr-review";
    private const string PluginB1 = "docs-writer";

    /// <summary>Every plugin of both marketplaces, in the order <see cref="ReadWorkspaceAsync"/> sorts them.</summary>
    private const string AllThreePlugins =
        $"list:{MarketplaceA}/{PluginA1},{MarketplaceA}/{PluginA2},{MarketplaceB}/{PluginB1}";

    private readonly PlaywrightFixture _fixture;

    public WorkspacePluginSelectionTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// THE CRUX: an explicitly empty selection persists as <c>[]</c> and the "Use all plugins" reset
    /// persists as <c>null</c> — through the real UI, in that order, on the same workspace.
    /// </summary>
    /// <remarks>
    /// Non-vacuity: the two saves run the identical code path and must produce DIFFERENT wire shapes.
    /// A collapse in either direction (<c>[]</c> read as "no preference", or the reset written as
    /// <c>[]</c>) makes exactly one of the two shape assertions fail. The revision is asserted to step
    /// 0 → 1 → 2, so a save that silently did nothing cannot pass either — the shape would be right by
    /// accident while the revision stood still.
    /// </remarks>
    [Fact]
    public async Task Explicit_empty_and_legacy_null_selections_stay_distinct_end_to_end()
    {
        await RunAsync(pluginFiltering: true, async session =>
        {
            var page = session.Page;
            var workspaceId = await CreateWorkspaceAsync(
                page,
                $$"""{ "name": "Tri State", "directoryRelPath": "tri-state", "marketplaces": ["{{MarketplaceA}}", "{{MarketplaceB}}"] }""");

            var seeded = await ReadWorkspaceAsync(page, workspaceId);
            seeded.Shape.Should().Be("null", "a workspace created without a selection expresses NO preference");
            seeded.Revision.Should().Be(0);

            await page.ReloadAsync();
            await page.Textarea().WaitForAsync();
            await OpenEditFormAsync(page, workspaceId);

            // `null` renders as every plugin checked (truthfully: the gateway reads it as all-plugins),
            // and the reset control is absent because there is nothing explicit to reset.
            await ExpectPluginBoxesAsync(page, "edit", (PluginA1, true), (PluginA2, true), (PluginB1, true));
            (await page.GetByTestId("workspace-edit-plugins-reset").CountAsync())
                .Should().Be(0, "the reset is offered only while the selection is EXPLICIT");

            // 1) Drive to an explicitly empty selection by unchecking everything.
            await SetCheckboxAsync(page, EditPlugin(MarketplaceA, PluginA1), false);
            await SetCheckboxAsync(page, EditPlugin(MarketplaceA, PluginA2), false);
            await SetCheckboxAsync(page, EditPlugin(MarketplaceB, PluginB1), false);
            (await SubmitEditFormAsync(page)).Should().BeNull("the empty-selection save must succeed");

            var afterEmpty = await ReadWorkspaceAsync(page, workspaceId);
            afterEmpty.Shape.Should().Be("empty", "an explicit [] must NOT be collapsed to null");
            afterEmpty.Revision.Should().Be(1, "an explicit selection change bumps the CAS revision");

            // 2) Return to the legacy `null` — the one state no checkbox can reach.
            await OpenEditFormAsync(page, workspaceId);
            await ExpectPluginBoxesAsync(page, "edit", (PluginA1, false), (PluginA2, false), (PluginB1, false));
            var reset = page.GetByTestId("workspace-edit-plugins-reset");
            await reset.WaitForAsync();
            await reset.ClickAsync();
            // The control is `v-if`'d on the selection being explicit, so its disappearance IS the
            // signal that state went back to null — waiting on that leaves the checkbox assertion
            // below an independent claim that can still fail.
            await reset.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            await ExpectPluginBoxesAsync(page, "edit", (PluginA1, true), (PluginA2, true), (PluginB1, true));

            (await SubmitEditFormAsync(page)).Should().BeNull("the reset save must succeed");

            var afterReset = await ReadWorkspaceAsync(page, workspaceId);
            afterReset.Shape.Should().Be("null", "\"use all plugins\" must persist as null, NOT as []");
            afterReset.Revision.Should().Be(2);

            await session.SaveSuccessScreenshotAsync("WorkspacePluginSelection.EmptyVsNull");
        });
    }

    /// <summary>
    /// A stale <c>pluginsRevision</c> is rejected with 409, the user is TOLD the pending change was
    /// discarded, and the form is re-seeded from SERVER state rather than left holding the rejected
    /// edit. Then the retry lands.
    /// </summary>
    /// <remarks>
    /// Non-vacuity: the out-of-band write leaves the server holding EXACTLY <c>{A1}</c> while the user's
    /// pending edit is EXACTLY <c>{A2}</c>. The two are disjoint, so "the form shows server state" and
    /// "the form kept my edit" cannot both pass — a re-seed that silently did nothing fails the final
    /// checkbox assertions. The pre-conflict assertion (the open form still holds BOTH plugins) proves
    /// there really was a stale revision to collide with; without it a 409 could be an artefact of the
    /// form having already re-read the server.
    /// </remarks>
    [Fact]
    public async Task Stale_revision_conflict_reports_the_discard_and_reseeds_the_form_from_server_state()
    {
        await RunAsync(pluginFiltering: true, async session =>
        {
            var page = session.Page;
            var workspaceId = await CreateWorkspaceAsync(
                page,
                $$"""
                {
                  "name": "Conflict", "directoryRelPath": "conflict",
                  "marketplaces": ["{{MarketplaceA}}"],
                  "pluginSelection": [
                    { "marketplace": "{{MarketplaceA}}", "plugin": "{{PluginA1}}" },
                    { "marketplace": "{{MarketplaceA}}", "plugin": "{{PluginA2}}" }
                  ]
                }
                """);

            await page.ReloadAsync();
            await page.Textarea().WaitForAsync();

            // Opening the form is what seeds the client with revision 0.
            await OpenEditFormAsync(page, workspaceId);
            await ExpectPluginBoxesAsync(page, "edit", (PluginA1, true), (PluginA2, true));

            // Move the server underneath the OPEN form: leave exactly A1 selected, revision → 1.
            var outOfBand = await RawPutAsync(
                page,
                workspaceId,
                $$"""
                {
                  "marketplaces": ["{{MarketplaceA}}"],
                  "pluginSelection": [{ "marketplace": "{{MarketplaceA}}", "plugin": "{{PluginA1}}" }],
                  "pluginsRevision": 0
                }
                """);
            outOfBand.Status.Should().Be(200, "the out-of-band write is setup, not the behaviour under test");
            (await ReadWorkspaceAsync(page, workspaceId)).Revision.Should().Be(1);

            // The open form must still hold the PRE-conflict seed; otherwise there is no stale
            // revision left to collide and everything below would prove nothing.
            await ExpectPluginBoxesAsync(page, "edit", (PluginA1, true), (PluginA2, true));

            // The "user" unchecks A1, so their pending state is exactly {A2} — disjoint from the
            // server's exactly-{A1}.
            await SetCheckboxAsync(page, EditPlugin(MarketplaceA, PluginA1), false);

            var conflictError = await SubmitEditFormAsync(page);
            conflictError.Should().NotBeNull("a stale revision must surface an error, not fail silently");
            conflictError.Should().Contain(
                "discarded",
                "the message is what makes the discard honest — \"the list was refreshed\" would hide it");
            (await page.GetByTestId("workspace-edit-form").CountAsync())
                .Should().Be(1, "the form stays open so the user can re-apply the change");

            // The load-bearing half: the form now shows the SERVER's selection, not the rejected edit.
            await ExpectPluginBoxesAsync(page, "edit", (PluginA1, true), (PluginA2, false));

            var afterConflict = await ReadWorkspaceAsync(page, workspaceId);
            afterConflict.Shape.Should().Be($"list:{MarketplaceA}/{PluginA1}", "the rejected write must change nothing");
            afterConflict.Revision.Should().Be(1);

            // Recovery: re-applying the change on the refreshed CAS token succeeds.
            await SetCheckboxAsync(page, EditPlugin(MarketplaceA, PluginA2), true);
            (await SubmitEditFormAsync(page)).Should().BeNull("the retry must land");

            var recovered = await ReadWorkspaceAsync(page, workspaceId);
            recovered.Shape.Should().Be($"list:{MarketplaceA}/{PluginA1},{MarketplaceA}/{PluginA2}");
            recovered.Revision.Should().Be(2);

            await session.SaveSuccessScreenshotAsync("WorkspacePluginSelection.RevisionConflict");
        });
    }

    /// <summary>
    /// The capability gate FAILS CLOSED: the per-plugin UI renders only when the gateway advertises
    /// <c>capabilities.pluginFiltering === true</c>. An explicit <c>false</c> and an absent capability
    /// block (an older gateway) both hide it, while the marketplace-level UI is untouched.
    /// </summary>
    /// <remarks>
    /// Non-vacuity by construction: all three rows drive the IDENTICAL steps and differ only in what
    /// the gateway advertises. The <c>true</c> row observes 3 plugin checkboxes at the very point the
    /// other two observe 0, so "0 boxes" cannot be an artefact of the form not rendering or the
    /// marketplaces not being enabled — both of which are asserted separately, in every row.
    /// </remarks>
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    [InlineData(null, 0)]
    public async Task Per_plugin_ui_renders_only_when_the_gateway_advertises_plugin_filtering(
        bool? pluginFiltering,
        int expectedPluginBoxes)
    {
        await RunAsync(pluginFiltering, async session =>
        {
            var page = session.Page;
            await OpenCreateFormAsync(page);

            await SetCheckboxAsync(page, $"workspace-create-marketplace-{MarketplaceA}", true);
            await SetCheckboxAsync(page, $"workspace-create-marketplace-{MarketplaceB}", true);

            // The marketplace half of the form is the control: it must render and be enabled in EVERY
            // row, so a zero plugin-box count can only mean the gate hid the per-plugin UI.
            await Assertions.Expect(page.GetByTestId($"workspace-create-marketplace-{MarketplaceA}")).ToBeCheckedAsync();
            await Assertions.Expect(page.GetByTestId($"workspace-create-marketplace-{MarketplaceB}")).ToBeCheckedAsync();

            if (expectedPluginBoxes > 0)
            {
                await page.GetByTestId($"workspace-create-plugins-{MarketplaceA}").WaitForAsync();
                await page.GetByTestId($"workspace-create-plugins-{MarketplaceB}").WaitForAsync();
            }

            // Page-wide, so a plugin control rendered ANYWHERE (including outside its marketplace's
            // subtree) still counts against a gate that claims to hide all of them.
            (await page.Locator("[data-plugin-checkbox]").CountAsync())
                .Should().Be(expectedPluginBoxes);
            (await page.GetByTestId($"workspace-create-plugins-{MarketplaceA}").CountAsync())
                .Should().Be(expectedPluginBoxes > 0 ? 1 : 0);
            (await page.GetByTestId($"workspace-create-plugins-{MarketplaceB}").CountAsync())
                .Should().Be(expectedPluginBoxes > 0 ? 1 : 0);
            (await page.GetByTestId("workspace-create-plugins-reset").CountAsync())
                .Should().Be(0, "the reset belongs to the per-plugin UI and a fresh form is never explicit");
        });
    }

    /// <summary>
    /// A NON-plugin edit (toggling a marketplace) omits <c>pluginSelection</c> from the PUT body
    /// entirely, leaves the stored selection alone, and does NOT bump <c>pluginsRevision</c>.
    /// </summary>
    /// <remarks>
    /// The wire body is what matters and no DOM assertion can see it, so the request is read straight
    /// off the page's network traffic. Non-vacuity: the SAME captured body is asserted to carry the
    /// newly-enabled marketplace, so an empty or missing PUT cannot satisfy the omission claim — and
    /// the workspace is seeded with a NON-EMPTY selection first, so "no <c>pluginSelection</c> key"
    /// is a real choice rather than a value that happened to be absent anyway.
    /// </remarks>
    [Fact]
    public async Task Marketplace_only_edit_omits_pluginSelection_and_leaves_the_revision_alone()
    {
        await RunAsync(pluginFiltering: true, async session =>
        {
            var page = session.Page;
            var workspaceId = await CreateWorkspaceAsync(
                page,
                $$"""
                {
                  "name": "Marketplace Only", "directoryRelPath": "marketplace-only",
                  "marketplaces": ["{{MarketplaceA}}"],
                  "pluginSelection": [{ "marketplace": "{{MarketplaceA}}", "plugin": "{{PluginA1}}" }]
                }
                """);

            var before = await ReadWorkspaceAsync(page, workspaceId);
            before.Shape.Should().Be($"list:{MarketplaceA}/{PluginA1}", "the omission must be a real choice");
            before.Revision.Should().Be(0);

            await page.ReloadAsync();
            await page.Textarea().WaitForAsync();

            var puts = new List<string>();
            void OnRequest(object? sender, IRequest request)
            {
                if (request.Method == "PUT" && request.Url.Contains("/api/workspaces/", StringComparison.Ordinal))
                {
                    lock (puts)
                    {
                        puts.Add(request.PostData ?? string.Empty);
                    }
                }
            }

            page.Request += OnRequest;
            try
            {
                await OpenEditFormAsync(page, workspaceId);
                await SetCheckboxAsync(page, $"workspace-edit-marketplace-{MarketplaceB}", true);
                (await SubmitEditFormAsync(page)).Should().BeNull("a marketplace-only edit must save");
            }
            finally
            {
                page.Request -= OnRequest;
            }

            string body;
            lock (puts)
            {
                puts.Should().HaveCount(1, "the save must send exactly one workspace PUT");
                body = puts[0];
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            root.TryGetProperty("pluginSelection", out _)
                .Should().BeFalse("an omitted key is the backend's four-state \"leave unchanged\"; body was: " + body);
            root.TryGetProperty("pluginsRevision", out _)
                .Should().BeFalse("the CAS token travels only alongside a selection; body was: " + body);
            root.GetProperty("marketplaces")
                .EnumerateArray()
                .Select(element => element.GetString())
                .Should().Contain(MarketplaceB, "the very same body must carry the change that WAS made");

            var after = await ReadWorkspaceAsync(page, workspaceId);
            after.Marketplaces.Should().Be($"{MarketplaceA},{MarketplaceB}", "the marketplace edit must take effect");
            after.Shape.Should().Be($"list:{MarketplaceA}/{PluginA1}", "the stored selection must be untouched");
            after.Revision.Should().Be(0, "no plugin changed, so no CAS bump and no session migration");
        });
    }

    /// <summary>
    /// A subset spanning BOTH marketplaces persists exactly — minus the one plugin left unchecked —
    /// and re-renders identically after a full page reload, with its partially-selected marketplace
    /// shown indeterminate.
    /// </summary>
    /// <remarks>
    /// Non-vacuity: the persisted shape is asserted as an EXACT sorted list, so a selection that
    /// silently widened to all-plugins, narrowed to one marketplace, or dropped to <c>[]</c> each
    /// fail. The post-reload assertions read a form re-seeded from the server, so they cannot pass on
    /// leftover client state.
    /// </remarks>
    [Fact]
    public async Task Subset_spanning_two_marketplaces_persists_exactly_and_survives_a_reload()
    {
        await RunAsync(pluginFiltering: true, async session =>
        {
            var page = session.Page;
            var workspaceId = await CreateWorkspaceAsync(
                page,
                $$"""{ "name": "Subset", "directoryRelPath": "subset", "marketplaces": ["{{MarketplaceA}}", "{{MarketplaceB}}"] }""");

            await page.ReloadAsync();
            await page.Textarea().WaitForAsync();
            await OpenEditFormAsync(page, workspaceId);
            await ExpectPluginBoxesAsync(page, "edit", (PluginA1, true), (PluginA2, true), (PluginB1, true));

            // Skip ONE plugin of the larger marketplace, so the saved subset still spans both.
            await SetCheckboxAsync(page, EditPlugin(MarketplaceA, PluginA2), false);
            (await SubmitEditFormAsync(page)).Should().BeNull();

            var saved = await ReadWorkspaceAsync(page, workspaceId);
            saved.Shape.Should().Be($"list:{MarketplaceA}/{PluginA1},{MarketplaceB}/{PluginB1}");
            saved.Revision.Should().Be(1);

            // A full reload re-derives the form from the server, so this is the persistence claim and
            // not a re-read of the state the click left behind.
            await page.ReloadAsync();
            await page.Textarea().WaitForAsync();
            await OpenEditFormAsync(page, workspaceId);
            await ExpectPluginBoxesAsync(page, "edit", (PluginA1, true), (PluginA2, false), (PluginB1, true));

            (await IsIndeterminateAsync(page, $"workspace-edit-marketplace-{MarketplaceA}"))
                .Should().BeTrue("SOME but not all of A's plugins are selected");
            (await IsIndeterminateAsync(page, $"workspace-edit-marketplace-{MarketplaceB}"))
                .Should().BeFalse("all of B's plugins are selected, which is a determinate state");

            await session.SaveSuccessScreenshotAsync("WorkspacePluginSelection.SubsetSurvivesReload");
        });
    }

    /// <summary>
    /// A plugin that cannot be selected for this workspace is rejected as <c>unsupported_plugins</c>,
    /// both when it exists nowhere and when it exists under a marketplace the workspace has not
    /// enabled — and neither rejection writes anything.
    /// </summary>
    /// <remarks>
    /// Driven through the page's own same-origin <c>fetch</c> rather than the UI, because the form can
    /// only offer plugins that ARE selectable: an unknown ref is unreachable by clicking. Non-vacuity:
    /// the run ends with a structurally IDENTICAL PUT carrying a legal plugin, which must return 200
    /// and bump the revision — so the two 400s cannot be blamed on a malformed request.
    /// </remarks>
    [Fact]
    public async Task Unknown_and_unenabled_plugin_refs_are_rejected_as_unsupported_plugins()
    {
        await RunAsync(pluginFiltering: true, async session =>
        {
            var page = session.Page;
            var workspaceId = await CreateWorkspaceAsync(
                page,
                $$"""{ "name": "Unsupported", "directoryRelPath": "unsupported", "marketplaces": ["{{MarketplaceA}}"] }""");

            foreach (var (marketplace, plugin, why) in new[]
            {
                ("__nope__", "__nope__", "a plugin that exists nowhere in the catalog"),
                (MarketplaceB, PluginB1, "a real plugin under a marketplace this workspace has not enabled"),
            })
            {
                var rejected = await RawPutAsync(
                    page,
                    workspaceId,
                    $$"""
                    {
                      "marketplaces": ["{{MarketplaceA}}"],
                      "pluginSelection": [{ "marketplace": "{{marketplace}}", "plugin": "{{plugin}}" }],
                      "pluginsRevision": 0
                    }
                    """);

                rejected.Status.Should().Be(400, why);
                using var error = JsonDocument.Parse(rejected.Body);
                error.RootElement.GetProperty("code").GetString().Should().Be("unsupported_plugins");
                error.RootElement.GetProperty("unsupportedPlugins")
                    .EnumerateArray().Should().NotBeEmpty("the rejection must name what it rejected");

                var untouched = await ReadWorkspaceAsync(page, workspaceId);
                untouched.Shape.Should().Be("null", "a rejected write must change nothing");
                untouched.Revision.Should().Be(0);
            }

            // The control: same body shape, legal plugin. Its success is what makes the two 400s
            // attributable to the plugin refs rather than to the request.
            var accepted = await RawPutAsync(
                page,
                workspaceId,
                $$"""
                {
                  "marketplaces": ["{{MarketplaceA}}"],
                  "pluginSelection": [{ "marketplace": "{{MarketplaceA}}", "plugin": "{{PluginA1}}" }],
                  "pluginsRevision": 0
                }
                """);
            accepted.Status.Should().Be(200);

            var final = await ReadWorkspaceAsync(page, workspaceId);
            final.Shape.Should().Be($"list:{MarketplaceA}/{PluginA1}");
            final.Revision.Should().Be(1);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Boots the app against a fake catalog advertising <paramref name="pluginFiltering"/>, runs
    /// <paramref name="body"/>, and cleans up the temp workspace base.
    /// </summary>
    /// <remarks>
    /// The <see cref="CapturingSandboxGatewayHandler"/> is supplied for its SIDE EFFECT rather than
    /// its captures: it is what makes <see cref="BrowserWebAppFactory"/> swap the workspace store for
    /// a temp-directory one, so these tests never write to the developer's real workspace catalog.
    /// Nothing here provisions a sandbox — no message is ever sent — so the closed-port
    /// <c>BaseUrl</c> is never dialled.
    /// </remarks>
    private async Task RunAsync(bool? pluginFiltering, Func<ScenarioSession, Task> body)
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), "lm-e2e-plugin-sel", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(workspaceBase);
        var sandboxOptions = new SandboxGatewayOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            WorkspaceBasePath = workspaceBase,
            AppId = "lm-e2e",
            AutoSpawn = false,
        };

        // Never exercised: these scenarios drive the workspace dropdown only and send no message.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("unused", _ => true)
            .Turn(t => t.Text("unused"))
            .Build();

        try
        {
            await using var session = await _fixture.OpenAsync(
                "test-anthropic",
                responder.HandlerFor("test-anthropic"),
                sandboxGatewayHandler: new CapturingSandboxGatewayHandler(),
                sandboxOptions: sandboxOptions,
                catalogClient: FakeMarketplaceCatalogClient.WithPlugins(
                    pluginFiltering,
                    (MarketplaceA, [PluginA1, PluginA2]),
                    (MarketplaceB, [PluginB1])));

            await body(session);
        }
        finally
        {
            try
            {
                Directory.Delete(workspaceBase, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp workspace base.
            }
            catch (UnauthorizedAccessException)
            {
                // Ditto.
            }
        }
    }

    private static string EditPlugin(string marketplace, string plugin) =>
        $"workspace-edit-plugin-{marketplace}-{plugin}";

    /// <summary>
    /// The tri-state selection classified into an explicit shape string BEFORE it can be flattened by
    /// C#: <c>null</c>, <c>empty</c>, <c>absent</c>, or <c>list:&lt;m&gt;/&lt;p&gt;,…</c> sorted.
    /// </summary>
    private sealed record WorkspaceSnapshot(string Shape, int Revision, string Marketplaces);

    private static async Task<WorkspaceSnapshot> ReadWorkspaceAsync(IPage page, string workspaceId)
    {
        var json = await page.EvaluateAsync<string>(
            """
            async (id) => {
                const res = await fetch('/api/workspaces');
                const body = await res.json();
                const list = Array.isArray(body) ? body : (body.workspaces ?? []);
                const w = list.find((x) => x.id === id);
                if (!w) { return JSON.stringify({ shape: 'missing', revision: -1, marketplaces: '' }); }
                const sel = w.pluginSelection;
                const shape =
                    sel === undefined ? 'absent'
                    : sel === null ? 'null'
                    : sel.length === 0 ? 'empty'
                    : 'list:' + sel.map((r) => r.marketplace + '/' + r.plugin).sort().join(',');
                return JSON.stringify({
                    shape,
                    revision: w.pluginsRevision,
                    marketplaces: [...(w.marketplaces ?? [])].sort().join(','),
                });
            }
            """,
            workspaceId);

        using var document = JsonDocument.Parse(json);
        return new WorkspaceSnapshot(
            document.RootElement.GetProperty("shape").GetString()!,
            document.RootElement.GetProperty("revision").GetInt32(),
            document.RootElement.GetProperty("marketplaces").GetString()!);
    }

    private static async Task<string> CreateWorkspaceAsync(IPage page, string payload)
    {
        var id = await page.EvaluateAsync<string>(
            """
            async (payload) => {
                const res = await fetch('/api/workspaces', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: payload,
                });
                if (!res.ok) { throw new Error('create workspace failed: ' + res.status + ' ' + (await res.text())); }
                return (await res.json()).id;
            }
            """,
            payload);
        id.Should().NotBeNullOrWhiteSpace();
        return id;
    }

    private readonly record struct RawResponse(int Status, string Body);

    /// <summary>
    /// A verbatim same-origin PUT. The payload is passed as a pre-serialized STRING so an explicit
    /// <c>null</c> or <c>[]</c> reaches the wire exactly as written, instead of being re-encoded by an
    /// object round trip that could normalise the very distinction under test.
    /// </summary>
    private static async Task<RawResponse> RawPutAsync(IPage page, string workspaceId, string payload)
    {
        var json = await page.EvaluateAsync<string>(
            """
            async ({ id, payload }) => {
                const res = await fetch('/api/workspaces/' + encodeURIComponent(id), {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: payload,
                });
                return JSON.stringify({ status: res.status, body: await res.text() });
            }
            """,
            new Dictionary<string, object> { ["id"] = workspaceId, ["payload"] = payload });

        using var document = JsonDocument.Parse(json);
        return new RawResponse(
            document.RootElement.GetProperty("status").GetInt32(),
            document.RootElement.GetProperty("body").GetString()!);
    }

    /// <summary>Opens the dropdown on the workspace LIST view, dismissing whatever form was open.</summary>
    private static async Task OpenWorkspaceListAsync(IPage page)
    {
        await Assertions.Expect(page.GetByTestId("workspace-selector-button"))
            .ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 30_000 });

        await DismissFormAsync(page, "workspace-edit");
        await DismissFormAsync(page, "workspace-create");

        // Cancelling may or may not collapse the whole dropdown depending on where the click landed,
        // so re-open it only when the list view is genuinely gone.
        if (await page.GetByTestId("workspace-create-open").CountAsync() == 0)
        {
            await page.GetByTestId("workspace-selector-button").ClickAsync();
        }

        await page.GetByTestId("workspace-create-open").WaitForAsync();
    }

    private static async Task DismissFormAsync(IPage page, string prefix)
    {
        var form = page.GetByTestId($"{prefix}-form");
        if (await form.CountAsync() == 0)
        {
            return;
        }

        await page.GetByTestId($"{prefix}-cancel").ClickAsync();
        await form.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    /// <summary>
    /// Opens the edit form for <paramref name="workspaceId"/> and waits for the catalog-driven
    /// marketplace rows, which arrive asynchronously from <c>GET /api/marketplaces</c>.
    /// </summary>
    private static async Task OpenEditFormAsync(IPage page, string workspaceId)
    {
        await OpenWorkspaceListAsync(page);
        await page.GetByTestId($"workspace-edit-{workspaceId}").ClickAsync();
        await page.GetByTestId("workspace-edit-form").WaitForAsync();
        await page.GetByTestId($"workspace-edit-marketplace-{MarketplaceA}").WaitForAsync();
        await page.GetByTestId($"workspace-edit-marketplace-{MarketplaceB}").WaitForAsync();
    }

    private static async Task OpenCreateFormAsync(IPage page)
    {
        await OpenWorkspaceListAsync(page);
        await page.GetByTestId("workspace-create-open").ClickAsync();
        await page.GetByTestId("workspace-create-form").WaitForAsync();
        await page.GetByTestId($"workspace-create-marketplace-{MarketplaceA}").WaitForAsync();
        await page.GetByTestId($"workspace-create-marketplace-{MarketplaceB}").WaitForAsync();
    }

    /// <summary>
    /// Clicks a CONTROLLED checkbox and waits for the state to actually land. The inputs bind
    /// <c>:checked</c> to Vue state through an <c>@change</c> handler, so a click is a request, not a
    /// guarantee — asserting the outcome is what makes a swallowed toggle fail the test instead of
    /// quietly producing a wrong selection.
    /// </summary>
    private static async Task SetCheckboxAsync(IPage page, string testId, bool desired)
    {
        var box = page.GetByTestId(testId);
        await box.WaitForAsync();
        if (await box.IsCheckedAsync() == desired)
        {
            return;
        }

        await box.ClickAsync();
        if (desired)
        {
            await Assertions.Expect(box).ToBeCheckedAsync();
        }
        else
        {
            await Assertions.Expect(box).Not.ToBeCheckedAsync();
        }
    }

    private static async Task ExpectPluginBoxesAsync(
        IPage page,
        string mode,
        params (string Plugin, bool Checked)[] expected)
    {
        foreach (var (plugin, isChecked) in expected)
        {
            var marketplace = plugin == PluginB1 ? MarketplaceB : MarketplaceA;
            var box = page.GetByTestId($"workspace-{mode}-plugin-{marketplace}-{plugin}");
            if (isChecked)
            {
                await Assertions.Expect(box).ToBeCheckedAsync();
            }
            else
            {
                await Assertions.Expect(box).Not.ToBeCheckedAsync();
            }
        }
    }

    private static Task<bool> IsIndeterminateAsync(IPage page, string testId) =>
        page.GetByTestId(testId).EvaluateAsync<bool>("el => el.indeterminate");

    /// <summary>
    /// Submits the edit form and waits for the OUTCOME — the parent closes the form on success and
    /// mounts <c>workspace-form-error</c> on failure, so exactly one of the two is observable.
    /// </summary>
    /// <returns>The error text, or <see langword="null"/> when the save succeeded.</returns>
    private static async Task<string?> SubmitEditFormAsync(IPage page)
    {
        // A leftover error from an earlier attempt would satisfy the outcome wait before Vue has
        // re-rendered, and this call would report the OLD failure. `submitEdit` nulls formError first,
        // which unmounts it, so wait for that to happen before judging the new attempt.
        var error = page.GetByTestId("workspace-form-error");
        var hadError = await error.CountAsync() > 0;

        await page.GetByTestId("workspace-edit-submit").ClickAsync();

        if (hadError)
        {
            await error.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
        }

        await page.WaitForFunctionAsync(
            """
            () => {
                const form = document.querySelector('[data-testid="workspace-edit-form"]');
                if (!form) { return true; }
                return !!form.querySelector('[data-testid="workspace-form-error"]');
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        return await error.CountAsync() > 0 ? await error.First.TextContentAsync() : null;
    }
}
