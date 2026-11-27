using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// The shared prepare/status route (LmDotnetTools #1005): an accepted preparation keeps its operation id on every
/// failure, <see cref="SandboxClient.ResumeSourcePreparationAsync"/> waits on a saved id without preparing again, and
/// both public preparation paths map the gateway's codes the same way. Timers run on <see cref="ScriptedClock"/>, so
/// nothing waits in real time.
/// </summary>
public sealed class SandboxClientSourcePreparationPollTests
{
    private const string Path = "/api/v1/apps/self/plugins/prepare";
    private const string OperationId = "2276472b-0b6e-4db9-8c50-0cb8c93b07aa";
    private const string RestoredOperationId = "6a0d1d55-5c3d-4f0e-9a43-7d1a2bb0c001";
    private const string Preparing = $$"""{"status":"preparing","operationId":"{{OperationId}}"}""";

    private const string Ready = """
        {"status":"ready","sourceVersion":"v-123","sourceMode":"exact",
         "sourceReceipt":{"sources":[{"kind":"marketplace","marketplace":"team","plugin":null,
           "url":"https://github.com/acme/plugins.git","repo_key":"plugins-0a1b",
           "sha":"8ca22dba9a94f28898bbce59f2537ff4d87c747d","subdir":null}],"exactRepeatSupported":true},
         "recipe":{"effective":[{"marketplace":"team","plugin":"chosen"}]}}
        """;

    private static readonly TimeSpan S_transportTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "preparation_not_found", SandboxErrorKind.NotFound)]
    [InlineData(HttpStatusCode.Gone, "source_snapshot_expired", SandboxErrorKind.Conflict)]
    [InlineData(HttpStatusCode.Forbidden, "marketplace_access_denied", SandboxErrorKind.Authorization)]
    [InlineData(HttpStatusCode.TooManyRequests, "preparation_capacity", SandboxErrorKind.Unavailable)]
    public async Task PrepareSourcesAsync_AcceptedThenErrorStatus_KeepsOperationIdAndCode(
        HttpStatusCode status,
        string code,
        SandboxErrorKind kind
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            $$"""{"error":"token=SECRET","code":{{(int)status}},"error_code":"{{code}}"}""",
            status
        );

        var thrown = await PrepareAndCatchAsync(client);

        thrown.Kind.Should().Be(kind);
        thrown.StatusCode.Should().Be((int)status);
        thrown.ErrorCode.Should().Be(code);
        thrown.OperationId.Should().Be(OperationId);
        thrown.Message.Should().NotContain("SECRET");
        handler.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Post, HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareSourcesAsync_AcceptedThenUnavailable_KeepsRetryMetadataAndOperationId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.On(
            request => request.Method == HttpMethod.Get,
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"error":"x","code":503,"error_code":"marketplace_broker_unavailable","retryable":true}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return response;
            }
        );

        var thrown = await PrepareAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Unavailable);
        thrown.StatusCode.Should().Be(503);
        thrown.ErrorCode.Should().Be("marketplace_broker_unavailable");
        thrown.Retryable.Should().BeTrue();
        thrown.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
        thrown.OperationId.Should().Be(OperationId);
        handler.Requests.Should().HaveCount(2, "the SDK never retries a preparation on its own");
    }

    [Fact]
    public async Task PrepareSourcesAsync_AcceptedThenNetworkFailure_KeepsOperationId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.On(request => request.Method == HttpMethod.Get, _ => throw new HttpRequestException("refused"));

        var thrown = await PrepareAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        thrown.Message.Should().StartWith("Could not reach the sandbox gateway");
        thrown.OperationId.Should().Be(OperationId);
        thrown.StatusCode.Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"status":"preparing","operationId":"not-a-guid"}""")]
    [InlineData("""{"status":"done"}""")]
    public async Task PrepareSourcesAsync_AcceptedThenMalformedPoll_IsProtocolWithOperationId(string body)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(HttpMethod.Get, $"{Path}/{OperationId}", body);

        var thrown = await PrepareAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be(200);
        thrown.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task PrepareSourcesAsync_FailureAfterARestoredId_CarriesTheRestoredId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            $$"""{"status":"preparing","operationId":"{{RestoredOperationId}}"}"""
        );
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{RestoredOperationId}",
            """{"status":"failed","error_code":"preparation_expired"}"""
        );

        var thrown = await PrepareAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Conflict);
        thrown.ErrorCode.Should().Be("preparation_expired");
        thrown.OperationId.Should().Be(RestoredOperationId);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task PrepareSourcesAsync_PollTransportTimeout_KeepsOperationId()
    {
        var clock = new ScriptedClock();
        var (client, handler) = TestSupport.CreateBorrowedClient(transportTimeout: S_transportTimeout, clock: clock);
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        var polled = HangOnGet(handler);

        var call = PrepareAndCatchAsync(client);
        await polled.Task;
        clock.Fire(S_transportTimeout).Should().Be(1, "only the hanging poll's transport budget is live");
        var thrown = await call;

        thrown.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        thrown.Message.Should().Contain("transport timeout");
        thrown.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task PrepareSourcesAsync_MaxWaitElapses_IsTransportTimeoutWithOperationId()
    {
        var clock = new ScriptedClock();
        var (client, handler) = TestSupport.CreateBorrowedClient(transportTimeout: S_transportTimeout, clock: clock);
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        var polled = HangOnGet(handler);
        var maxWait = TimeSpan.FromMinutes(1);

        var call = Record.ExceptionAsync(() =>
            client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"]) { MaxWait = maxWait })
        );
        await polled.Task;
        clock.Fire(maxWait).Should().Be(1);
        var thrown = (await call).Should().BeOfType<SandboxException>().Subject;

        thrown.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        thrown.Message.Should().Be($"Sandbox gateway did not finish preparing sandbox sources within {maxWait}.");
        thrown.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_StartsWithAnAppBoundGetAndSendsNoPost()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Get, $"{Path}/{OperationId}", Ready);

        var prepared = await client.ResumeSourcePreparationAsync(OperationId);

        prepared.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
        prepared.SourceVersion.Should().Be("v-123");
        prepared.OperationId.Should().Be(OperationId);
        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Get);
        sent.Uri.Host.Should().Be("127.0.0.1", "the configured gateway, never a Location header");
        sent.Uri.PathAndQuery.Should().Be($"{Path}/{OperationId}?app_id=app-1");
        sent.SbxAppId.Should().Be("app-1");
        sent.SbxAppKey.Should().Be(TestSupport.ValidSecret);
        sent.Body.Should().BeNull();
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_PollsUntilReadyAndFollowsARestoredId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            $$"""{"status":"preparing","operationId":"{{RestoredOperationId}}"}"""
        );
        handler.OnJson(HttpMethod.Get, $"{Path}/{RestoredOperationId}", Ready);

        var prepared = await client.ResumeSourcePreparationAsync(OperationId, TimeSpan.FromMinutes(1));

        prepared.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
        prepared.OperationId.Should().Be(RestoredOperationId);
        handler
            .Requests.Select(request => (request.Method, request.Uri.AbsolutePath))
            .Should()
            .Equal((HttpMethod.Get, $"{Path}/{OperationId}"), (HttpMethod.Get, $"{Path}/{RestoredOperationId}"));
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_UnknownOrForeignOperation_IsNotFoundWithOperationId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            """{"error":"x","code":404,"error_code":"preparation_not_found"}""",
            HttpStatusCode.NotFound
        );

        var thrown = await ResumeAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.NotFound);
        thrown.ErrorCode.Should().Be("preparation_not_found");
        thrown.OperationId.Should().Be(OperationId);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_ExpiredPreparation_IsConflictWithOperationId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            """{"status":"failed","error_code":"preparation_expired"}"""
        );

        var thrown = await ResumeAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Conflict);
        thrown.ErrorCode.Should().Be("preparation_expired");
        thrown.StatusCode.Should().Be(200);
        thrown.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_PreCancelled_SendsNothing()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => client.ResumeSourcePreparationAsync(OperationId, ct: cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_CancelledMidPoll_PropagatesCancellation()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        var polled = HangOnGet(handler);
        using var cts = new CancellationTokenSource();

        var call = client.ResumeSourcePreparationAsync(OperationId, ct: cts.Token);
        await polled.Task;
        await cts.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => call);
        thrown.Should().BeAssignableTo<OperationCanceledException>("caller cancellation is never a SandboxException");
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("2276472b0b6e4db98c500cb8c93b07aa")]
    [InlineData("{2276472b-0b6e-4db9-8c50-0cb8c93b07aa}")]
    [InlineData(" 2276472b-0b6e-4db9-8c50-0cb8c93b07aa")]
    [InlineData("2276472b-0b6e-4db9-8c50-0cb8c93b07aa/../x")]
    public async Task ResumeSourcePreparationAsync_InvalidOperationId_ThrowsWithoutSending(string operationId)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());

        var act = () => client.ResumeSourcePreparationAsync(operationId);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("operationId");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_NullOperationId_ThrowsWithoutSending()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());

        var act = () => client.ResumeSourcePreparationAsync(null!);

        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("operationId");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ResumeSourcePreparationAsync_NonPositiveMaxWait_ThrowsWithoutSending(int seconds)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());

        var act = () => client.ResumeSourcePreparationAsync(OperationId, TimeSpan.FromSeconds(seconds));

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("maxWait");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothPreparationPaths_FailedAccessDenied_IsAuthorizationWithCode(bool sourcesPath)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, """{"status":"failed","error_code":"marketplace_access_denied"}""");

        var thrown = await CatchAsync(sourcesPath ? PrepareSources(client) : PrepareSelection(client));

        thrown.Kind.Should().Be(SandboxErrorKind.Authorization);
        thrown.ErrorCode.Should().Be("marketplace_access_denied");
        thrown.StatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothPreparationPaths_ForbiddenAccessDenied_IsAuthorizationWithCode(bool sourcesPath)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(
            HttpMethod.Post,
            Path,
            """{"error":"x","code":403,"error_code":"marketplace_access_denied"}""",
            HttpStatusCode.Forbidden
        );

        var thrown = await CatchAsync(sourcesPath ? PrepareSources(client) : PrepareSelection(client));

        thrown.Kind.Should().Be(SandboxErrorKind.Authorization);
        thrown.ErrorCode.Should().Be("marketplace_access_denied");
        thrown.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task PrepareMarketplacePluginSelectionAsync_AcceptedThenFailed_KeepsOperationIdAndKind()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            """{"status":"failed","error_code":"preparation_expired"}"""
        );

        var thrown = await CatchAsync(PrepareSelection(client));

        thrown.Kind.Should().Be(SandboxErrorKind.Conflict);
        thrown.ErrorCode.Should().Be("preparation_expired");
        thrown.OperationId.Should().Be(OperationId);
    }

    /// <summary>Hangs every GET until its token fires; the returned task completes once a GET has arrived.</summary>
    private static TaskCompletionSource HangOnGet(FakeGatewayHandler handler)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.OnHang(request =>
        {
            if (request.Method != HttpMethod.Get)
            {
                return false;
            }

            arrived.TrySetResult();
            return true;
        });
        return arrived;
    }

    private static Func<Task> PrepareSources(SandboxClient client) =>
        () => client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"]));

    private static Func<Task> PrepareSelection(SandboxClient client) =>
        () => client.PrepareMarketplacePluginSelectionAsync(["team"], [new("team", "chosen")]);

    private static Task<SandboxException> PrepareAndCatchAsync(SandboxClient client) =>
        CatchAsync(PrepareSources(client));

    private static Task<SandboxException> ResumeAndCatchAsync(SandboxClient client) =>
        CatchAsync(() => client.ResumeSourcePreparationAsync(OperationId));

    private static async Task<SandboxException> CatchAsync(Func<Task> call) =>
        (await Record.ExceptionAsync(call)).Should().BeOfType<SandboxException>().Subject;
}
