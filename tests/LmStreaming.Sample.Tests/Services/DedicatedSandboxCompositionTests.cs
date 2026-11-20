using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// A thread provisioned with a dedicated sandbox gets a sandbox of its own, created with its working directory as
/// the gateway working directory, and loses it when its agent goes or when the release route is called.
/// </summary>
/// <remarks>
/// Composition-root on purpose, like <see cref="ReadOnlyEgressCompositionTests"/>: the flag is read and put on the
/// workspace ref inside <c>Program.cs</c>'s pool agent factory, and an omission there is silent (the thread simply
/// shares the workspace's sandbox, writable everywhere). Only the sandbox gateway and the marketplace catalog are
/// faked (<see cref="FakeGatewayWebAppFactory"/>); the registry, the pool, the factory, the controllers and the
/// stores are the production types.
/// </remarks>
public sealed class DedicatedSandboxCompositionTests : LoggingTestBase
{
    private const string AppId = "lm-dedicated-sandbox-test";

    private static readonly AgentProfile WorkspaceMode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId)!;
    private static readonly AgentProfile WorkflowAuthorMode = SystemChatModes.GetById(
        SystemChatModes.WorkflowAuthorModeId
    )!;

    public DedicatedSandboxCompositionTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task Each_dedicated_thread_gets_its_own_jailed_sandbox_and_keeps_it_across_a_mode_switch()
    {
        await using var fixture = await StartAsync();
        var a = await fixture.ProvisionAsync("slots/r-1/PRs/r-7");
        var b = await fixture.ProvisionAsync("slots/r-2/PRs/r-8");
        var shared = $"lane-{Guid.NewGuid():N}";

        foreach (var threadId in new[] { shared, a, b })
        {
            _ = fixture.BuildAgent(threadId);
        }

        fixture.Gateway.Creates.Should().HaveCount(3, "no thread may share another's sandbox");
        var sessions = fixture.Gateway.Creates.Select(create => create.SessionId).ToList();
        fixture.Registry.GetThreads(sessions[0]).Should().Equal(shared);
        fixture.Registry.GetThreads(sessions[1]).Should().Equal(a);
        fixture.Registry.GetThreads(sessions[2]).Should().Equal(b);
        WorkingDir(fixture.Gateway.Creates[0].Body).Should().BeNull("the shared sandbox stays writable everywhere");
        WorkingDir(fixture.Gateway.Creates[1].Body).Should().Be("slots/r-1/PRs/r-7");
        WorkingDir(fixture.Gateway.Creates[2].Body).Should().Be("slots/r-2/PRs/r-8");
        fixture.Registry.TryGetSessionById(sessions[1], out var sandboxA).Should().BeTrue();
        sandboxA!.OwnerThreadId.Should().Be(a);

        _ = await fixture.Pool.SwitchModeAsync(a, WorkflowAuthorMode);

        fixture.Gateway.Creates.Should().HaveCount(3, "the switch must reuse the thread's own sandbox");
        fixture.Registry.GetThreads(sessions[1]).Should().Equal(a);
    }

    [Fact]
    public async Task Removing_a_dedicated_threads_agent_deletes_its_sandbox_but_not_the_shared_one()
    {
        await using var fixture = await StartAsync();
        var a = await fixture.ProvisionAsync("PRs/r-7");
        var shared = $"lane-{Guid.NewGuid():N}";
        foreach (var threadId in new[] { shared, a })
        {
            _ = fixture.BuildAgent(threadId);
        }

        await fixture.Pool.RemoveAgentAsync(a);
        await fixture.Pool.RemoveAgentAsync(shared);

        (await fixture.Gateway.NextDeleteAsync()).Should().Be(fixture.Gateway.Creates[1].SessionId);
        fixture.Gateway.Deletes.Should().Equal(fixture.Gateway.Creates[1].SessionId);
    }

    [Fact]
    public async Task The_release_route_deletes_the_sandbox_moves_the_directory_keeps_the_history_and_the_next_turn_gets_a_fresh_one()
    {
        await using var fixture = await StartAsync();
        var a = await fixture.ProvisionAsync("slots/r-1/PRs/r-7");
        await fixture.Store.AppendMessagesAsync(
            a,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Role = Role.User, Text = "review r-7" },
                    a,
                    "run-1"
                ),
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Role = Role.Assistant, Text = "two findings" },
                    a,
                    "run-1"
                ),
            ]
        );
        var history = (await fixture.Store.LoadMessagesAsync(a)).Select(message => message.MessageJson).ToList();
        _ = fixture.BuildAgent(a);
        var first = fixture.Gateway.Creates.Single().SessionId;

        using var response = await fixture.ReleaseAsync(a, "slots/r-3/PRs/r-7");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ReleaseSandboxResponse>())!
            .WorkingDirectoryRelPath.Should()
            .Be("slots/r-3/PRs/r-7");
        fixture.Gateway.Deletes.Should().Equal(first);
        fixture.Pool.ActiveAgentCount.Should().Be(0, "the agent held the deleted sandbox's session id");
        (await fixture.Store.LoadMessagesAsync(a))
            .Select(message => message.MessageJson)
            .Should()
            .Equal(history, "only the container goes; the conversation keeps its history");

        _ = fixture.BuildAgent(a);

        fixture.Gateway.Creates.Should().HaveCount(2);
        WorkingDir(fixture.Gateway.Creates[1].Body).Should().Be("slots/r-3/PRs/r-7");
    }

    [Fact]
    public async Task The_release_route_refuses_a_thread_without_a_dedicated_sandbox()
    {
        await using var fixture = await StartAsync();
        var shared = await fixture.ProvisionAsync(null, dedicated: false);
        _ = fixture.BuildAgent(shared);

        using var response = await fixture.ReleaseAsync(shared, directory: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("dedicated_sandbox_required");
        fixture.Gateway.Deletes.Should().BeEmpty("the workspace's shared sandbox serves other threads");
    }

    [Fact]
    public async Task The_release_route_with_a_directory_gives_a_shared_sandbox_thread_its_own_and_leaves_the_shared_one()
    {
        // A review conversation from before dedicated sandboxes runs in the workspace's shared sandbox. Continuing it
        // must not keep it there, writable everywhere, nor delete the sandbox other threads still use.
        await using var fixture = await StartAsync();
        // Without a directory: a shared-sandbox directory is resolved by a command this fake gateway does not
        // serve, and the conversion does not read the old directory anyway.
        var legacy = await fixture.ProvisionAsync(null, dedicated: false);
        _ = fixture.BuildAgent(legacy);
        var shared = fixture.Gateway.Creates.Single();
        WorkingDir(shared.Body).Should().BeNull();

        using var response = await fixture.ReleaseAsync(legacy, "slots/r-3/PRs/r-7");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Gateway.Deletes.Should().BeEmpty("the shared sandbox serves other threads");
        fixture.Pool.ActiveAgentCount.Should().Be(0, "the agent held the shared sandbox's session id");
        _ = fixture.BuildAgent(legacy);
        fixture.Gateway.Creates.Should().HaveCount(2);
        WorkingDir(fixture.Gateway.Creates[1].Body).Should().Be("slots/r-3/PRs/r-7");
        fixture.Registry.GetThreads(fixture.Gateway.Creates[1].SessionId).Should().Equal(legacy);
    }

    [Fact]
    public async Task Provisioning_a_dedicated_sandbox_over_http_stores_and_acknowledges_it_and_jails_the_first_sandbox()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.ProvisionOverHttpAsync("PRs/r-7", dedicated: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        reply.RootElement.GetProperty("dedicatedSandboxAccepted").GetBoolean().Should().BeTrue();
        var threadId = reply.RootElement.GetProperty("threadId").GetString()!;
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, threadId)).Should().BeTrue();

        _ = fixture.BuildAgent(threadId);

        WorkingDir(fixture.Gateway.Creates.Single().Body).Should().Be("PRs/r-7");
        fixture.Registry.GetThreads(fixture.Gateway.Creates.Single().SessionId).Should().Equal(threadId);
        fixture
            .Registry.TryGetSessionById(fixture.Gateway.Creates.Single().SessionId, out var session)
            .Should()
            .BeTrue();
        session!.OwnerThreadId.Should().Be(threadId);
    }

    [Fact]
    public async Task Provisioning_a_dedicated_sandbox_without_a_directory_is_refused_before_anything_is_stored()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.ProvisionOverHttpAsync(directory: null, dedicated: true);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("working_directory_invalid");
        (await fixture.Store.ListThreadsAsync()).Should().BeEmpty("a refused provision stores no thread");
        fixture.Gateway.Creates.Should().BeEmpty();
    }

    [Fact]
    public async Task Provisioning_refuses_a_directory_outside_the_workspaces_own_and_accepts_one_inside()
    {
        // "slots/r-10" shares the text prefix "slots/r-1" but is a sibling, not a descendant.
        await using var fixture = await StartAsync(workspaceWorkingDir: "slots/r-1");

        using var outside = await fixture.ProvisionOverHttpAsync("slots/r-10/PRs/r-7", dedicated: true);
        using var inside = await fixture.ProvisionOverHttpAsync("slots/r-1/PRs/r-7", dedicated: true);

        outside.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await outside.Content.ReadAsStringAsync()).Should().Contain("working_directory_invalid");
        inside.StatusCode.Should().Be(HttpStatusCode.OK);
        var threads = await fixture.Store.ListThreadsAsync();
        threads.Should().ContainSingle("only the inside directory was stored");
        _ = fixture.BuildAgent(threads[0].ThreadId);
        fixture.Gateway.Creates.Select(create => WorkingDir(create.Body)).Should().Equal("slots/r-1/PRs/r-7");
    }

    [Fact]
    public async Task A_padded_provider_id_is_checked_as_the_pool_reads_it_and_stored_trimmed()
    {
        // The pool trims a provider id before it looks it up, so " codex " runs as codex, which cannot enforce a
        // directory. The check must see it the same way, and what is stored must be what was checked.
        await using var fixture = await StartAsync();

        using var cli = await fixture.ProvisionOverHttpAsync("PRs/r-7", dedicated: true, providerId: " codex ");
        using var padded = await fixture.ProvisionOverHttpAsync("PRs/r-8", dedicated: true, providerId: " test ");

        cli.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await cli.Content.ReadAsStringAsync()).Should().Contain("working_directory_invalid");
        padded.StatusCode.Should().Be(HttpStatusCode.OK);
        var threads = await fixture.Store.ListThreadsAsync();
        threads.Should().ContainSingle("the refused provision stores nothing");
        var metadata = await fixture.Store.LoadMetadataAsync(threads[0].ThreadId);
        metadata!.Properties![MultiTurnAgentPool.ProviderPropertyKey].ToString().Should().Be("test");
    }

    [Fact]
    public async Task The_capabilities_route_reports_dedicated_sandboxes_on_a_host_with_a_sandbox_registry()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.Client.GetAsync("/api/conversations/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // The wire name the companion client gates dedicated operation on.
        reply.RootElement.GetProperty("dedicatedSandbox").ValueKind.Should().Be(JsonValueKind.True);
    }

    [Fact]
    public async Task The_release_route_refuses_while_accepted_input_waits_and_changes_nothing()
    {
        await using var fixture = await StartAsync();
        var a = await fixture.ProvisionAsync("PRs/r-7");
        var agent = fixture.BuildAgent(a);
        var session = fixture.Gateway.Creates.Single().SessionId;
        // A send was accepted and no run has taken it yet.
        ((IInputAcceptanceObserver)fixture.Pool)
            .OnInputAccepted(a, "input-1", agent)
            .Should()
            .BeTrue();

        using var response = await fixture.ReleaseAsync(a, "PRs/r-9");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync())
            .Should()
            .Contain("sandbox_release_while_running")
            .And.Contain("work in hand", "the pool reports the waiting input as Busy");
        fixture.BuildAgent(a).Should().BeSameAs(agent, "the agent with work in hand stays");
        fixture.Registry.TryGetExistingSession(fixture.WorkspaceId, false, a)!.SessionId.Should().Be(session);
        fixture.Gateway.DeleteAttempts.Should().Be(0);
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, a)).Should().Be("PRs/r-7");
    }

    [Fact]
    public async Task A_refused_delete_held_in_flight_answers_503_moves_nothing_and_a_retry_completes_the_release()
    {
        await using var fixture = await StartAsync();
        var a = await fixture.ProvisionAsync("PRs/r-7");
        _ = fixture.BuildAgent(a);
        var old = fixture.Gateway.Creates.Single().SessionId;
        var gate = fixture.Gateway.HoldDeletes();

        // Removing the agent starts a release of its sandbox; the route must report that DELETE's outcome.
        var post = fixture.ReleaseAsync(a, "PRs/r-9");
        await fixture.Gateway.WaitForDeleteStartedAsync();
        await Task.Delay(100);
        post.IsCompleted.Should().BeFalse("the route answers only once the gateway has answered the DELETE");
        fixture.Gateway.FailDeletes = true;
        gate.SetResult();
        using var refused = await post;
        Logger.LogDebug(
            "Release of {ThreadId} answered {Status} after {Attempts} DELETE attempt(s)",
            a,
            refused.StatusCode,
            fixture.Gateway.DeleteAttempts
        );

        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("sandbox_release_failed");
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, a)).Should().Be("PRs/r-7", "nothing is moved");

        // The next turn: a fresh sandbox, never the one still being retired, in the directory still stored.
        _ = fixture.BuildAgent(a);
        fixture.Gateway.Creates.Should().HaveCount(2);
        fixture.Gateway.Creates[1].SessionId.Should().NotBe(old);
        WorkingDir(fixture.Gateway.Creates[1].Body).Should().Be("PRs/r-7");

        fixture.Gateway.FailDeletes = false;
        using var retried = await fixture.ReleaseAsync(a, "PRs/r-9");
        fixture.LogGateway();

        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Gateway.Deletes.Should().BeEquivalentTo([old, fixture.Gateway.Creates[1].SessionId]);
        _ = fixture.BuildAgent(a);
        WorkingDir(fixture.Gateway.Creates[2].Body).Should().Be("PRs/r-9");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_turn_admitted_while_a_release_moves_the_thread_builds_in_the_new_directory(bool wasDedicated)
    {
        // The route unloads the agent, then deletes the sandbox and only then stores the new directory. A turn that
        // arrives in between builds a new agent at once; it must read what the release commits, not what it replaces.
        await using var fixture = await StartAsync();
        string a;
        if (wasDedicated)
        {
            a = await fixture.ProvisionAsync("PRs/r-7");
            _ = fixture.BuildAgent(a);
        }
        else
        {
            // A shared-sandbox thread becoming dedicated. It also owns a sandbox here, so the release has a DELETE
            // to hold while the turn arrives.
            a = await fixture.ProvisionAsync(null, dedicated: false);
            _ = fixture.BuildAgent(a);
            _ = await fixture.Registry.GetOrCreateSessionAsync(
                new WorkspaceRef(fixture.WorkspaceId, WorkingDir: "PRs/r-7", OwnerThreadId: a)
            );
        }

        var before = fixture.Gateway.Creates.Count;
        var gate = fixture.Gateway.HoldDeletes();
        var post = fixture.ReleaseAsync(a, "PRs/r-9");
        await fixture.Gateway.WaitForDeleteStartedAsync();
        // The agent's removal may start that DELETE before the route queues its move; admit the turn only once the
        // move is queued, or the route would rightly refuse with "a new turn took the conversation".
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (fixture.Registry.WaitForOwnerMoveAsync(a).IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var turn = Task.Run(() => fixture.BuildAgent(a));
        await Task.Delay(100);
        Logger.LogDebug(
            "Turn for {ThreadId} done before the DELETE answered: {Done}; release done: {Released}",
            a,
            turn.IsCompleted,
            post.IsCompleted
        );
        turn.IsCompleted.Should().BeFalse("the turn waits for the move to commit");
        gate.SetResult();
        using var response = await post;
        _ = await turn;
        fixture.LogGateway();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = fixture.Gateway.Creates.Skip(before).ToList();
        created.Should().ContainSingle("the turn creates exactly one sandbox, after the move");
        WorkingDir(created[0].Body).Should().Be("PRs/r-9", "the old directory must not be cached in the new agent");
        var own = fixture.Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress: false, a);
        own.Should().NotBeNull("the turn runs in the thread's own sandbox");
        own!.SessionId.Should().Be(created[0].SessionId);
        own.WorkspaceWorkingDir.Should().Be("PRs/r-9");
        fixture.Registry.GetThreads(created[0].SessionId).Should().Equal(a);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fork_of_a_dedicated_thread_gets_its_own_sandbox_jailed_to_the_same_directory(
        bool readOnlyEgress
    )
    {
        // Sandboxes are never shared; workspaces are. The fork works in its source's directory, from its own sandbox.
        await using var fixture = await StartAsync();
        var a = await fixture.ProvisionAsync(
            "PRs/r-7",
            properties: readOnlyEgress
                ? new Dictionary<string, object> { [ConversationReadOnlyEgress.PropertyKey] = true }
                : null
        );
        await fixture.Store.AppendMessagesAsync(
            a,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Role = Role.User, Text = "review r-7" },
                    a,
                    "run-1"
                ),
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Role = Role.Assistant, Text = "two findings" },
                    a,
                    "run-1"
                ),
            ]
        );
        _ = fixture.BuildAgent(a);
        var parent = fixture.Gateway.Creates.Single().SessionId;

        using var response = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{a}/fork",
            new ForkConversationRequest { AfterRunId = "run-1" }
        );
        var body = await response.Content.ReadAsStringAsync();
        Logger.LogDebug("Fork of {ThreadId} answered {Status}: {Body}", a, (int)response.StatusCode, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var fork = JsonSerializer.Deserialize<ForkConversationResponse>(body, JsonSerializerOptions.Web)!.ThreadId;
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, fork)).Should().BeTrue();
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, fork)).Should().Be("PRs/r-7");
        (await ConversationReadOnlyEgress.ReadAsync(fixture.Store, fork)).Should().Be(readOnlyEgress);

        _ = fixture.BuildAgent(fork);
        fixture.LogGateway();

        fixture.Gateway.Creates.Should().HaveCount(2, "the fork must not share its source's sandbox");
        var created = fixture.Gateway.Creates[1];
        created.SessionId.Should().NotBe(parent);
        WorkingDir(created.Body).Should().Be("PRs/r-7");
        var own = fixture.Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress, fork);
        own.Should().NotBeNull("the fork runs in a sandbox of its own");
        own!.SessionId.Should().Be(created.SessionId);
        own.OwnerThreadId.Should().Be(fork);
        own.ReadOnlyEgress.Should().Be(readOnlyEgress);
        fixture.Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress, a)!.SessionId.Should().Be(parent);
        fixture
            .Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress, null)
            .Should()
            .BeNull("neither conversation runs in the shared sandbox");
    }

    [Fact]
    public async Task The_release_route_answers_404_for_an_unknown_thread_and_changes_nothing()
    {
        await using var fixture = await StartAsync();
        var unknown = $"review-{Guid.NewGuid():N}";

        using var response = await fixture.ReleaseAsync(unknown, "PRs/r-9");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unknown_thread");
        (await fixture.Store.LoadMetadataAsync(unknown)).Should().BeNull("a refused release stores nothing");
        fixture.Gateway.DeleteAttempts.Should().Be(0);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/abs")]
    [InlineData("")]
    [InlineData("slots/r-10/PRs/r-7")] // outside the workspace's own working directory
    public async Task The_release_route_refuses_a_bad_directory_before_anything_changes(string directory)
    {
        await using var fixture = await StartAsync(workspaceWorkingDir: "slots/r-1");
        var a = await fixture.ProvisionAsync("slots/r-1/PRs/r-7");
        var agent = fixture.BuildAgent(a);

        using var response = await fixture.ReleaseAsync(a, directory);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("working_directory_invalid");
        fixture.BuildAgent(a).Should().BeSameAs(agent, "a refused release unloads nothing");
        fixture.Gateway.DeleteAttempts.Should().Be(0);
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, a)).Should().Be("slots/r-1/PRs/r-7");
    }

    [Theory]
    [InlineData("claude")]
    [InlineData(" claude ")] // stored padded by a host from before provision trimmed it
    public async Task The_release_route_refuses_to_jail_a_conversation_whose_provider_cannot_enforce_a_directory(
        string provider
    )
    {
        // A conversation from before dedicated sandboxes, on a CLI provider: moved into a dedicated sandbox, its next
        // build would refuse the working directory, so the move is refused up front and nothing is stored.
        await using var fixture = await StartAsync();
        var legacy = await fixture.ProvisionAsync(
            null,
            dedicated: false,
            properties: new Dictionary<string, object> { [MultiTurnAgentPool.ProviderPropertyKey] = provider }
        );

        using var response = await fixture.ReleaseAsync(legacy, "PRs/r-9");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("working_directory_invalid");
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, legacy)).Should().BeNull();
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, legacy)).Should().BeFalse();
        fixture.Gateway.DeleteAttempts.Should().Be(0);
    }

    [Fact]
    public async Task A_thread_whose_dedicated_flag_is_stored_false_runs_in_the_shared_sandbox()
    {
        await using var fixture = await StartAsync();
        var a = await fixture.ProvisionAsync(
            null,
            dedicated: false,
            properties: new Dictionary<string, object> { [ConversationDedicatedSandbox.PropertyKey] = false }
        );

        _ = fixture.BuildAgent(a);

        var create = fixture.Gateway.Creates.Single();
        WorkingDir(create.Body).Should().BeNull();
        fixture.Registry.TryGetSessionById(create.SessionId, out var session).Should().BeTrue();
        session!.OwnerThreadId.Should().BeNull("false is the shared sandbox");
        fixture.Registry.TryGetExistingSession(fixture.WorkspaceId, false)!.SessionId.Should().Be(create.SessionId);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("null")]
    [InlineData("object")]
    public async Task A_thread_with_an_unreadable_dedicated_flag_gets_no_agent_and_no_sandbox(string kind)
    {
        await using var fixture = await StartAsync();
        object malformed = kind switch
        {
            "string" => "true",
            "null" => null!,
            _ => new Dictionary<string, string> { ["enabled"] = "true" },
        };
        var a = await fixture.ProvisionAsync(
            "PRs/r-7",
            dedicated: false,
            properties: new Dictionary<string, object> { [ConversationDedicatedSandbox.PropertyKey] = malformed }
        );

        var build = () => fixture.BuildAgent(a);

        build.Should().Throw<Exception>().Which.ToString().Should().Contain("refusing to build its agent");
        fixture.Gateway.Creates.Should().BeEmpty("an unreadable flag must not fall back to the shared sandbox");
        fixture.Pool.ActiveAgentCount.Should().Be(0);
    }

    [Fact]
    public async Task A_session_of_another_owner_in_the_threads_slot_refuses_to_build_its_agent()
    {
        // Defence in depth at the one place a thread is handed a sandbox: whatever the registry returns, a thread
        // never runs in a sandbox jailed to another conversation's directory.
        await using var fixture = await StartAsync();
        var a = await fixture.ProvisionAsync("PRs/r-7");
        var intruder = new SandboxSession(
            fixture.WorkspaceId,
            "sess-intruder",
            ".",
            "/host/intruder",
            WorkspaceWorkingDir: "PRs/r-8",
            OwnerThreadId: "thread-b"
        );
        var slots =
            (ConcurrentDictionary<(string, string, bool, string?), Lazy<Task<SandboxSession>>>)
                typeof(SandboxSessionRegistry)
                    .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(fixture.Registry)!;
        slots[(fixture.WorkspaceId, AppId, false, a)] = new Lazy<Task<SandboxSession>>(Task.FromResult(intruder));

        var build = () => fixture.BuildAgent(a);

        build
            .Should()
            .Throw<Exception>()
            .Which.ToString()
            .Should()
            .Contain($"asked for the sandbox of owner '{a}'")
            .And.Contain("sess-intruder");
        fixture.Pool.ActiveAgentCount.Should().Be(0);
    }

    [Fact]
    public async Task A_dedicated_thread_reloaded_after_an_agent_unload_or_a_host_restart_gets_its_own_jailed_sandbox_again()
    {
        var root = FakeGatewayWebAppFactory.NewRoot();
        try
        {
            string a;
            string workspaceId;
            OwnedSandboxGateway firstGateway;
            await using (var first = await StartAsync(root: root))
            {
                workspaceId = first.WorkspaceId;
                firstGateway = first.Gateway;
                a = await first.ProvisionAsync("PRs/r-7");
                _ = first.BuildAgent(a);
                AssertOwnedAndJailed(first, a, "the first build");
                var firstSession = first.Gateway.Creates.Single().SessionId;

                // Idle unload: the agent leaves the pool; the next turn rebuilds it from the stored metadata.
                first.Pool.TryGetHandoffState(a, out var handoff).Should().BeTrue();
                (await first.Pool.TryReleaseIdleAgentAsync(a, handoff))
                    .Should()
                    .Be(MultiTurnAgentPool.AgentReleaseOutcome.Released);
                first.Pool.ActiveAgentCount.Should().Be(0);
                (await first.Gateway.NextDeleteAsync())
                    .Should()
                    .Be(firstSession, "the unloaded agent's own sandbox goes with it");
                _ = first.BuildAgent(a);
                AssertOwnedAndJailed(first, a, "the rebuild after an idle unload");
                first.Gateway.Creates.Should().HaveCount(2, "the rebuild makes exactly one more sandbox");
                first.Gateway.Creates[1].SessionId.Should().NotBe(firstSession);
                first.Gateway.Deletes.Should().Equal(firstSession);
                first.Gateway.Creates.Should().OnlyContain(create => WorkingDir(create.Body) == "PRs/r-7");
            }

            // A graceful shutdown deletes the sandbox the stopped host still held; the next host makes its own.
            // The test host disposes its container on the app's own thread, after the factory's dispose returns,
            // so wait for that DELETE rather than read the list at once.
            (await firstGateway.NextDeleteAsync())
                .Should()
                .Be(firstGateway.Creates[1].SessionId, "shutdown deletes the sandbox the host still held");
            firstGateway
                .Deletes.Should()
                .BeEquivalentTo(firstGateway.Creates.Select(create => create.SessionId), "shutdown deletes it");

            // Restart: a new host, registry and pool over the same stores. Only the stored metadata carries the
            // flag and the directory now.
            await using var second = await StartAsync(root: root, workspaceId: workspaceId);
            (await ConversationDedicatedSandbox.ReadAsync(second.Store, a)).Should().BeTrue();
            (await ConversationWorkingDirectory.ReadAsync(second.Store, a)).Should().Be("PRs/r-7");
            _ = second.BuildAgent(a);
            AssertOwnedAndJailed(second, a, "the rebuild after a host restart");
            second.Gateway.Creates.Should().ContainSingle();
            WorkingDir(second.Gateway.Creates[0].Body).Should().Be("PRs/r-7");
        }
        finally
        {
            DetachedStoreTeardown.Purge(root);
        }
    }

    private void AssertOwnedAndJailed(Fixture fixture, string threadId, string when)
    {
        var own = fixture.Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress: false, threadId);
        var shared = fixture.Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress: false, null);
        Logger.LogDebug(
            "After {When}: thread {ThreadId} has sandbox {SessionId} (owner {Owner}, jail {Jail}); shared slot {Shared}",
            when,
            threadId,
            own?.SessionId,
            own?.OwnerThreadId,
            own?.WorkspaceWorkingDir,
            shared?.SessionId
        );
        own.Should().NotBeNull($"{when} resolves the thread's own sandbox");
        own!.OwnerThreadId.Should().Be(threadId, when);
        own.WorkspaceWorkingDir.Should().Be("PRs/r-7", when);
        shared.Should().BeNull($"{when} must not fall back to the shared sandbox");
    }

    private static string? WorkingDir(string createBody) => OwnedSandboxGateway.WorkingDir(createBody);

    [Fact]
    public async Task A_refresh_in_flight_across_a_release_never_leaves_the_old_directory_for_the_next_send()
    {
        // A send refreshes the pooled agent's sandbox first, with the agent's ref, old directory included. Here the
        // gateway has forgotten that sandbox, so the refresh recreates it, and the recreate reaches the registry while
        // the release route's move is deleting: it waits for the move, then creates. That sandbox must be in the new
        // directory, or the next send would get it (the slot is keyed without the directory).
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await StartAsync(timeProvider: clock);
        var a = await fixture.ProvisionAsync("PRs/r-7");
        _ = fixture.BuildAgent(a);
        var first = fixture.Gateway.Creates.Single().SessionId;
        clock.Advance(TimeSpan.FromMinutes(1)); // past the liveness window, so the refresh probes

        var probeGate = fixture.Gateway.HoldProbes();
        var refresh = fixture.Pool.EnsureCurrentAgentAsync(a);
        await fixture.Gateway.WaitForProbeStartedAsync(); // the refresh holds the agent's old ref now
        var deleteGate = fixture.Gateway.HoldDeletes();
        var release = fixture.ReleaseAsync(a, "PRs/r-9");
        await fixture.Gateway.WaitForDeleteStartedAsync(); // the move has begun: the old sandbox is out of its slot
        fixture.Gateway.Forget(first);
        probeGate.SetResult(); // 404: the refresh recreates from its old ref, and waits for the move

        // The refresh must read its 404 before the DELETE completes: the DELETE's eviction would cancel a probe still
        // in flight, and a cancelled probe reads as alive. The 404 path evicts the session itself first, and nothing
        // else can while the DELETE is held.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (fixture.Registry.TryGetSessionById(first, out _))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the refresh was expected to read its 404");
            await Task.Delay(10);
        }

        deleteGate.SetResult();
        using (var released = await release)
        {
            released.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await fixture.Gateway.NextDeleteAsync()).Should().Be(first);
        var refreshed = await refresh
            .Invoking(task => task.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should()
            .ThrowAsync<AgentNotPooledException>();
        Logger.LogDebug("The held refresh ended: {Outcome}", refreshed.Which.Message);

        _ = fixture.BuildAgent(a); // the next send
        fixture.LogGateway();
        var own = fixture.Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress: false, a);
        own.Should().NotBeNull();
        own!.WorkspaceWorkingDir.Should().Be("PRs/r-9", "the next send must not get a sandbox in the old directory");
        fixture
            .Gateway.Creates.Skip(1)
            .Should()
            .OnlyContain(
                create => WorkingDir(create.Body) == "PRs/r-9",
                "nothing is created in the old directory after the move"
            );
        fixture.Gateway.Creates.Should().HaveCount(2, "the move costs exactly one new sandbox");
    }

    private Task<Fixture> StartAsync(
        string? workspaceWorkingDir = null,
        string? root = null,
        string? workspaceId = null,
        TimeProvider? timeProvider = null
    ) => Fixture.StartAsync(Logger, workspaceWorkingDir, root, workspaceId, timeProvider);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly FakeGatewayWebAppFactory _host;
        private readonly ILogger _logger;

        private Fixture(OwnedSandboxGateway gateway, FakeGatewayWebAppFactory host, ILogger logger)
        {
            Gateway = gateway;
            _host = host;
            _logger = logger;
            Client = host.CreateClient();
        }

        public OwnedSandboxGateway Gateway { get; }
        public HttpClient Client { get; }
        public string WorkspaceId { get; private set; } = string.Empty;
        public MultiTurnAgentPool Pool => _host.Services.GetRequiredService<MultiTurnAgentPool>();
        public SandboxSessionRegistry Registry => _host.Services.GetRequiredService<SandboxSessionRegistry>();
        public IConversationStore Store => _host.Services.GetRequiredService<IConversationStore>();

        public static async Task<Fixture> StartAsync(
            ILogger logger,
            string? workspaceWorkingDir,
            string? root = null,
            string? workspaceId = null,
            TimeProvider? timeProvider = null
        )
        {
            var gateway = new OwnedSandboxGateway();
            var fixture = new Fixture(
                gateway,
                new FakeGatewayWebAppFactory(gateway, AppId, root: root, timeProvider: timeProvider),
                logger
            );
            try
            {
                fixture.WorkspaceId =
                    workspaceId
                    ?? (
                        await fixture
                            ._host.Services.GetRequiredService<IWorkspaceStore>()
                            .CreateAsync(new WorkspaceCreate { Name = "Reviews", WorkingDir = workspaceWorkingDir })
                    ).Id;
            }
            catch
            {
                // The caller never gets the fixture to dispose, so a host that started and then failed goes here.
                await fixture.DisposeAsync();
                throw;
            }
            logger.LogDebug(
                "Host up: app {AppId}, workspace {WorkspaceId} with working directory {WorkspaceWorkingDir}, stores in {Root}",
                AppId,
                fixture.WorkspaceId,
                workspaceWorkingDir,
                fixture._host.Root
            );
            return fixture;
        }

        /// <summary>Stores a thread exactly as the provision route stores one, plus any extra properties.</summary>
        public async Task<string> ProvisionAsync(
            string? workingDirectory,
            bool dedicated = true,
            IReadOnlyDictionary<string, object>? properties = null
        )
        {
            var threadId = $"review-{Guid.NewGuid():N}";
            var stored = ImmutableDictionary<string, object>.Empty.Add(
                MultiTurnAgentPool.WorkspacePropertyKey,
                WorkspaceId
            );
            if (workingDirectory is not null)
            {
                stored = stored.Add(ConversationWorkingDirectory.PropertyKey, workingDirectory);
            }

            if (dedicated)
            {
                stored = stored.Add(ConversationDedicatedSandbox.PropertyKey, true);
            }

            stored = stored.SetItems(properties ?? ImmutableDictionary<string, object>.Empty);
            await Store.UpdateMetadataAsync(
                threadId,
                _ => new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Properties = stored,
                }
            );
            _logger.LogDebug(
                "Stored thread {ThreadId}: directory {WorkingDirectory}, dedicated {Dedicated}, extra {Extra}",
                threadId,
                workingDirectory,
                dedicated,
                properties?.Keys
            );
            return threadId;
        }

        /// <summary>Builds (or returns) the thread's agent, as a turn does.</summary>
        public IMultiTurnAgent BuildAgent(string threadId) =>
            Pool.GetOrCreateAgent(threadId, WorkspaceMode, "test", null, requestedWorkspaceId: WorkspaceId);

        public Task<HttpResponseMessage> ProvisionOverHttpAsync(
            string? directory,
            bool dedicated,
            string providerId = "test"
        ) =>
            Client.PostAsJsonAsync(
                "/api/conversations",
                new ProvisionConversationRequest
                {
                    WorkspaceId = WorkspaceId,
                    ProviderId = providerId,
                    ModeId = SystemChatModes.WorkspaceAgentModeId,
                    WorkingDirectoryRelPath = directory,
                    DedicatedSandbox = dedicated,
                }
            );

        public async Task<HttpResponseMessage> ReleaseAsync(string threadId, string? directory)
        {
            var response = await Client.PostAsJsonAsync(
                $"/api/conversations/{threadId}/sandbox/release",
                new ReleaseSandboxRequest { WorkingDirectoryRelPath = directory }
            );
            _logger.LogDebug(
                "Release of {ThreadId} to {Directory} answered {Status}: {Body}",
                threadId,
                directory,
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync()
            );
            return response;
        }

        public void LogGateway() =>
            _logger.LogDebug(
                "Gateway saw creates {Creates} ({Directories}), delete attempts {DeleteAttempts}, confirmed deletes {Deletes}",
                Gateway.Creates.Select(create => create.SessionId),
                Gateway.Creates.Select(create => WorkingDir(create.Body)),
                Gateway.DeleteAttempts,
                Gateway.Deletes
            );

        public async ValueTask DisposeAsync()
        {
            LogGateway();
            Client.Dispose();
            await _host.DisposeAsync();
            _logger.LogDebug("Host stopped; gateway saw confirmed deletes {Deletes}", Gateway.Deletes);
        }
    }
}
