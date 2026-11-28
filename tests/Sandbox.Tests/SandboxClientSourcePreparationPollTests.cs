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
        using var outstanding = new CancellationTokenSource();

        var call = PrepareAndCatchAsync(client, outstanding.Token);
        await polled.Task.WithinAsync("the first poll reaching the gateway", outstanding);
        clock.Fire(S_transportTimeout).Should().Be(1, "only the hanging poll's transport budget is live");
        var thrown = await call.WithinAsync("the prepare failing on the poll's transport timeout", outstanding);

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
        using var outstanding = new CancellationTokenSource();

        var call = Record.ExceptionAsync(() =>
            client.PrepareSourcesAsync(
                new SandboxSourcePreparationRequest(["team"]) { MaxWait = maxWait },
                outstanding.Token
            )
        );
        await polled.Task.WithinAsync("the first poll reaching the gateway", outstanding);
        clock.Fire(maxWait).Should().Be(1);
        var thrown = (await call.WithinAsync("the prepare failing at its maxWait", outstanding))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

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
        await polled.Task.WithinAsync("the resume's poll reaching the gateway", cts);
        await cts.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => call).WithinAsync("the cancelled resume ending", cts);
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

    [Fact]
    public async Task PrepareSourcesAsync_AcceptedThenBrokenPollBody_IsTransportFailureWithOperationId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.On(request => request.Method == HttpMethod.Get, _ => BrokenOkBody());

        var thrown = await PrepareAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        thrown.StatusCode.Should().BeNull();
        thrown.OperationId.Should().Be(OperationId);
        thrown.Message.Should().NotContain("SECRET");
        handler.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Post, HttpMethod.Get);
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_BrokenBodyAfterARestoredId_CarriesTheRestoredId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            $$"""{"status":"preparing","operationId":"{{RestoredOperationId}}"}"""
        );
        handler.On(request => request.RequestUri!.AbsolutePath == $"{Path}/{RestoredOperationId}", _ => BrokenOkBody());

        var thrown = await ResumeAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        thrown.OperationId.Should().Be(RestoredOperationId);
        handler.Requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_TwoFailedWaits_KeepTheSavedIdAndNeverPostAgain()
    {
        // Prepare fails on a broken poll body, the first resume on its maxWait, and the second resume finishes: each
        // failure hands back the id the next resume needs, and only the first call ever POSTs.
        var clock = new ScriptedClock();
        var (client, handler) = TestSupport.CreateBorrowedClient(transportTimeout: S_transportTimeout, clock: clock);
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        var gets = 0;
        var secondGet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.OnHang(request =>
        {
            if (request.Method != HttpMethod.Get || ++gets != 2)
            {
                return false;
            }

            secondGet.TrySetResult();
            return true;
        });
        handler.On(
            request => request.Method == HttpMethod.Get,
            _ =>
                gets == 1
                    ? BrokenOkBody()
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(Ready, Encoding.UTF8, "application/json"),
                    }
        );
        var maxWait = TimeSpan.FromMinutes(1);
        using var outstanding = new CancellationTokenSource();

        var first = await PrepareAndCatchAsync(client, outstanding.Token)
            .WithinAsync("the prepare failing on its broken poll body", outstanding);
        var resumed = CatchAsync(() =>
            client.ResumeSourcePreparationAsync(first.OperationId!, maxWait, outstanding.Token)
        );
        await secondGet.Task.WithinAsync("the first resume's GET reaching the gateway", outstanding);
        clock.Fire(maxWait).Should().Be(1, "only the resume's maxWait is due");
        var second = await resumed.WithinAsync("the first resume failing at its maxWait", outstanding);
        var prepared = await client
            .ResumeSourcePreparationAsync(second.OperationId!, maxWait, outstanding.Token)
            .WithinAsync("the second resume finishing", outstanding);

        first.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        first.OperationId.Should().Be(OperationId);
        second.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        second.Message.Should().Contain($"within {maxWait}");
        second.OperationId.Should().Be(OperationId);
        prepared.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
        prepared.OperationId.Should().Be(OperationId);
        handler
            .Requests.Select(request => (request.Method, request.Uri.AbsolutePath))
            .Should()
            .Equal(
                (HttpMethod.Post, Path),
                (HttpMethod.Get, $"{Path}/{OperationId}"),
                (HttpMethod.Get, $"{Path}/{OperationId}"),
                (HttpMethod.Get, $"{Path}/{OperationId}")
            );
    }

    [Theory]
    [InlineData("""{"status":"ready","sourceReceipt":{"recipe":{"effective":null}}}""")]
    [InlineData("""{"status":"ready","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team"}]}}}""")]
    [InlineData(
        """{"status":"ready","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"other"}]}}}"""
    )]
    [InlineData(
        """{"status":"ready","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"chosen"},{"marketplace":"team","plugin":"chosen"}]}}}"""
    )]
    [InlineData("""{"status":"ready","sourceReceipt":"x"}""")]
    public async Task PrepareMarketplacePluginSelectionAsync_InvalidReplyAfterARestoredId_CarriesTheRestoredId(
        string reply
    )
    {
        var (client, handler) = RestoredThen(reply);

        var thrown = await CatchAsync(PrepareSelection(client));

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be(200);
        thrown.OperationId.Should().Be(RestoredOperationId);
        handler.Requests.Should().HaveCount(3);
    }

    private const string ArchiveDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ArchiveContentDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ArchiveRow = $$"""
        {"kind":"archive","app_id":"app-1","alias":"team","url":"https://publisher.example/releases/package",
         "sha256":"{{ArchiveDigest}}","content_sha256":"{{ArchiveContentDigest}}","plugin":"chosen"}
        """;
    private const string ArchiveEffective = """
        "sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"chosen"}]}}
        """;

    [Theory]
    [InlineData($$"""{"status":"ready","sourceVersion":"v-1",{{ArchiveEffective}}}""")]
    [InlineData($$"""{"status":"ready","resolvedArchiveReceipts":[{{ArchiveRow}}],{{ArchiveEffective}}}""")]
    [InlineData($$"""{"status":"unversioned","resolvedArchiveReceipts":[{{ArchiveRow}}],{{ArchiveEffective}}}""")]
    [InlineData($$"""{"status":"ready","sourceVersion":"v-1","resolvedArchiveReceipts":"x",{{ArchiveEffective}}}""")]
    [InlineData(
        $$$$"""{"status":"ready","sourceVersion":"v-1","resolvedArchiveReceipts":[{{{{ArchiveRow}}}}],"sourceReceipt":{"recipe":{"effective":[]}}}"""
    )]
    public async Task PrepareMarketplaceArchiveSelectionAsync_InvalidReplyAfterARestoredId_CarriesTheRestoredId(
        string reply
    )
    {
        var (client, handler) = RestoredThen(reply);
        var archive = new SandboxAppMarketplaceArchive(
            "app-1",
            "team",
            "https://publisher.example/releases/package",
            ArchiveDigest,
            ArchiveContentDigest,
            "chosen"
        );

        var thrown = await CatchAsync(() =>
            client.PrepareMarketplaceArchiveSelectionAsync(["team"], [new("team", "chosen")], [archive])
        );

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.OperationId.Should().Be(RestoredOperationId);
        handler.Requests.Should().HaveCount(3);
    }

    [Theory]
    [InlineData($$"""{"status":42,"operationId":"{{OperationId}}"}""")]
    [InlineData($$"""{"operationId":"{{OperationId}}"}""")]
    [InlineData($$"""{"status":"done","operationId":"{{OperationId}}"}""")]
    public async Task PrepareSourcesAsync_AcceptedReplyWithInvalidStatus_KeepsItsOperationIdForResume(string accepted)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, accepted, HttpStatusCode.Accepted);
        handler.OnJson(HttpMethod.Get, $"{Path}/{OperationId}", Ready);

        var thrown = await PrepareAndCatchAsync(client);
        var resumed = await client.ResumeSourcePreparationAsync(thrown.OperationId!);

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be(202);
        thrown.OperationId.Should().Be(OperationId);
        resumed.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
        handler
            .Requests.Select(request => (request.Method, request.Uri.AbsolutePath))
            .Should()
            .Equal((HttpMethod.Post, Path), (HttpMethod.Get, $"{Path}/{OperationId}"));
    }

    [Theory]
    [InlineData("""{"status":42,"operationId":"not-a-guid"}""", HttpStatusCode.Accepted)]
    [InlineData("""{"status":42,"operationId":42}""", HttpStatusCode.Accepted)]
    [InlineData("""{"status":"done","operationId":"00000000-0000-0000-0000-000000000000"}""", HttpStatusCode.Accepted)]
    [InlineData($$"""{"operationId":" {{OperationId}}"}""", HttpStatusCode.Accepted)]
    [InlineData($$"""{"status":"done","operationId":"{{{OperationId}}}"}""", HttpStatusCode.Accepted)]
    [InlineData($$"""[{"operationId":"{{OperationId}}"}]""", HttpStatusCode.Accepted)]
    [InlineData(Preparing, HttpStatusCode.OK)]
    public async Task PrepareSourcesAsync_InvalidInitialReplyWithoutAValidAcceptedId_HasNoHandle(
        string reply,
        HttpStatusCode status
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, reply, status);

        var thrown = await PrepareAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be((int)status);
        thrown.OperationId.Should().BeNull("only a valid id on an accepted reply becomes a handle");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareSourcesAsync_AcceptedThenUnknownCharset_IsProtocolWithOperationId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.On(request => request.Method == HttpMethod.Get, _ => BogusCharset(Ready));

        var thrown = await PrepareAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be(200);
        thrown.OperationId.Should().Be(OperationId);
        handler.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Post, HttpMethod.Get);
    }

    [Fact]
    public async Task ResumeSourcePreparationAsync_UnknownCharset_IsProtocolWithTheSavedId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.On(request => request.Method == HttpMethod.Get, _ => BogusCharset(Ready));

        var thrown = await ResumeAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be(200);
        thrown.OperationId.Should().Be(OperationId);
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    /// <summary>A 200 whose Content-Type names a charset no runtime knows.</summary>
    private static HttpResponseMessage BogusCharset(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=x-bogus");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>A POST accepted as <see cref="OperationId"/>, restored as <see cref="RestoredOperationId"/>, then <paramref name="reply"/>.</summary>
    private static (SandboxClient Client, FakeGatewayHandler Handler) RestoredThen(string reply)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(clock: new ScriptedClock());
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            $$"""{"status":"preparing","operationId":"{{RestoredOperationId}}"}"""
        );
        handler.OnJson(HttpMethod.Get, $"{Path}/{RestoredOperationId}", reply);
        return (client, handler);
    }

    /// <summary>A 200 whose body breaks after its first bytes, as a reset connection does mid-download.</summary>
    private static HttpResponseMessage BrokenOkBody() =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(
                new ScriptedBodyStream("{\"status\":\"ready\""u8.ToArray(), chunkSize: 8) { FailAfterBytes = 8 }
            ),
        };

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

    private static Func<Task> PrepareSources(SandboxClient client, CancellationToken ct = default) =>
        () => client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"]), ct);

    private static Func<Task> PrepareSelection(SandboxClient client) =>
        () => client.PrepareMarketplacePluginSelectionAsync(["team"], [new("team", "chosen")]);

    private static Task<SandboxException> PrepareAndCatchAsync(SandboxClient client, CancellationToken ct = default) =>
        CatchAsync(PrepareSources(client, ct));

    private static Task<SandboxException> ResumeAndCatchAsync(SandboxClient client) =>
        CatchAsync(() => client.ResumeSourcePreparationAsync(OperationId));

    private static async Task<SandboxException> CatchAsync(Func<Task> call) =>
        (await Record.ExceptionAsync(call)).Should().BeOfType<SandboxException>().Subject;
}
