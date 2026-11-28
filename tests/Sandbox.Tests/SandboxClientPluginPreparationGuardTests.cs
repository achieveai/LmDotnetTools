using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Source preparation and guarded create (LmDotnetTools #1005, gateway Docs/app-marketplaces.md "Source preparation
/// and polling"): prepare → poll → create with <c>expectedSourceVersion</c>, and the actionable failures.
/// </summary>
public sealed class SandboxClientPluginPreparationGuardTests
{
    private const string Path = "/api/v1/apps/self/plugins/prepare";
    private const string OperationId = "2276472b-0b6e-4db9-8c50-0cb8c93b07aa";
    private const string RestoredOperationId = "6a0d1d55-5c3d-4f0e-9a43-7d1a2bb0c001";
    private const string Sha = "8ca22dba9a94f28898bbce59f2537ff4d87c747d";

    private const string Ready = $$$"""
        {"status":"ready","sourceVersion":"v-123","sourceMode":"exact",
         "sourceReceipt":{"sources":[{"kind":"marketplace","marketplace":"team","plugin":null,"origin":"app",
           "url":"https://github.com/acme/plugins.git","repo_key":"plugins-0a1b","sha":"{{{Sha}}}","subdir":null,
           "container_root":"/marketplaces/team"}],"exactRepeatSupported":true},
         "preparation":{"refresh":true,"requireFresh":false}}
        """;

    private const string Preparing = $$"""{"status":"preparing","operationId":"{{OperationId}}"}""";

    private const string CreateResponse = """
        {"session_id":"sess-1","container_id":"container-1","sourceVersion":"v-123",
         "volumes":{"workspace":{"container_path":"/workspace","read_only":false}}}
        """;

    [Fact]
    public async Task PrepareSourcesAsync_ReadyAtOnce_ReturnsVersionReceiptAndSendsSelection()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, Ready);

        var prepared = await client.PrepareSourcesAsync(
            new SandboxSourcePreparationRequest(["team"], [new("team", "chosen")]) { Refresh = true }
        );

        prepared.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
        prepared.SourceVersion.Should().Be("v-123");
        prepared.OperationId.Should().BeNull();
        prepared.ErrorCode.Should().BeNull();
        prepared
            .Sources.Should()
            .Equal(
                new SandboxSourceReceiptEntry(
                    "marketplace",
                    "team",
                    null,
                    "https://github.com/acme/plugins.git",
                    "plugins-0a1b",
                    null,
                    Sha
                )
            );
        prepared.RawSourceReceipt!.Value.GetProperty("exactRepeatSupported").GetBoolean().Should().BeTrue();

        var sent = handler.Requests.Single();
        sent.Uri.PathAndQuery.Should().Be($"{Path}?app_id=app-1");
        sent.SbxAppKey.Should().Be(TestSupport.ValidSecret);
        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("marketplaces", "pluginSelection", "refresh");
        body.RootElement.GetProperty("marketplaces")
            .EnumerateArray()
            .Select(alias => alias.GetString())
            .Should()
            .Equal("team");
        body.RootElement.GetProperty("pluginSelection")
            .EnumerateArray()
            .Select(reference =>
                (reference.GetProperty("marketplace").GetString(), reference.GetProperty("plugin").GetString())
            )
            .Should()
            .Equal(("team", "chosen"));
        body.RootElement.GetProperty("refresh").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PrepareSourcesAsync_RequireFresh_SendsRequireFreshOnThePrepareRequest()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, Ready);

        await client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"]) { RequireFresh = true });

        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        body.RootElement.GetProperty("requireFresh").GetBoolean().Should().BeTrue();
        body.RootElement.TryGetProperty("refresh", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PrepareSourcesAsync_NoStoreUnversionedReply_ReturnsWithoutReceipt()
    {
        // The store-disabled gateway's actual reply (preparation.rs unversioned_reply): capabilities, status,
        // error_code and the input recipe, with no sourceReceipt and no sourceVersion.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            Path,
            """
            {"sourceMode":"unversioned","exactRepeatSupported":false,
             "unversionedSources":[{"marketplace":"ops","plugin":null}],
             "status":"unversioned","error_code":"source_snapshot_unsupported",
             "recipe":{"marketplaces":["ops"],"itemFilters":{},"pluginSelection":null,"pluginRevisions":[],
               "marketplaceRevisions":[]}}
            """
        );

        var prepared = await client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["ops"]));

        prepared.Status.Should().Be(SandboxSourcePreparationStatus.Unversioned);
        prepared.ErrorCode.Should().Be("source_snapshot_unsupported");
        prepared.SourceVersion.Should().BeNull();
        prepared.OperationId.Should().BeNull();
        prepared.Sources.Should().BeNull();
        prepared.RawSourceReceipt.Should().BeNull();
    }

    [Fact]
    public async Task PrepareSourcesAsync_ExplicitEmptyVersusOmitted_IsPreserved()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, Ready);

        await client.PrepareSourcesAsync(new SandboxSourcePreparationRequest([], []) { ExplicitMarketplaces = true });
        await client.PrepareSourcesAsync(new SandboxSourcePreparationRequest());

        using var explicitBody = JsonDocument.Parse(handler.Requests[0].Body!);
        explicitBody.RootElement.GetProperty("marketplaces").GetArrayLength().Should().Be(0);
        explicitBody.RootElement.GetProperty("pluginSelection").GetArrayLength().Should().Be(0);
        handler.Requests[1].Body.Should().Be("{}");
    }

    [Fact]
    public async Task PrepareSourcesAsync_Accepted_PollsOwnGatewayUntilReady()
    {
        var (client, handler, gateway) = TestSupport.CreateBorrowedClientWithBaseAddress(
            new Uri("https://unrelated.example/")
        );
        handler.On(
            request => request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == Path,
            _ => new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Headers = { Location = new Uri("https://evil.example/api/v1/apps/self/plugins/prepare/x") },
                Content = new StringContent(Preparing, Encoding.UTF8, "application/json"),
            }
        );
        handler.OnJson(HttpMethod.Get, $"{Path}/{OperationId}", Preparing.Replace(OperationId, RestoredOperationId));
        handler.OnJson(HttpMethod.Get, $"{Path}/{RestoredOperationId}", Ready);

        var prepared = await client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"]));

        prepared.Status.Should().Be(SandboxSourcePreparationStatus.Ready);
        prepared.SourceVersion.Should().Be("v-123");
        prepared.OperationId.Should().Be(RestoredOperationId);
        handler
            .Requests.Select(request => (request.Method, request.Uri.AbsolutePath))
            .Should()
            .Equal(
                (HttpMethod.Post, Path),
                (HttpMethod.Get, $"{Path}/{OperationId}"),
                (HttpMethod.Get, $"{Path}/{RestoredOperationId}")
            );
        handler
            .Requests.Should()
            .OnlyContain(request =>
                request.Uri.Authority == gateway.Authority
                && request.Uri.Query == "?app_id=app-1"
                && request.SbxAppKey == TestSupport.ValidSecret
            );
    }

    [Fact]
    public async Task PrepareSourcesAsync_Unversioned_ReturnsWithoutVersion()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            Path,
            """
            {"status":"unversioned","error_code":"source_snapshot_unsupported",
             "sourceReceipt":{"sources":[{"kind":"marketplace","marketplace":"ops","origin":"operator","url":null,
               "repo_key":null,"sha":null,"subdir":null,"container_root":"/marketplaces/ops"}]}}
            """
        );

        var prepared = await client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["ops"]));

        prepared.Status.Should().Be(SandboxSourcePreparationStatus.Unversioned);
        prepared.SourceVersion.Should().BeNull();
        prepared.ErrorCode.Should().Be("source_snapshot_unsupported");
        prepared.Sources!.Single().Sha.Should().BeNull();
    }

    [Theory]
    [InlineData("sources_changed", SandboxErrorKind.Conflict)]
    [InlineData("source_snapshot_expired", SandboxErrorKind.Conflict)]
    [InlineData("marketplace_access_denied", SandboxErrorKind.Authorization)]
    [InlineData("marketplace_broker_unavailable", SandboxErrorKind.Unavailable)]
    [InlineData("version_not_found", SandboxErrorKind.Protocol)]
    [InlineData("preparation_expired", SandboxErrorKind.Conflict)]
    [InlineData("preparation_not_found", SandboxErrorKind.NotFound)]
    [InlineData("baseline_not_found", SandboxErrorKind.NotFound)]
    public async Task PrepareSourcesAsync_FailedOnHttp200_ThrowsWithCode(string code, SandboxErrorKind kind)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            $$"""{"status":"failed","error_code":"{{code}}","error":"token=SECRET"}"""
        );

        var thrown = (
            await Record.ExceptionAsync(() => client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"])))
        )
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

        thrown.Kind.Should().Be(kind);
        thrown.ErrorCode.Should().Be(code);
        thrown.OperationId.Should().Be(OperationId);
        thrown.Message.Should().NotContain("SECRET");
        handler.Requests.Should().HaveCount(2, "a failed preparation is never retried");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "preparation_in_progress", SandboxErrorKind.Conflict)]
    [InlineData(HttpStatusCode.Gone, "source_snapshot_expired", SandboxErrorKind.Conflict)]
    [InlineData((HttpStatusCode)429, "preparation_capacity", SandboxErrorKind.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, "marketplace_access_denied", SandboxErrorKind.Authorization)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "marketplace_broker_unavailable", SandboxErrorKind.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, "baseline_not_found", SandboxErrorKind.NotFound)]
    public async Task PrepareSourcesAsync_ErrorStatus_ThrowsWithCode(
        HttpStatusCode status,
        string code,
        SandboxErrorKind kind
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            Path,
            $$"""{"error":"x","code":{{(int)status}},"error_code":"{{code}}"}""",
            status
        );

        var thrown = (
            await Record.ExceptionAsync(() => client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"])))
        )
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

        thrown.Kind.Should().Be(kind);
        thrown.ErrorCode.Should().Be(code);
        thrown.StatusCode.Should().Be((int)status);
        thrown.OperationId.Should().BeNull("the initial POST named no operation");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareSourcesAsync_CodelessFailedPost_ThrowsProtocolWithoutCodeOrOperation()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, """{"status":"failed","error":"token=SECRET"}""");

        var thrown = (
            await Record.ExceptionAsync(() => client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"])))
        )
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.ErrorCode.Should().BeNull();
        thrown.OperationId.Should().BeNull();
        thrown.Message.Should().NotContain("SECRET");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareSourcesAsync_CodelessFailedPoll_ThrowsProtocolWithOperationButNoCode()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(HttpMethod.Get, $"{Path}/{OperationId}", """{"status":"failed"}""");

        var thrown = (
            await Record.ExceptionAsync(() => client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"])))
        )
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.ErrorCode.Should().BeNull();
        thrown.OperationId.Should().Be(OperationId);
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("""{"status":"ready","sourceReceipt":{"sources":[]}}""")]
    [InlineData("""{"status":"ready","sourceVersion":"v-1"}""")]
    [InlineData("""{"status":"ready","sourceVersion":"v-1","sourceReceipt":null}""")]
    [InlineData("""{"status":"unversioned","sourceReceipt":"x"}""")]
    [InlineData("""{"status":"unversioned","sourceVersion":"v-1","sourceReceipt":{"sources":[]}}""")]
    [InlineData("""{"status":"ready","sourceVersion":"v-1","sourceReceipt":{"sources":[{"kind":"marketplace"}]}}""")]
    [InlineData("""{"status":"preparing","operationId":"../other"}""")]
    [InlineData("""{"status":"mystery"}""")]
    public async Task PrepareSourcesAsync_InvalidReply_IsProtocol(string reply)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, reply);

        var act = () => client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"]));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareSourcesAsync_CancelledWhilePolling_PropagatesCancellation()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(HttpMethod.Get, $"{Path}/{OperationId}", Preparing);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var act = () => client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team"]), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task PrepareSourcesAsync_MaxWaitElapsed_IsTransportTimeoutWithOperationId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, Preparing, HttpStatusCode.Accepted);
        handler.OnJson(HttpMethod.Get, $"{Path}/{OperationId}", Preparing);

        var act = () =>
            client.PrepareSourcesAsync(
                new SandboxSourcePreparationRequest(["team"]) { MaxWait = TimeSpan.FromMilliseconds(250) }
            );

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        thrown.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task CreateAsync_ExpectedSourceVersion_SendsGuardAndExplicitEmptyMarketplaces()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponse);

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", marketplaces: [], pluginSelection: [])
            {
                ExplicitMarketplaces = true,
                ExpectedSourceVersion = "v-123",
                RequireFresh = true,
            }
        );

        info.SourceVersion.Should().Be("v-123");
        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        body.RootElement.GetProperty("expectedSourceVersion").GetString().Should().Be("v-123");
        body.RootElement.GetProperty("requireFresh").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("marketplaces").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("pluginSelection").GetArrayLength().Should().Be(0);
        body.RootElement.TryGetProperty("refresh", out _).Should().BeFalse("preparation consumes refresh");
    }

    [Fact]
    public async Task CreateAsync_Unguarded_SendsNoGuardFields()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponse);

        await client.CreateAsync(new SandboxCreateRequest("ws", marketplaces: ["team"]));

        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        body.RootElement.TryGetProperty("expectedSourceVersion", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("requireFresh", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("refresh", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "sources_changed")]
    [InlineData(HttpStatusCode.Conflict, "sources_not_prepared")]
    [InlineData(HttpStatusCode.Conflict, "sources_not_fresh")]
    [InlineData(HttpStatusCode.Gone, "source_snapshot_expired")]
    public async Task CreateAsync_StaleGuard_SurfacesCodeWithoutUnguardedRetry(HttpStatusCode status, string code)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            $$"""{"error":"x","code":{{(int)status}},"error_code":"{{code}}"}""",
            status
        );

        var act = () =>
            client.CreateAsync(new SandboxCreateRequest("ws", ["team"]) { ExpectedSourceVersion = "v-123" });

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Conflict);
        thrown.ErrorCode.Should().Be(code);
        handler.Requests.Should().ContainSingle("a guarded create is never retried unguarded");
    }

    [Fact]
    public async Task CreateAsync_GatewayAcknowledgesOtherVersion_DeletesSandboxAndThrows()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponse.Replace("v-123", "v-999"));
        handler.OnStatus(HttpMethod.Delete, "/api/v1/sandboxes/sess-1", HttpStatusCode.NoContent);

        var act = () =>
            client.CreateAsync(new SandboxCreateRequest("ws", ["team"]) { ExpectedSourceVersion = "v-123" });

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        handler.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Post, HttpMethod.Delete);
    }

    [Fact]
    public async Task CreateAsync_GatewayEchoesNoVersion_DeletesSandboxAndThrows()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            CreateResponse.Replace("\"sourceVersion\":\"v-123\",", "")
        );
        handler.OnStatus(HttpMethod.Delete, "/api/v1/sandboxes/sess-1", HttpStatusCode.NoContent);

        var act = () =>
            client.CreateAsync(new SandboxCreateRequest("ws", ["team"]) { ExpectedSourceVersion = "v-123" });

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.Message.Should().Be("The gateway did not acknowledge the expected source version.");
        handler.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Post, HttpMethod.Delete);
    }

    [Theory]
    [InlineData(""" "sourceReceipt":{"sources":[{"kind":"marketplace"}]} """)]
    [InlineData(""" "sourceReceipt":{"sources":"x"} """)]
    [InlineData(""" "resolvedMarketplaceRevisions":{"team":"abc"} """)]
    [InlineData(""" "resolvedMarketplaceRevisions":[{"marketplace":"team"}] """)]
    [InlineData(""" "resolvedMarketplaceRevisions":[{"marketplace":"\uD800","commit":"abc"}] """)]
    [InlineData(""" "sourceMode":42 """)]
    [InlineData(""" "sourceVersion":42 """)]
    [InlineData(""" "resolvedArchiveReceipts":"x" """)]
    [InlineData(""" "status":42 """)]
    [InlineData(""" "inventory":7 """)]
    [InlineData(""" "inventory":{"status":"confirmed","items":[null]} """)]
    [InlineData(""" "image_report":{} """)]
    [InlineData(""" "skills":{"default_tier":"bogus","included":[]} """)]
    [InlineData(""" "webhook":{"url":"","auth_header_set":true} """)]
    [InlineData(""" "partition":[null] """)]
    [InlineData(""" "pluginResolution":{"supported":true,"effective":[null]} """)]
    public async Task CreateAsync_MalformedSourceEcho_DeletesSandboxAndThrows(string field)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"session_id":"sess-1","container_id":"c","volumes":{"workspace":{"container_path":"/w"}},"""
                + field
                + "}"
        );
        handler.OnStatus(HttpMethod.Delete, "/api/v1/sandboxes/sess-1", HttpStatusCode.NoContent);

        var act = () => client.CreateAsync(new SandboxCreateRequest("ws", ["team"]));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        handler
            .Requests.Select(request => (request.Method, request.Uri.AbsolutePath))
            .Should()
            .Equal((HttpMethod.Post, "/api/v1/sandboxes"), (HttpMethod.Delete, "/api/v1/sandboxes/sess-1"));
    }

    [Fact]
    public async Task CreateAsync_RejectedEchoAndFailedDelete_KeepsSessionIdAndBothCauses()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponse.Replace("v-123", "v-999"));
        handler.OnJson(
            HttpMethod.Delete,
            "/api/v1/sandboxes/sess-1",
            """{"error":"x","code":503,"error_code":"sandbox_busy"}""",
            HttpStatusCode.ServiceUnavailable
        );

        var act = () =>
            client.CreateAsync(new SandboxCreateRequest("ws", ["team"]) { ExpectedSourceVersion = "v-123" });

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be(200);
        thrown.Message.Should().Contain("did not acknowledge the expected source version").And.Contain("'sess-1'");
        var causes = thrown.InnerException.Should().BeOfType<AggregateException>().Subject.InnerExceptions;
        causes.Should().HaveCount(2);
        causes[0]
            .Should()
            .BeOfType<SandboxException>()
            .Which.Message.Should()
            .Be("The gateway did not acknowledge the expected source version.");
        var deleteFailure = causes[1].Should().BeOfType<SandboxException>().Subject;
        deleteFailure.Kind.Should().Be(SandboxErrorKind.Unavailable);
        deleteFailure.ErrorCode.Should().Be("sandbox_busy");
        handler.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Post, HttpMethod.Delete);
    }

    [Theory]
    [InlineData(""" "sourceReceipt":{"sources":[{"kind":"marketplace"}]} """, "source receipt entry")]
    [InlineData(""" "resolvedMarketplaceRevisions":[{"marketplace":"team"}] """, "resolved marketplace revision")]
    [InlineData(""" "sourceMode":42 """, "non-string 'sourceMode'")]
    [InlineData(""" "sourceVersion":42 """, "malformed response")]
    [InlineData(""" "inventory":7 """, "malformed response")]
    [InlineData(""" "inventory":{"status":"confirmed","items":[null]} """, "the inventory of sandbox creation")]
    [InlineData(""" "image_report":{} """, "incomplete image report")]
    public async Task CreateAsync_MalformedSourceEchoAndFailedDelete_KeepsSessionIdAndBothCauses(
        string field,
        string rejection
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", EchoWith(field));
        handler.OnJson(
            HttpMethod.Delete,
            "/api/v1/sandboxes/sess-1",
            """{"error":"x","code":503,"error_code":"sandbox_busy"}""",
            HttpStatusCode.ServiceUnavailable
        );

        var thrown = await CreateAndCatchAsync(client);

        var (echoRejection, deleteFailure) = AssertBothCauses(thrown, handler);
        echoRejection.Message.Should().Contain(rejection);
        deleteFailure.Kind.Should().Be(SandboxErrorKind.Unavailable);
        deleteFailure.ErrorCode.Should().Be("sandbox_busy");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_RejectedEchoAndDeleteConnectionFailure_KeepsSessionIdAndBothCauses(bool afterHeaders)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", EchoWith(""" "sourceMode":42 """));
        handler.On(
            request => request.Method == HttpMethod.Delete,
            _ =>
                afterHeaders
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(
                            new ScriptedBodyStream("{\"deleted\":"u8.ToArray(), chunkSize: 4) { FailAfterBytes = 4 }
                        ),
                    }
                    : throw new IOException("connection reset")
        );

        var thrown = await CreateAndCatchAsync(client);

        var (echoRejection, deleteFailure) = AssertBothCauses(thrown, handler);
        echoRejection.Message.Should().Contain("non-string 'sourceMode'");
        deleteFailure.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        deleteFailure.StatusCode.Should().BeNull();
        deleteFailure.InnerException.Should().BeOfType<IOException>();
    }

    [Theory]
    [InlineData("""{"session_id":42,"container_id":"c"}""", "malformed response")]
    [InlineData("""{"session_id":["sess-1"],"sourceVersion":42}""", "malformed response")]
    [InlineData("""{"container_id":"c","sourceVersion":42}""", "no session id")]
    [InlineData("""{"session_id":" ","sourceVersion":42}""", "no session id")]
    [InlineData("null", "no session id")]
    [InlineData("[]", "malformed response")]
    [InlineData("not json", "malformed response")]
    public async Task CreateAsync_UnreadableSessionId_ThrowsWithoutDeleting(string reply, string message)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", reply);

        var thrown = await CreateAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be(200);
        thrown.Message.Should().Contain(message);
        thrown.InnerException.Should().BeNull();
        handler.Requests.Should().ContainSingle("without a session id there is nothing to delete");
    }

    [Fact]
    public async Task CreateAndGet_NullOrAbsentSourceMode_ReadAsNone()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", EchoWith(""" "sourceMode":null """));
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1", EchoWith(""" "sourceMode":"exact" """));

        var created = await client.CreateAsync(new SandboxCreateRequest("ws", ["team"]));
        var read = await client.GetAsync("sess-1");

        created.SourceMode.Should().BeNull();
        read.SourceMode.Should().Be("exact");
        handler.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Post, HttpMethod.Get);
    }

    private static string EchoWith(string field) =>
        """{"session_id":"sess-1","container_id":"c","volumes":{"workspace":{"container_path":"/w"}},""" + field + "}";

    private static async Task<SandboxException> CreateAndCatchAsync(SandboxClient client) =>
        (await Record.ExceptionAsync(() => client.CreateAsync(new SandboxCreateRequest("ws", ["team"]))))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

    /// <summary>
    /// Asserts the double failure keeps the session id and both causes, in order, after exactly one cleanup DELETE.
    /// </summary>
    private static (SandboxException Rejection, SandboxException DeleteFailure) AssertBothCauses(
        SandboxException thrown,
        FakeGatewayHandler handler
    )
    {
        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.StatusCode.Should().Be(200);
        thrown.Message.Should().Contain("'sess-1'").And.Contain("DeleteAsync");
        var causes = thrown.InnerException.Should().BeOfType<AggregateException>().Subject.InnerExceptions;
        causes.Should().HaveCount(2);
        var rejection = causes[0].Should().BeOfType<SandboxException>().Subject;
        rejection.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.Message.Should().StartWith(rejection.Message);
        handler
            .Requests.Select(request => (request.Method, request.Uri.AbsolutePath))
            .Should()
            .Equal((HttpMethod.Post, "/api/v1/sandboxes"), (HttpMethod.Delete, "/api/v1/sandboxes/sess-1"));
        return (rejection, causes[1].Should().BeOfType<SandboxException>().Subject);
    }

    [Fact]
    public async Task CreateAsync_ExpectedSourceVersionDisagreesWithArchivePreparation_ThrowsWithoutSending()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var archive = new SandboxAppMarketplaceArchive(
            "app-1",
            "team",
            "https://publisher.example/releases/selected",
            new string('a', 64),
            new string('b', 64),
            "selected"
        );
        var request = new SandboxCreateRequest("ws", ["team"], pluginSelection: [new("team", "selected")])
        {
            ArchivePreparation = new SandboxArchivePreparation("v-archive", [new("team", "selected")], [archive]),
            ExpectedSourceVersion = "v-other",
        };

        var act = () => client.CreateAsync(request);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_BlankExpectedSourceVersion_ThrowsWithoutSending()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();

        var act = () => client.CreateAsync(new SandboxCreateRequest("ws", ["team"]) { ExpectedSourceVersion = " " });

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ControlCharacterInExpectedSourceVersion_ThrowsWithoutSending()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();

        var act = () =>
            client.CreateAsync(new SandboxCreateRequest("ws", ["team"]) { ExpectedSourceVersion = "v-123\nx" });

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.ParamName.Should().Be("request");
        thrown.Message.Should().Contain("blank").And.Contain("control characters");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void SandboxSourceReceiptEntry_ToString_RedactsUrlAndKeepsProperty()
    {
        const string sentinel = "https://dev.azure.com/secret-team/p/_git/private-repo.git";
        var entry = new SandboxSourceReceiptEntry("marketplace", "team", null, sentinel, "team-0a1b", null, Sha);

        var text = entry.ToString();

        text.Should().NotContain("secret-team").And.NotContain("private-repo").And.Contain("[REDACTED]");
        text.Should().Contain("team-0a1b").And.Contain(Sha);
        entry.Url.Should().Be(sentinel);
        $"{entry}".Should().Be(text);
    }

    private const string MixedReceipt = $$$"""
        {"sources":[
          {"kind":"marketplace","marketplace":"team","plugin":null,"origin":"app",
           "url":"https://dev.azure.com/acme/p/_git/team.git","repo_key":"team-0a1b","sha":"{{{Sha}}}",
           "subdir":null,"container_root":"/marketplaces/team"},
          {"kind":"local_plugin","marketplace":"ops","plugin":"tool","origin":"operator","url":null,
           "repo_key":null,"sha":null,"subdir":"plugins/tool","container_root":"/marketplaces/ops"}]}
        """;

    [Fact]
    public async Task CreateAndGet_UnversionedMixedSet_ExposeReceiptComparableWithPreparation()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            Path,
            $$$"""{"status":"unversioned","error_code":"source_snapshot_unsupported","sourceReceipt":{{{MixedReceipt}}}"""
                + "}"
        );
        var echo =
            $$$"""
                {"session_id":"sess-1","container_id":"container-1","sourceMode":"unversioned","sourceVersion":null,
                 "sourceReceipt":{{{MixedReceipt}}},
                 "resolvedMarketplaceRevisions":[{"marketplace":"team","commit":"{{{Sha}}}"}],
                 "volumes":{"workspace":{"container_path":"/workspace","read_only":false}}
                """ + "}";
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", echo);
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1", echo);

        var prepared = await client.PrepareSourcesAsync(new SandboxSourcePreparationRequest(["team", "ops"]));
        var created = await client.CreateAsync(new SandboxCreateRequest("ws", ["team", "ops"]));
        var read = await client.GetAsync("sess-1");

        foreach (var info in new[] { created, read })
        {
            info.SourceMode.Should().Be("unversioned");
            info.SourceVersion.Should().BeNull();
            info.SourceReceiptSources.Should().Equal(prepared.Sources);
            info.SourceReceiptSources![1]
                .Should()
                .Be(new SandboxSourceReceiptEntry("local_plugin", "ops", "tool", null, null, "plugins/tool", null));
            info.RawSourceReceipt!.Value.GetProperty("sources").GetArrayLength().Should().Be(2);
            info.ResolvedMarketplaceRevisions.Should().Equal(new SandboxResolvedMarketplaceRevision("team", Sha));
        }
    }

    [Fact]
    public async Task CreateAsync_NoSourceReceipt_LeavesReceiptFieldsEmpty()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"session_id":"sess-1","container_id":"c","volumes":{"workspace":{"container_path":"/workspace"}}}"""
        );

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        info.SourceMode.Should().BeNull();
        info.SourceReceiptSources.Should().BeNull();
        info.RawSourceReceipt.Should().BeNull();
        info.ResolvedMarketplaceRevisions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(""" "sourceReceipt":{"sources":[{"kind":"marketplace"}]} """)]
    [InlineData(""" "sourceReceipt":{"sources":"x"} """)]
    [InlineData(""" "resolvedMarketplaceRevisions":{"team":"abc"} """)]
    [InlineData(""" "resolvedMarketplaceRevisions":[{"marketplace":"team"}] """)]
    [InlineData(""" "sourceMode":42 """)]
    [InlineData(""" "sourceMode":{"mode":"exact"} """)]
    [InlineData(""" "sourceVersion":42 """)]
    public async Task GetAsync_MalformedSourceEcho_IsProtocol(string field)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/sess-1",
            """{"session_id":"sess-1","container_id":"c","volumes":{"workspace":{"container_path":"/w"}},"""
                + field
                + "}"
        );

        var act = () => client.GetAsync("sess-1");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }
}
