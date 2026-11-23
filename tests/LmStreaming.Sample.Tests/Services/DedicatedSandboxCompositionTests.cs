using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using LmStreaming.Sample.SandboxApps;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    [Theory]
    [InlineData(".")] // the workspace root holds the Mini App directory
    [InlineData("mini-web-apps")]
    [InlineData("mini-web-apps/demo")]
    [InlineData("./Mini-Web-Apps//demo/")] // normalized, and case-insensitive
    public async Task A_dedicated_directory_that_is_holds_or_lies_inside_the_Mini_App_directory_is_refused(
        string directory
    )
    {
        // The host runs Mini Apps outside the jail (the operations API). An agent that could write one could act
        // anywhere in the workspace, so no dedicated sandbox may be jailed where the apps live.
        await using var fixture = await StartAsync();
        var legacy = await fixture.ProvisionAsync(null, dedicated: false);

        using var provisioned = await fixture.ProvisionOverHttpAsync(directory, dedicated: true);
        using var released = await fixture.ReleaseAsync(legacy, directory);
        var provisionBody = await provisioned.Content.ReadAsStringAsync();
        Logger.LogDebug(
            "Provision with {Directory} answered {Status}: {Body}",
            directory,
            (int)provisioned.StatusCode,
            provisionBody
        );

        provisioned.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        provisionBody.Should().Contain("working_directory_invalid");
        (await fixture.Store.ListThreadsAsync()).Should().ContainSingle("the refused provision stores nothing");
        released.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await released.Content.ReadAsStringAsync()).Should().Contain("working_directory_invalid");
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, legacy)).Should().BeFalse();
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, legacy)).Should().BeNull();
        fixture.Gateway.DeleteAttempts.Should().Be(0);
    }

    [Theory]
    [InlineData(ToolGroups.MiniAppDebug)]
    [InlineData(ToolGroups.BrowserDebug)]
    public async Task A_dedicated_conversation_is_refused_a_mode_that_serves_Mini_Apps_at_provision_mode_switch_and_release(
        string toolGroup
    )
    {
        // Mini App debugging runs apps, and browser debugging opens one by id, through the operations API, which the
        // jail does not cover. Both are on here, so the refusal is the dedicated sandbox's and nothing else's.
        await using var fixture = await StartAsync(configureServices: EnableMiniAppsAndBrowserDebug);
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(toolGroup);

        using var provisioned = await fixture.ProvisionOverHttpAsync(
            "PRs/r-7",
            dedicated: true,
            modeId: miniAppMode.Id
        );
        var provisionBody = await provisioned.Content.ReadAsStringAsync();
        Logger.LogDebug(
            "Provision in {ModeId} answered {Status}: {Body}",
            miniAppMode.Id,
            (int)provisioned.StatusCode,
            provisionBody
        );
        provisioned.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        provisionBody.Should().Contain(ConversationDedicatedSandbox.ModeUnsupportedCode);
        (await fixture.Store.ListThreadsAsync()).Should().BeEmpty("the refused provision stores nothing");

        var a = await fixture.ProvisionAsync("PRs/r-7");
        var agent = fixture.BuildAgent(a);
        using var switched = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{a}/mode",
            new SwitchModeRequest { ModeId = miniAppMode.Id }
        );
        var switchBody = await switched.Content.ReadAsStringAsync();
        Logger.LogDebug("Mode switch of {ThreadId} answered {Status}: {Body}", a, (int)switched.StatusCode, switchBody);
        switched.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        switchBody.Should().Contain(ConversationDedicatedSandbox.ModeUnsupportedCode);
        fixture.BuildAgent(a).Should().BeSameAs(agent, "a refused switch keeps the agent");

        var shared = await fixture.ProvisionAsync(
            null,
            dedicated: false,
            properties: new Dictionary<string, object> { [MultiTurnAgentPool.ModePropertyKey] = miniAppMode.Id }
        );
        using var released = await fixture.ReleaseAsync(shared, "PRs/r-9");
        released.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await released.Content.ReadAsStringAsync()).Should().Contain(ConversationDedicatedSandbox.ModeUnsupportedCode);
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, shared)).Should().BeFalse();
        fixture.Gateway.Creates.Should().ContainSingle("only the first dedicated thread got a sandbox");
        fixture.Gateway.DeleteAttempts.Should().Be(0);
    }

    [Theory]
    [InlineData(ToolGroups.MiniAppDebug)]
    [InlineData(ToolGroups.BrowserDebug)]
    public async Task A_dedicated_thread_in_a_mode_that_serves_Mini_Apps_never_gets_an_agent_nor_does_its_fork(
        string toolGroup
    )
    {
        // The routes refuse the mode, but a mode can gain such a tool after a thread took it, and a fork copies its
        // source's mode. The agent factory is the check every path goes through.
        await using var fixture = await StartAsync(configureServices: EnableMiniAppsAndBrowserDebug);
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(toolGroup);
        var a = await fixture.ProvisionAsync(
            "PRs/r-7",
            properties: new Dictionary<string, object> { [MultiTurnAgentPool.ModePropertyKey] = miniAppMode.Id }
        );
        await fixture.Store.AppendMessagesAsync(
            a,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Role = Role.User, Text = "debug the app" },
                    a,
                    "run-1"
                ),
            ]
        );

        using var forked = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{a}/fork",
            new ForkConversationRequest { AfterRunId = "run-1" }
        );
        var forkBody = await forked.Content.ReadAsStringAsync();
        Logger.LogDebug("Fork of {ThreadId} answered {Status}: {Body}", a, (int)forked.StatusCode, forkBody);
        forked.StatusCode.Should().Be(HttpStatusCode.Created);
        var fork = JsonSerializer.Deserialize<ForkConversationResponse>(forkBody, JsonSerializerOptions.Web)!.ThreadId;
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, fork)).Should().BeTrue();
        (await fixture.Store.LoadMetadataAsync(fork))!
            .Properties![MultiTurnAgentPool.ModePropertyKey]
            .ToString()
            .Should()
            .Be(miniAppMode.Id, "the fork stores its source's mode");

        // A send builds each thread's agent from the mode it has stored, as a turn does.
        foreach (var threadId in new[] { a, fork })
        {
            using var sent = await fixture.SendAsync(threadId);
            sent.StatusCode.Should().Be(HttpStatusCode.Conflict, "the factory refuses the stored mode");
            (await sent.Content.ReadAsStringAsync())
                .Should()
                .Contain("\"code\":\"dedicated_sandbox_mode_unsupported\"");
        }

        fixture
            .Invoking(f => f.Pool.GetOrCreateAgent(a, miniAppMode, "test", null, requestedWorkspaceId: f.WorkspaceId))
            .Should()
            .Throw<DedicatedSandboxModeUnsupportedException>()
            .WithMessage("*not confined to the conversation's working directory*");
        fixture.Gateway.Creates.Should().BeEmpty("a refused build creates no sandbox");
    }

    [Theory]
    [InlineData(ToolGroups.MiniAppDebug)]
    [InlineData(ToolGroups.BrowserDebug)]
    public async Task A_stop_only_release_of_a_dedicated_thread_in_a_mode_that_serves_Mini_Apps_answers_200_and_its_next_send_is_still_refused(
        string toolGroup
    )
    {
        // A release without a directory moves nothing, so it does not check the mode; the factory still refuses.
        await using var fixture = await StartAsync(configureServices: EnableMiniAppsAndBrowserDebug);
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(toolGroup);
        var a = await fixture.ProvisionAsync(
            "PRs/r-7",
            properties: new Dictionary<string, object> { [MultiTurnAgentPool.ModePropertyKey] = miniAppMode.Id }
        );

        using var released = await fixture.ReleaseAsync(a, directory: null);
        released.StatusCode.Should().Be(HttpStatusCode.OK, "a stop-only release moves nothing");
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, a)).Should().Be("PRs/r-7");

        using var sent = await fixture.SendAsync(a);
        sent.StatusCode.Should().Be(HttpStatusCode.Conflict, "the next build is still refused");
        (await sent.Content.ReadAsStringAsync()).Should().Contain(ConversationDedicatedSandbox.ModeUnsupportedCode);
        fixture.Gateway.Creates.Should().BeEmpty("a refused build creates no sandbox");
    }

    [Theory]
    [InlineData(ToolGroups.MiniAppDebug)]
    [InlineData(ToolGroups.BrowserDebug)]
    public async Task A_provider_switch_of_a_dedicated_thread_in_a_mode_that_serves_Mini_Apps_answers_409_and_builds_nothing(
        string toolGroup
    )
    {
        // A provider switch rebuilds the agent from the stored mode, so the factory refuses it as it refuses a send.
        var logs = new WarningLogCapture();
        await using var fixture = await StartAsync(configureServices: services =>
        {
            EnableMiniAppsAndBrowserDebug(services);
            logs.AddTo(services);
        });
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(toolGroup);
        var a = await fixture.ProvisionAsync(
            "PRs/r-7",
            properties: new Dictionary<string, object> { [MultiTurnAgentPool.ModePropertyKey] = miniAppMode.Id }
        );

        using var switched = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{a}/provider",
            new SwitchProviderRequest { ProviderId = "test" }
        );
        var body = await switched.Content.ReadAsStringAsync();
        Logger.LogDebug("Provider switch of {ThreadId} answered {Status}: {Body}", a, (int)switched.StatusCode, body);

        switched.StatusCode.Should().Be(HttpStatusCode.Conflict, "the same refusal a send gets, not a bare 500");
        body.Should().Contain("\"code\":\"dedicated_sandbox_mode_unsupported\"");
        fixture.Gateway.Creates.Should().BeEmpty("a refused build creates no sandbox");
        logs.ShouldHoldTheRefusalFor(a);
    }

    [Theory]
    [InlineData(ToolGroups.MiniAppDebug)]
    [InlineData(ToolGroups.BrowserDebug)]
    public async Task A_socket_to_a_dedicated_thread_in_a_mode_that_serves_Mini_Apps_gets_the_refusal_frame_before_it_closes(
        string toolGroup
    )
    {
        // The UI opens a socket on load, before any REST turn, so the socket must say why it closes.
        var logs = new WarningLogCapture();
        await using var fixture = await StartAsync(configureServices: services =>
        {
            EnableMiniAppsAndBrowserDebug(services);
            logs.AddTo(services);
        });
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(toolGroup);
        var a = await fixture.ProvisionAsync(
            "PRs/r-7",
            properties: new Dictionary<string, object> { [MultiTurnAgentPool.ModePropertyKey] = miniAppMode.Id }
        );

        var (frames, closeStatus, closeDescription) = await fixture.ConnectUntilClosedAsync(a, miniAppMode.Id);

        // The client dispatches on "$type" and shows "message", so both are pinned as sent, beside the code.
        var label = toolGroup == ToolGroups.MiniAppDebug ? "Mini App debugging" : "browser debugging";
        var refusal = frames
            .Should()
            .ContainSingle(frame => frame.Contains("\"code\":\"dedicated_sandbox_mode_unsupported\""))
            .Which;
        refusal.Should().Contain("\"$type\":\"error\"");
        using (var sent = JsonDocument.Parse(refusal))
        {
            sent.RootElement.GetProperty("$type").GetString().Should().Be("error");
            sent.RootElement.GetProperty("message")
                .GetString()
                .Should()
                .Be(
                    $"A conversation with a dedicated sandbox cannot use a mode with {label}: it runs Mini Apps through "
                        + "the gateway's operations API, which is not confined to the conversation's working directory."
                );
        }
        closeStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        closeDescription.Should().Be("Dedicated sandbox mode unsupported");
        fixture.Gateway.Creates.Should().BeEmpty("a refused build creates no sandbox");
        logs.ShouldHoldTheRefusalFor(a);
    }

    [Theory]
    [InlineData(ToolGroups.MiniAppDebug)]
    [InlineData(ToolGroups.BrowserDebug)]
    public async Task A_shared_thread_provisions_switches_into_and_builds_in_a_mode_that_serves_Mini_Apps(
        string toolGroup
    )
    {
        // Only a dedicated sandbox is jailed, so only a dedicated conversation is refused these tools.
        await using var fixture = await StartAsync(configureServices: EnableMiniAppsAndBrowserDebug);
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(toolGroup);

        using var provisioned = await fixture.ProvisionOverHttpAsync(null, dedicated: false, modeId: miniAppMode.Id);
        var provisionBody = await provisioned.Content.ReadAsStringAsync();
        Logger.LogDebug(
            "Shared provision in {ModeId} answered {Status}: {Body}",
            miniAppMode.Id,
            (int)provisioned.StatusCode,
            provisionBody
        );
        provisioned.StatusCode.Should().Be(HttpStatusCode.OK, "a shared conversation may take the mode");

        var b = await fixture.ProvisionAsync(
            null,
            dedicated: false,
            properties: new Dictionary<string, object>
            {
                [MultiTurnAgentPool.ModePropertyKey] = SystemChatModes.WorkspaceAgentModeId,
            }
        );
        using var switched = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{b}/mode",
            new SwitchModeRequest { ModeId = miniAppMode.Id }
        );
        var switchBody = await switched.Content.ReadAsStringAsync();
        Logger.LogDebug(
            "Shared switch of {ThreadId} answered {Status}: {Body}",
            b,
            (int)switched.StatusCode,
            switchBody
        );
        switched.StatusCode.Should().Be(HttpStatusCode.OK, "a shared conversation may switch into the mode");
        (await fixture.Store.LoadMetadataAsync(b))!
            .Properties![MultiTurnAgentPool.ModePropertyKey]
            .ToString()
            .Should()
            .Be(miniAppMode.Id);

        var agent = fixture.Pool.GetOrCreateAgent(
            b,
            miniAppMode,
            "test",
            null,
            requestedWorkspaceId: fixture.WorkspaceId
        );
        agent.Should().NotBeNull();
        fixture
            .Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress: false, ownerThreadId: null)
            .Should()
            .NotBeNull("the shared thread's agent runs in the shared sandbox");
    }

    [Fact]
    public async Task A_mode_switch_reads_an_unreadable_dedicated_flag_as_dedicated_and_keeps_the_mode()
    {
        // Read as false, a malformed flag would let a thread that may be dedicated into a mode that serves Mini Apps.
        await using var fixture = await StartAsync(configureServices: EnableMiniAppsAndBrowserDebug);
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(ToolGroups.MiniAppDebug);
        var a = await fixture.ProvisionAsync(
            "PRs/r-7",
            dedicated: false,
            properties: new Dictionary<string, object>
            {
                [ConversationDedicatedSandbox.PropertyKey] = "yes",
                [MultiTurnAgentPool.ModePropertyKey] = SystemChatModes.WorkspaceAgentModeId,
            }
        );

        using var switched = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{a}/mode",
            new SwitchModeRequest { ModeId = miniAppMode.Id }
        );
        var switchBody = await switched.Content.ReadAsStringAsync();
        Logger.LogDebug("Mode switch of {ThreadId} answered {Status}: {Body}", a, (int)switched.StatusCode, switchBody);

        switched.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        switchBody.Should().Contain(ConversationDedicatedSandbox.ModeUnsupportedCode);
        var stored = await fixture.Store.LoadMetadataAsync(a);
        stored!
            .Properties![MultiTurnAgentPool.ModePropertyKey]
            .ToString()
            .Should()
            .Be(SystemChatModes.WorkspaceAgentModeId, "a refused switch leaves the mode as it was");
        fixture.Gateway.Creates.Should().BeEmpty("a refused switch builds nothing");
    }

    [Fact]
    public async Task A_mode_switch_that_lands_after_the_release_checked_the_mode_keeps_the_thread_shared()
    {
        // The release reads the mode, then unloads the agent and commits the move. A switch into a Mini App mode in
        // between must not be committed into a dedicated sandbox: every later send would answer 409.
        await using var fixture = await StartHookedAsync();
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(ToolGroups.MiniAppDebug);
        var a = await fixture.ProvisionSharedWorkspaceThreadAsync();
        _ = fixture.BuildAgent(a);
        HttpStatusCode? switchStatus = null;
        fixture.Modes.BeforeNextRead(
            SystemChatModes.WorkspaceAgentModeId,
            async () => switchStatus = (await fixture.SwitchModeAsync(a, miniAppMode.Id)).Status
        );

        using var response = await fixture.ReleaseAsync(a, "PRs/r-9");

        switchStatus.Should().Be(HttpStatusCode.OK, "the thread was still shared when the switch ran");
        await AssertModeRefusedAsync(response);
        await AssertMoveNotStoredAsync(fixture, a);
        (await fixture.StoredModeAsync(a)).Should().Be(miniAppMode.Id, "the switch stands");
        fixture
            .Pool.GetOrCreateAgent(a, miniAppMode, "test", null, requestedWorkspaceId: fixture.WorkspaceId)
            .Should()
            .NotBeNull("the thread still gets an agent, in the shared sandbox");
    }

    [Fact]
    public async Task A_mode_written_between_the_commits_own_check_and_its_store_is_checked_again()
    {
        // A switch persists its mode just after it builds its agent. That write can land after the commit read the
        // mode and before it stores the move; storing anyway would commit a mode the check never saw.
        await using var fixture = await StartHookedAsync();
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(ToolGroups.MiniAppDebug);
        var a = await fixture.ProvisionSharedWorkspaceThreadAsync();
        bool? duringMove = null;
        fixture.Modes.BeforeNextRead(
            SystemChatModes.WorkspaceAgentModeId,
            async () =>
            {
                // The commit runs inside the release, which holds the thread's move fence; the route's own reads
                // run before it.
                duringMove = !fixture.Registry.WaitForOwnerMoveAsync(a).IsCompleted;
                await fixture.WriteModeAsync(a, miniAppMode.Id);
            },
            skip: RouteModeReads
        );

        using var response = await fixture.ReleaseAsync(a, "PRs/r-9");

        duringMove.Should().BeTrue("the write landed inside the commit, after the route's check");
        await AssertModeRefusedAsync(response);
        await AssertMoveNotStoredAsync(fixture, a);
    }

    [Fact]
    public async Task A_mode_changed_once_to_another_the_sandbox_allows_while_the_move_is_stored_is_moved_in_that_mode()
    {
        // A change the commit sees is checked again, not refused: the second check finds a mode a dedicated sandbox
        // may have, so the move lands in it.
        await using var fixture = await StartHookedAsync();
        var a = await fixture.ProvisionSharedWorkspaceThreadAsync();
        fixture.Modes.BeforeNextRead(
            SystemChatModes.WorkspaceAgentModeId,
            () => fixture.WriteModeAsync(a, SystemChatModes.WorkflowAuthorModeId),
            skip: RouteModeReads
        );

        using var response = await fixture.ReleaseAsync(a, "PRs/r-9");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fixture.StoredModeAsync(a)).Should().Be(SystemChatModes.WorkflowAuthorModeId);
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, a)).Should().BeTrue();
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, a)).Should().Be("PRs/r-9");
        using var sent = await fixture.SendAsync(a);
        sent.IsSuccessStatusCode.Should().BeTrue("the thread builds in the mode the move was stored in");
        fixture
            .Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress: false, a)!
            .WorkspaceWorkingDir.Should()
            .Be("PRs/r-9", "the next agent runs in its own sandbox, in the new directory");
    }

    [Fact]
    public async Task A_mode_that_keeps_changing_while_the_move_is_stored_answers_503_and_stores_nothing()
    {
        // The commit checks again when the mode changed under it, but not for ever: the release holds the thread's
        // next sandbox back until it ends.
        await using var fixture = await StartHookedAsync();
        var a = await fixture.ProvisionSharedWorkspaceThreadAsync();
        var writes = 0;
        void FlipOnRead(string modeId, int skip) =>
            fixture.Modes.BeforeNextRead(
                modeId,
                async () =>
                {
                    var next =
                        modeId == SystemChatModes.WorkspaceAgentModeId
                            ? SystemChatModes.DefaultModeId
                            : SystemChatModes.WorkspaceAgentModeId;
                    _ = Interlocked.Increment(ref writes);
                    await fixture.WriteModeAsync(a, next);
                    FlipOnRead(next, skip: 0);
                },
                skip
            );
        // Past the route's reads, every read the commit makes changes the mode before the commit stores.
        FlipOnRead(SystemChatModes.WorkspaceAgentModeId, skip: RouteModeReads);

        using var response = await fixture.ReleaseAsync(a, "PRs/r-9").WaitAsync(TimeSpan.FromSeconds(30));

        Logger.LogDebug("Mode writes during the commit: {Writes}", writes);
        await AssertReleaseFailedAsync(response);
        writes.Should().Be(3, "the commit checks three times, then gives up");
        await AssertMoveNotStoredAsync(fixture, a);
    }

    [Fact]
    public async Task A_thread_deleted_between_the_commits_reload_and_its_store_is_not_written_back()
    {
        // The commit reloads the thread, then stores the move in a separate step. A delete in between leaves nothing
        // to move; writing the move anyway would bring the thread back.
        await using var fixture = await StartHookedAsync();
        var a = await fixture.ProvisionSharedWorkspaceThreadAsync();
        fixture.Modes.BeforeNextRead(
            SystemChatModes.WorkspaceAgentModeId,
            () => fixture.Store.DeleteThreadAsync(a),
            skip: RouteModeReads
        );

        using var response = await fixture.ReleaseAsync(a, "PRs/r-9");

        await AssertReleaseFailedAsync(response);
        (await fixture.Store.LoadMetadataAsync(a)).Should().BeNull("the deleted thread stays deleted");
        var failure = fixture.Logs.Events.Single(logEvent =>
            logEvent.MessageTemplate.Text.StartsWith(
                "Released the dedicated sandbox of thread",
                StringComparison.Ordinal
            )
            && logEvent.Level == Serilog.Events.LogEventLevel.Error
        );
        failure
            .Exception.Should()
            .BeOfType<InvalidOperationException>("a deleted thread is refused by name, not by a null dereference")
            .Which.Message.Should()
            .Contain("deleted");
    }

    [Theory]
    [InlineData("the conversation store fails the commit's reload")]
    [InlineData("the mode store fails the commit's mode lookup")]
    public async Task A_store_failure_after_the_delete_answers_503_stores_nothing_and_a_retry_moves_the_thread(
        string failure
    )
    {
        // The sandbox is gone by the time the commit runs. Its failure must not read as a stored move, and the
        // thread then owns no sandbox, so a retry deletes nothing and stores the move.
        MetadataLoadFaultStore? conversations = null;
        await using var fixture = await StartHookedAsync(services =>
            conversations = MetadataLoadFaultStore.Install(services)
        );
        var a = await fixture.ProvisionSharedWorkspaceThreadAsync();
        var owned = await fixture.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(fixture.WorkspaceId, WorkingDir: "PRs/r-7", OwnerThreadId: a)
        );
        if (failure.Contains("reload", StringComparison.Ordinal))
        {
            // The route's own mode check is its last read before the release; the commit's reload comes next.
            fixture.Modes.BeforeNextRead(
                SystemChatModes.WorkspaceAgentModeId,
                () =>
                {
                    conversations!.FailNextLoad(a);
                    return Task.CompletedTask;
                },
                skip: RouteModeReads - 1
            );
        }
        else
        {
            fixture.Modes.BeforeNextRead(
                SystemChatModes.WorkspaceAgentModeId,
                () => throw new IOException("The mode store failed."),
                skip: RouteModeReads
            );
        }

        using var failed = await fixture.ReleaseAsync(a, "PRs/r-9");

        await AssertReleaseFailedAsync(failed);
        fixture.Gateway.Deletes.Should().Equal([owned.SessionId], "the sandbox was deleted before the commit ran");
        await AssertMoveNotStoredAsync(fixture, a);
        using var retried = await fixture.ReleaseAsync(a, "PRs/r-9");
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, a)).Should().BeTrue();
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, a)).Should().Be("PRs/r-9");
        fixture.Gateway.DeleteAttempts.Should().Be(1, "the retry had no sandbox left to delete");
    }

    [Fact]
    public async Task A_mode_switch_that_arrives_while_the_release_commits_is_refused_and_keeps_the_mode()
    {
        // The other order: the switch checks the flag while the thread is still shared, then waits for the move.
        // By the time it builds, the thread is dedicated, so it must get the refusal, not a server error.
        await using var fixture = await StartHookedAsync();
        var miniAppMode = await fixture.CreateSandboxModeWithAsync(ToolGroups.MiniAppDebug);
        var a = await fixture.ProvisionSharedWorkspaceThreadAsync();
        _ = fixture.BuildAgent(a);
        // It also owns a sandbox, so the release has a DELETE to hold while the switch arrives.
        _ = await fixture.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(fixture.WorkspaceId, WorkingDir: "PRs/r-7", OwnerThreadId: a)
        );
        var gate = fixture.Gateway.HoldDeletes();
        var post = fixture.ReleaseAsync(a, "PRs/r-9");
        await fixture.Gateway.WaitForDeleteStartedAsync();
        // The release puts the thread's move fence up as it hands the DELETE to the thread pool.
        SpinWait
            .SpinUntil(() => !fixture.Registry.WaitForOwnerMoveAsync(a).IsCompleted, Patience)
            .Should()
            .BeTrue("the release holds the thread's move fence while its DELETE runs");

        var pastGuard = fixture.Logs.WaitForAsync(PoolSwitchTemplate, a, Patience);
        var switching = fixture.SwitchModeAsync(a, miniAppMode.Id);
        // The pool logs this only once the route passed its dedicated-flag check and handed it the switch, so the
        // switch can no longer be refused there.
        _ = await pastGuard;
        gate.SetResult();
        using var released = await post;
        var (switchStatus, switchBody) = await switching;

        released.StatusCode.Should().Be(HttpStatusCode.OK);
        switchStatus.Should().Be(HttpStatusCode.BadRequest);
        switchBody.Should().Contain(ConversationDedicatedSandbox.ModeUnsupportedCode);
        (await fixture.StoredModeAsync(a))
            .Should()
            .Be(SystemChatModes.WorkspaceAgentModeId, "a refused switch leaves the mode as it was");
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, a)).Should().BeTrue();
        _ = fixture.BuildAgent(a);
        fixture
            .Registry.TryGetExistingSession(fixture.WorkspaceId, readOnlyEgress: false, a)!
            .WorkspaceWorkingDir.Should()
            .Be("PRs/r-9", "the thread's next agent runs in its own sandbox, in the new directory");
    }

    /// <summary>How many times the release route reads the mode before its commit: validating the directory, then
    /// checking the mode.</summary>
    private const int RouteModeReads = 2;

    /// <summary>The pool's log line as it takes a mode switch the route has already let through.</summary>
    private const string PoolSwitchTemplate = "Switching agent for thread {ThreadId} to mode {ModeId} ({ModeName})";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A host with Mini Apps and browser debugging on, its mode store hooked (<see cref="Fixture.Modes"/>) and its
    /// logs captured (<see cref="Fixture.Logs"/>).
    /// </summary>
    private Task<Fixture> StartHookedAsync(Action<IServiceCollection>? configureServices = null) =>
        StartAsync(configureServices: services =>
        {
            EnableMiniAppsAndBrowserDebug(services);
            _ = ModeReadHookStore.Install(services);
            _ = HostLogCapture.Install(services);
            configureServices?.Invoke(services);
        });

    private static async Task AssertModeRefusedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ConversationDedicatedSandbox.ModeUnsupportedCode);
    }

    private static async Task AssertReleaseFailedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("sandbox_release_failed");
    }

    private static async Task AssertMoveNotStoredAsync(Fixture fixture, string threadId)
    {
        (await ConversationDedicatedSandbox.ReadAsync(fixture.Store, threadId))
            .Should()
            .BeFalse("the move was not committed");
        (await ConversationWorkingDirectory.ReadAsync(fixture.Store, threadId)).Should().BeNull();
    }

    /// <summary>Mini Apps and browser debugging on. The test host runs as Production, where both are off.</summary>
    private static void EnableMiniAppsAndBrowserDebug(IServiceCollection services)
    {
        services.RemoveAll<BrowserDebugOptions>();
        services.AddSingleton(
            new BrowserDebugOptions
            {
                Enabled = true,
                PreviewOrigin = "https://preview.example.test",
                GatewayAppId = AppId,
                SigningSecret = new string('s', 32),
            }
        );
        services.RemoveAll<SandboxAppCatalog>();
        services.AddSingleton(
            SandboxAppCatalog.Load(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["SandboxApps:Enabled"] = "true",
                            ["SandboxApps:SiteDomain"] = "example.test",
                            ["SandboxApps:AppDomain"] = "apps.example.test",
                        }
                    )
                    .Build()
            )
        );
    }

    private Task<Fixture> StartAsync(
        string? workspaceWorkingDir = null,
        string? root = null,
        string? workspaceId = null,
        TimeProvider? timeProvider = null,
        Action<IServiceCollection>? configureServices = null
    ) => Fixture.StartAsync(Logger, workspaceWorkingDir, root, workspaceId, timeProvider, configureServices);

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

        /// <summary>The hooked mode store; only on a host started with <c>StartHookedAsync</c>.</summary>
        public ModeReadHookStore Modes => (ModeReadHookStore)_host.Services.GetRequiredService<IChatModeStore>();

        /// <summary>The captured host logs; only on a host started with <c>StartHookedAsync</c>.</summary>
        public HostLogCapture Logs =>
            _host.Services.GetServices<Serilog.Core.ILogEventSink>().OfType<HostLogCapture>().Single();

        public static async Task<Fixture> StartAsync(
            ILogger logger,
            string? workspaceWorkingDir,
            string? root = null,
            string? workspaceId = null,
            TimeProvider? timeProvider = null,
            Action<IServiceCollection>? configureServices = null
        )
        {
            var gateway = new OwnedSandboxGateway();
            var fixture = new Fixture(
                gateway,
                new FakeGatewayWebAppFactory(
                    gateway,
                    AppId,
                    root: root,
                    timeProvider: timeProvider,
                    configureServices: configureServices
                ),
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

        /// <summary>A thread in the workspace's shared sandbox, in the Workspace Agent mode.</summary>
        public Task<string> ProvisionSharedWorkspaceThreadAsync() =>
            ProvisionAsync(
                null,
                dedicated: false,
                properties: new Dictionary<string, object>
                {
                    [MultiTurnAgentPool.ModePropertyKey] = SystemChatModes.WorkspaceAgentModeId,
                }
            );

        /// <summary>The mode id the thread stores.</summary>
        public async Task<string?> StoredModeAsync(string threadId) =>
            (await Store.LoadMetadataAsync(threadId))?.Properties?[MultiTurnAgentPool.ModePropertyKey].ToString();

        /// <summary>Stores <paramref name="modeId"/> as the thread's mode, as a mode switch persists it.</summary>
        public Task WriteModeAsync(string threadId, string modeId)
        {
            _logger.LogDebug("Writing mode {ModeId} to {ThreadId}", modeId, threadId);
            return Store.UpdateMetadataAsync(
                threadId,
                existing =>
                    existing! with
                    {
                        Properties = existing.Properties!.SetItem(MultiTurnAgentPool.ModePropertyKey, modeId),
                    }
            );
        }

        /// <summary>Switches the thread's mode over HTTP, as the UI does.</summary>
        public async Task<(HttpStatusCode Status, string Body)> SwitchModeAsync(string threadId, string modeId)
        {
            using var response = await Client.PostAsJsonAsync(
                $"/api/conversations/{threadId}/mode",
                new SwitchModeRequest { ModeId = modeId }
            );
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogDebug(
                "Switch of {ThreadId} to {ModeId} answered {Status}: {Body}",
                threadId,
                modeId,
                (int)response.StatusCode,
                body
            );
            return (response.StatusCode, body);
        }

        /// <summary>A stored mode with the sandbox tools and every tool of <paramref name="toolGroup"/>.</summary>
        public Task<ChatMode> CreateSandboxModeWithAsync(string toolGroup) =>
            _host
                .Services.GetRequiredService<IChatModeStore>()
                .CreateModeAsync(
                    new ChatModeCreateUpdate
                    {
                        Name = $"Sandbox with {toolGroup}",
                        SystemPrompt = "Debug the app.",
                        EnabledCapabilityTools =
                        [
                            ToolGroups.Wildcard(ToolGroups.Sandbox),
                            ToolGroups.Wildcard(toolGroup),
                        ],
                    }
                );

        /// <summary>Builds (or returns) the thread's agent, as a turn does.</summary>
        public IMultiTurnAgent BuildAgent(string threadId) =>
            Pool.GetOrCreateAgent(threadId, WorkspaceMode, "test", null, requestedWorkspaceId: WorkspaceId);

        public Task<HttpResponseMessage> ProvisionOverHttpAsync(
            string? directory,
            bool dedicated,
            string providerId = "test",
            string modeId = SystemChatModes.WorkspaceAgentModeId
        ) =>
            Client.PostAsJsonAsync(
                "/api/conversations",
                new ProvisionConversationRequest
                {
                    WorkspaceId = WorkspaceId,
                    ProviderId = providerId,
                    ModeId = modeId,
                    WorkingDirectoryRelPath = directory,
                    DedicatedSandbox = dedicated,
                }
            );

        /// <summary>Sends a turn over REST, which builds the agent from the thread's stored mode.</summary>
        public async Task<HttpResponseMessage> SendAsync(string threadId)
        {
            var response = await Client.PostAsJsonAsync(
                $"/api/conversations/{threadId}/messages",
                new SendMessageRequest { Text = "debug the app" }
            );
            _logger.LogDebug(
                "Send to {ThreadId} answered {Status}: {Body}",
                threadId,
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync()
            );
            return response;
        }

        /// <summary>Opens <c>/ws</c> as the UI does and reads every text frame until the server closes.</summary>
        public async Task<(
            IReadOnlyList<string> Frames,
            WebSocketCloseStatus? CloseStatus,
            string? CloseDescription
        )> ConnectUntilClosedAsync(string threadId, string modeId)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var socket = await _host
                .Server.CreateWebSocketClient()
                .ConnectAsync(new Uri(_host.Server.BaseAddress, $"/ws?threadId={threadId}&modeId={modeId}"), cts.Token);
            var frames = new List<string>();
            var buffer = new byte[16 * 1024];
            var text = new StringBuilder();
            while (true)
            {
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                _ = text.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                if (received.EndOfMessage)
                {
                    frames.Add(text.ToString());
                    _ = text.Clear();
                }
            }

            _logger.LogDebug(
                "Socket to {ThreadId} got frames {Frames}, then closed {CloseStatus} {CloseDescription}",
                threadId,
                frames,
                socket.CloseStatus,
                socket.CloseStatusDescription
            );
            return (frames, socket.CloseStatus, socket.CloseStatusDescription);
        }

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
