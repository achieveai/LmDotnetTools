using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The parent-only typed publication surface (spec §6): exactly six operations, forwarded to the
/// authenticated daemon backend in the shape its <c>ReviewPublicationController</c> binds, with typed
/// receipts and typed rejections and no credential or raw provider body ever reaching the model.
/// </summary>
/// <remarks>
/// The wire shape asserted here is the daemon's, read from
/// <c>CodeReviewDaemon.Sample/Controllers/ReviewPublicationController.cs</c> and
/// <c>Orchestration/IReviewPublicationOperations.cs</c>: a <c>long</c> route round id, an
/// <c>X-Review-Bridge-Auth</c> raw-secret header, and a camelCase body of <c>{ scope, ...operation
/// fields }</c> bound with <see cref="JsonSerializerDefaults.Web"/>.
/// </remarks>
public sealed class ReviewPublicationFunctionProviderTests
{
    private const string Secret = "bridge-secret-value";

    /// <summary>
    /// A minimal valid citation set. The daemon's <c>ReviewStore.ValidateSourceReferences</c> rejects an
    /// empty one outright, so every well-formed call carries at least one entry.
    /// </summary>
    private static readonly object[] Sources = [new { sourceRecordId = "source-1", contentSha256 = "beef" }];

    private static readonly ReviewPublicationScope Scope = ReviewPublicationScope.TryCreate(
        new ReviewPublicationScopeRequest
        {
            RoundId = 7,
            Provider = "github",
            RepoId = 1234,
            PrId = "118",
            ExpectedHeadSha = "abc123",
            LivePostingAuthorized = true,
        }
    )!;

    /// <summary>
    /// The same round with the live-write gate CLOSED. This is the state a model has an incentive to
    /// flip, so the scope-escalation tests run against it: if the host's value survives, the model's did
    /// not win.
    /// </summary>
    private static readonly ReviewPublicationScope CollectOnlyScope = ReviewPublicationScope.TryCreate(
        new ReviewPublicationScopeRequest
        {
            RoundId = 7,
            Provider = "github",
            RepoId = 1234,
            PrId = "118",
            ExpectedHeadSha = "abc123",
            LivePostingAuthorized = false,
        }
    )!;

    // --- The surface -------------------------------------------------------------------------

    [Fact]
    public void Provider_ExposesExactlyTheSixSpecifiedOperations()
    {
        var provider = BuildProvider(_ => Json(HttpStatusCode.OK, "{}"));

        provider
            .GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .BeEquivalentTo([
                "CreateRootSummary",
                "AppendSummaryDelta",
                "SubmitInlineFindings",
                "PostClarificationQuestion",
                "ReplyToDiscussion",
                "FinalizeRound",
            ]);
    }

    /// <summary>
    /// The names the host excludes from inheritance MUST be the names it registers. Declaring the
    /// roster separately from the descriptors is how one of the two ends up stale after an edit.
    /// </summary>
    [Fact]
    public void ToolNames_MatchTheRegisteredDescriptors()
    {
        var provider = BuildProvider(_ => Json(HttpStatusCode.OK, "{}"));

        ReviewPublicationFunctionProvider
            .ToolNames.Should()
            .BeEquivalentTo(provider.GetFunctions().Select(f => f.Contract.Name));
    }

    [Theory]
    [InlineData("CreateRootSummary")]
    [InlineData("AppendSummaryDelta")]
    [InlineData("SubmitInlineFindings")]
    [InlineData("PostClarificationQuestion")]
    [InlineData("ReplyToDiscussion")]
    [InlineData("FinalizeRound")]
    public async Task EachOperation_PostsToItsOwnRouteSegment_UnderTheScopedRound(string operation)
    {
        Uri? seen = null;
        var provider = BuildProvider(request =>
        {
            seen = request.RequestUri;
            return Json(HttpStatusCode.OK, Receipt());
        });

        _ = await InvokeAsync(provider, operation, MinimalArgumentsFor(operation));

        seen!.AbsolutePath.Should().Be($"/api/review-publication/rounds/7/actions/{operation}");
    }

    /// <summary>
    /// The daemon rejects a body whose <c>scope.roundId</c> disagrees with the route as
    /// <c>round_id_mismatch</c>. Both come from the host's single scope, so they cannot disagree — this
    /// pins that they are in fact written from the same source.
    /// </summary>
    [Fact]
    public async Task TheRouteRoundAndTheScopeRound_AreTheSameValue()
    {
        var (route, payload) = await CaptureAsync(
            "CreateRootSummary",
            new
            {
                actionId = "A-1",
                body = "text",
                sources = Sources,
            }
        );

        route.AbsolutePath.Should().Be("/api/review-publication/rounds/7/actions/CreateRootSummary");
        payload.GetProperty("scope").GetProperty("roundId").GetInt64().Should().Be(7);
    }

    // --- Forwarding fidelity -----------------------------------------------------------------

    /// <summary>
    /// Agent-authored prose is the one thing the host must not touch. A body carrying quotes,
    /// backslashes, newlines, an HTML action marker and non-ASCII has to arrive at the backend
    /// character-for-character, as a SIBLING of scope (where <c>RootSummaryRequest.Body</c> binds).
    /// </summary>
    [Fact]
    public async Task Body_ReachesTheBackend_ByteForByte_AsASiblingOfScope()
    {
        const string Body =
            "Line one\n\"quoted\" \\backslash\\ <!-- review-action:F-001:POST -->\nunicode: café — ✅\ttab";

        var (_, payload) = await CaptureAsync(
            "CreateRootSummary",
            new
            {
                actionId = "A-1",
                body = Body,
                sources = Sources,
            }
        );

        payload.GetProperty("body").GetString().Should().Be(Body);
    }

    /// <summary>
    /// Findings bind to <c>InlineFindingsRequest.Findings</c>, so they must arrive as a top-level array
    /// with the daemon's own member names — not folded into scope and not renamed.
    /// </summary>
    [Fact]
    public async Task Findings_ArriveAsTheDaemonsOwnRecordShape()
    {
        var (_, payload) = await CaptureAsync(
            "SubmitInlineFindings",
            new
            {
                actionId = "A-9",
                findings = new[]
                {
                    new
                    {
                        path = "src/A.cs",
                        side = "RIGHT",
                        startLine = 10,
                        endLine = 12,
                        body = "finding",
                    },
                },
                sources = Sources,
            }
        );

        var finding = payload.GetProperty("findings").EnumerateArray().Single();
        finding.GetProperty("path").GetString().Should().Be("src/A.cs");
        finding.GetProperty("side").GetString().Should().Be("RIGHT");
        finding.GetProperty("startLine").GetInt32().Should().Be(10);
        finding.GetProperty("endLine").GetInt32().Should().Be(12);
        finding.GetProperty("body").GetString().Should().Be("finding");
    }

    /// <summary>
    /// The two per-action fields the agent owns are lifted INTO scope, because that is where the
    /// daemon's <c>PublicationScope</c> carries them — an actionId left as a sibling would arrive as an
    /// empty idempotency key and defeat replay.
    /// </summary>
    [Fact]
    public async Task ActionIdAndSources_AreLiftedIntoTheScopeObject()
    {
        var (_, payload) = await CaptureAsync(
            "CreateRootSummary",
            new
            {
                actionId = "A-1",
                body = "text",
                sources = new[] { new { sourceRecordId = "source-1", contentSha256 = "beef" } },
            }
        );

        var scope = payload.GetProperty("scope");
        scope.GetProperty("actionId").GetString().Should().Be("A-1");
        var source = scope.GetProperty("sources").EnumerateArray().Single();
        source.GetProperty("sourceRecordId").GetString().Should().Be("source-1");
        source.GetProperty("contentSha256").GetString().Should().Be("beef");

        payload.TryGetProperty("actionId", out _).Should().BeFalse("it belongs to scope, not beside it");
        payload.TryGetProperty("sources", out _).Should().BeFalse("it belongs to scope, not beside it");
    }

    /// <summary>
    /// The daemon rejects a citation-free action as <c>invalid_source_reference</c> — an opaque backend
    /// code for what is really a malformed call. Refusing it here turns that into a message the agent
    /// can act on, and does so WITHOUT consuming the action id, so the retry is still idempotent.
    /// </summary>
    [Theory]
    [InlineData("CreateRootSummary")]
    [InlineData("AppendSummaryDelta")]
    [InlineData("SubmitInlineFindings")]
    [InlineData("PostClarificationQuestion")]
    [InlineData("ReplyToDiscussion")]
    [InlineData("FinalizeRound")]
    public async Task EveryOperation_RefusesAnActionWithNoCitations(string operation)
    {
        var (result, called) = await InvokeExpectingNoCallAsync(
            operation,
            StripSources(MinimalArgumentsFor(operation))
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
        called.Should().BeFalse();
    }

    /// <summary>
    /// An EMPTY array is the same answer as an absent key — nothing was cited — and the daemon refuses
    /// it for the same reason. A shape-only check that accepted <c>[]</c> would pass the call straight
    /// through to that opaque rejection.
    /// </summary>
    [Fact]
    public async Task AnEmptyCitationSet_IsRefusedLikeAnAbsentOne()
    {
        var (result, called) = await InvokeExpectingNoCallAsync(
            "CreateRootSummary",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Array.Empty<object>(),
                }
            )
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
        called.Should().BeFalse();
    }

    /// <summary>
    /// <c>PublicationScope.Sources</c> is a non-nullable list on the daemon side, so a missing key would
    /// hand its binder a null to dereference. The provider refuses such a call before it can get here,
    /// so this pins the bridge's own defence directly: the key is always present, always an array.
    /// </summary>
    [Fact]
    public async Task TheBridgeNeverOmitsTheSourcesKey_EvenWhenCalledDirectly()
    {
        JsonElement payload = default;
        var httpClient = new HttpClient(
            new FakeHttpMessageHandler(
                (request, _) =>
                {
                    payload = JsonDocument
                        .Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                        .RootElement.Clone();
                    return Task.FromResult(Json(HttpStatusCode.OK, Receipt()));
                }
            )
        );
        var bridge = new ReviewPublicationBridgeClient(httpClient, new Uri("https://daemon.test"), Scope, Secret);

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { actionId = "A-1", noOp = true }));
        _ = await bridge.SendAsync("FinalizeRound", arguments.RootElement, CancellationToken.None);

        var sources = payload.GetProperty("scope").GetProperty("sources");
        sources.ValueKind.Should().Be(JsonValueKind.Array);
        sources.GetArrayLength().Should().Be(0);
    }

    // --- Scope is host-owned -------------------------------------------------------------------

    /// <summary>
    /// The sharpest escalation available to a model: supply your own <c>scope</c>. It could re-aim the
    /// daemon's credentials at another repository AND flip <c>livePostingAuthorized</c>, turning a
    /// collect-only round into a live write to the pull request. The model's object must be DROPPED,
    /// leaving exactly one scope — the host's.
    /// </summary>
    [Fact]
    public async Task AModelSuppliedScope_IsDropped_AndCannotAuthorizeLivePosting()
    {
        string? raw = null;
        JsonElement payload = default;
        var provider = BuildProvider(
            request =>
            {
                raw = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                payload = JsonDocument.Parse(raw).RootElement.Clone();
                return Json(HttpStatusCode.OK, Receipt());
            },
            CollectOnlyScope
        );

        _ = await InvokeAsync(
            provider,
            "CreateRootSummary",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Sources,
                    scope = new
                    {
                        roundId = 999,
                        actionId = "A-1",
                        provider = "ado",
                        repoId = 9999,
                        prId = "1",
                        expectedHeadSha = "deadbeef",
                        sources = Array.Empty<object>(),
                        livePostingAuthorized = true,
                    },
                }
            )
        );

        var scope = payload.GetProperty("scope");
        scope.GetProperty("roundId").GetInt64().Should().Be(7);
        scope.GetProperty("provider").GetString().Should().Be("github");
        scope.GetProperty("repoId").GetInt64().Should().Be(1234);
        scope.GetProperty("prId").GetString().Should().Be("118");
        scope.GetProperty("expectedHeadSha").GetString().Should().Be("abc123");
        scope
            .GetProperty("livePostingAuthorized")
            .GetBoolean()
            .Should()
            .BeFalse("the collect-only gate is the host's decision, never the model's");

        // Exactly one scope member: a duplicate would leave "which one wins" to the backend's parser.
        System
            .Text.RegularExpressions.Regex.Matches(raw!, "\"scope\"")
            .Count.Should()
            .Be(1, "a second scope member would make the security answer a JSON-parser detail");
    }

    /// <summary>
    /// The same escalation with one letter changed. The daemon's controller binds with
    /// <see cref="JsonSerializerDefaults.Web"/>, which matches member names case-INSENSITIVELY, and the
    /// host writes its own scope FIRST — so a model-supplied <c>Scope</c> that survived as a sibling
    /// would be the LATER member, and the one the binder honours. Dropping the model's object therefore
    /// has to be a case-insensitive decision; an exact-name match leaves the whole guard bypassable by
    /// pressing shift.
    /// </summary>
    [Theory]
    [InlineData("scope")]
    [InlineData("Scope")]
    [InlineData("SCOPE")]
    [InlineData("sCoPe")]
    public async Task AModelSuppliedScope_InAnyCasing_CannotOverrideTheHostScope(string name)
    {
        var (_, payload) = await CaptureAsync(
            "CreateRootSummary",
            WithExtraMember(
                name,
                new
                {
                    roundId = 999,
                    provider = "ado",
                    repoId = 9999,
                    prId = "1",
                    expectedHeadSha = "deadbeef",
                    livePostingAuthorized = true,
                }
            ),
            CollectOnlyScope
        );

        payload
            .EnumerateObject()
            .Count(p => string.Equals(p.Name, "scope", StringComparison.OrdinalIgnoreCase))
            .Should()
            .Be(1, "a second scope member in ANY casing makes the security answer a parser detail");

        var scope = payload.GetProperty("scope");
        scope.GetProperty("roundId").GetInt64().Should().Be(7);
        scope.GetProperty("repoId").GetInt64().Should().Be(1234);
        scope
            .GetProperty("livePostingAuthorized")
            .GetBoolean()
            .Should()
            .BeFalse("the collect-only gate survives a re-cased scope exactly as it survives a lowercase one");
    }

    /// <summary>
    /// <c>actionId</c> and <c>sources</c> are host-consumed too — the bridge lifts them INTO the scope
    /// object. A re-cased copy must not also travel as a sibling, because on a case-insensitive binder
    /// that sibling is a second value for a field the host has already decided.
    /// </summary>
    [Theory]
    [InlineData("ActionId")]
    [InlineData("ACTIONID")]
    [InlineData("Sources")]
    [InlineData("SOURCES")]
    public async Task AModelSuppliedHostConsumedField_InAnotherCasing_IsNotForwardedAsASibling(string name)
    {
        var (_, payload) = await CaptureAsync("CreateRootSummary", WithExtraMember(name, "attacker-supplied"));

        payload
            .EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .NotContain(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        var scope = payload.GetProperty("scope");
        scope.GetProperty("actionId").GetString().Should().Be("A-1");
        scope.GetProperty("sources").GetArrayLength().Should().Be(1);
    }

    // --- Credential isolation ----------------------------------------------------------------

    [Fact]
    public async Task BridgeSecret_TravelsInTheHeaderOnly_AndNeverInThePayloadOrTheResult()
    {
        string? payload = null;
        string? presented = null;
        var provider = BuildProvider(request =>
        {
            presented = request.Headers.TryGetValues("X-Review-Bridge-Auth", out var values)
                ? string.Join(",", values)
                : null;
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, Receipt());
        });

        var result = await InvokeAsync(
            provider,
            "CreateRootSummary",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Sources,
                }
            )
        );

        presented.Should().Be(Secret, "the daemon's ReviewBridgeAuthAttribute reads the raw secret, not a scheme");
        payload.Should().NotContain(Secret);
        result.ResultText.Should().NotContain(Secret);
    }

    /// <summary>
    /// <see cref="HttpClient"/> merges its DEFAULT headers into every request it sends. The publication
    /// client is process-wide, so a default credential set on it by unrelated wiring would be forwarded
    /// to the daemon on every publication — a credential the daemon never asked for and the review
    /// conversation has no business presenting. The bridge refuses to exist on such a client rather than
    /// forwarding quietly; only the host-configured bridge secret may travel on these requests.
    /// </summary>
    [Theory]
    [InlineData("Authorization", "Bearer someone-elses-token")]
    [InlineData("Proxy-Authorization", "Basic dXNlcjpwdw==")]
    [InlineData("Cookie", "session=abc")]
    public void AClientCarryingSomeoneElsesCredential_IsRefused(string header, string value)
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header, value);

        FluentActions
            .Invoking(() => ReviewPublicationBridgeClient.TryCreate(httpClient, Scope, "https://daemon.test", Secret))
            .Should()
            .Throw<ArgumentException>();
    }

    /// <summary>
    /// .NET's auto-redirect clears only <c>Authorization</c>; every other header — including a custom
    /// one like <c>X-Review-Bridge-Auth</c> — is re-sent to the redirect target
    /// (<c>HttpClientHandler.AllowAutoRedirect</c> documents "No other headers are cleared"). An answer
    /// that came back from a DIFFERENT origin therefore means the bridge secret has already left the
    /// configured host. The host cannot un-send it, but it must not compound the leak by treating a
    /// stranger's body as the daemon's decision about the action.
    /// </summary>
    [Fact]
    public async Task AnAnswerFromAnotherOrigin_IsRefused_BecauseTheSecretTravelledToIt()
    {
        var provider = BuildProvider(_ =>
        {
            var response = Json(HttpStatusCode.OK, Receipt());
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Post,
                "https://evil.test/api/review-publication/rounds/7/actions/CreateRootSummary"
            );
            return response;
        });

        var result = await InvokeAsync(provider, "CreateRootSummary", MinimalArgumentsFor("CreateRootSummary"));

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(ReviewPublicationBridgeClient.BackendUnavailableCode);
    }

    // --- Receipts ----------------------------------------------------------------------------

    [Fact]
    public async Task AcceptedAction_ReturnsTheCompleteTypedReceipt()
    {
        var provider = BuildProvider(_ => Json(HttpStatusCode.OK, Receipt()));

        var result = await InvokeAsync(
            provider,
            "SubmitInlineFindings",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-9",
                    findings = new[]
                    {
                        new
                        {
                            path = "src/A.cs",
                            side = "RIGHT",
                            endLine = 12,
                            body = "finding",
                        },
                    },
                    sources = Sources,
                }
            )
        );

        result.Payload.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.ResultText);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Accepted");
        doc.RootElement.GetProperty("actionId").GetString().Should().Be("A-9");
        doc.RootElement.GetProperty("providerReviewId").GetString().Should().Be("rev-1");
        doc.RootElement.GetProperty("providerThreadId").GetString().Should().Be("thr-2");
        doc.RootElement.GetProperty("providerCommentId").GetString().Should().Be("cmt-3");
        doc.RootElement.GetProperty("providerPermalink").GetString().Should().Be("https://example.test/c/3");
        doc.RootElement.GetProperty("relationshipDegraded").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("acceptedAt").GetString().Should().Be("2026-09-02T10:00:00+00:00");
    }

    /// <summary>
    /// A <c>CollectedOnly</c> outcome is a RECEIPT, not a failure: the daemon accepted the action and
    /// recorded it without posting because the round is not live-authorized. Surfacing it as an error
    /// would push the agent into retrying an action that already succeeded.
    /// </summary>
    [Fact]
    public async Task CollectedOnlyOutcome_IsAReceipt_NotAnError()
    {
        const string CollectedOnly = """
            {"status":"CollectedOnly","actionId":"A-1","relationshipDegraded":false,"rejectionCode":null}
            """;
        var provider = BuildProvider(_ => Json(HttpStatusCode.OK, CollectedOnly));

        var result = await InvokeAsync(
            provider,
            "CreateRootSummary",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Sources,
                }
            )
        );

        result.Payload.IsError.Should().BeFalse();
        result.ResultText.Should().Contain("CollectedOnly");
    }

    // --- Rejections ---------------------------------------------------------------------------

    /// <summary>
    /// A typed rejection reaches the agent with its mechanical code and its typed members. Members
    /// OUTSIDE the daemon's <c>PublicationOutcome</c> contract — <c>detail</c> here — do not: a rejection
    /// body is the one place a backend is most likely to attach free prose about why something failed,
    /// and free prose from the credential-holding side is exactly what must not land in the review
    /// transcript. The mechanical reason survives; the narration does not.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Conflict, "action_id_conflict")]
    [InlineData(HttpStatusCode.Conflict, "round_id_mismatch")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_source_reference")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "root_summary_already_exists")]
    public async Task TypedRejection_IsSurfacedWithItsCode_AndOnlyItsTypedFields(HttpStatusCode status, string code)
    {
        var rejection = $$"""
            {"status":"Rejected","actionId":"A-1","rejectionCode":"{{code}}","detail":"backend detail"}
            """;
        var provider = BuildProvider(_ => Json(status, rejection));

        var result = await InvokeAsync(
            provider,
            "CreateRootSummary",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Sources,
                }
            )
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(code);

        using var document = JsonDocument.Parse(result.ResultText);
        document.RootElement.GetProperty("status").GetString().Should().Be("Rejected");
        document.RootElement.GetProperty("actionId").GetString().Should().Be("A-1");
        document.RootElement.GetProperty("rejectionCode").GetString().Should().Be(code);
        document.RootElement.TryGetProperty("detail", out _).Should().BeFalse("detail is not a typed outcome field");
    }

    /// <summary>
    /// A 409/422 that carries no rejection code is not a rejection the agent can act on — it is a
    /// non-answer, and it must not be reported under an improvised code.
    /// </summary>
    [Fact]
    public async Task ARejectionStatusWithoutACode_IsReducedToTheUnavailableCode()
    {
        var provider = BuildProvider(_ => Json(HttpStatusCode.Conflict, """{"status":"Rejected"}"""));

        var result = await InvokeAsync(provider, "CreateRootSummary", MinimalArgumentsFor("CreateRootSummary"));

        result.Payload.ErrorCode.Should().Be(ReviewPublicationBridgeClient.BackendUnavailableCode);
    }

    // --- Only typed outcome fields reach the model ---------------------------------------------

    /// <summary>
    /// A 200 is not a receipt just because it is a 200. A proxy, gateway or load balancer that answers
    /// with its own page — carrying a token, an internal address, a stack frame — must not be read as an
    /// accepted action, and none of its text may reach the review transcript. JSON that is not an object
    /// is the same non-answer for the same reason.
    /// </summary>
    [Theory]
    [InlineData("<html><body>502 upstream token ghp_SECRETVALUE at 10.0.0.5:9443</body></html>")]
    [InlineData("""[{"status":"Accepted","actionId":"A-1"}]""")]
    [InlineData("\"Accepted\"")]
    [InlineData("")]
    public async Task A200CarryingAnUntypedBody_IsNotReadAsAReceipt(string body)
    {
        var provider = BuildProvider(_ => Json(HttpStatusCode.OK, body));

        var result = await InvokeAsync(provider, "CreateRootSummary", MinimalArgumentsFor("CreateRootSummary"));

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(ReviewPublicationBridgeClient.BackendUnavailableCode);
        result.ResultText.Should().NotContain("ghp_SECRETVALUE").And.NotContain("10.0.0.5");
    }

    /// <summary>
    /// Only the daemon's own <c>PublicationOutcome</c> members reach the model. An answer that also
    /// carries diagnostics — an internal endpoint, a stack frame, the bridge secret echoed back — is
    /// projected down to the known fields, so a member nobody designed cannot become a channel from the
    /// credential-holding backend into the review transcript.
    /// </summary>
    [Fact]
    public async Task MembersOutsideTheTypedOutcome_AreProjectedAway()
    {
        var chatty = $$"""
            {"status":"Accepted","actionId":"A-1","providerPermalink":"https://example.test/c/3",
             "detail":"backend narration","internalEndpoint":"https://daemon.internal:9443",
             "stackTrace":"at Provider.Post()","echoed":"{{Secret}}"}
            """;
        var provider = BuildProvider(_ => Json(HttpStatusCode.OK, chatty));

        var result = await InvokeAsync(provider, "CreateRootSummary", MinimalArgumentsFor("CreateRootSummary"));

        result.Payload.IsError.Should().BeFalse();

        using var document = JsonDocument.Parse(result.ResultText);
        document.RootElement.GetProperty("status").GetString().Should().Be("Accepted");
        document.RootElement.GetProperty("providerPermalink").GetString().Should().Be("https://example.test/c/3");
        foreach (var dropped in new[] { "detail", "internalEndpoint", "stackTrace", "echoed" })
        {
            document.RootElement.TryGetProperty(dropped, out _).Should().BeFalse($"{dropped} is not a typed field");
        }

        result.ResultText.Should().NotContain("daemon.internal").And.NotContain(Secret);
    }

    // --- Transport bounds ----------------------------------------------------------------------

    /// <summary>A deployment that configures nothing still gets the spec's per-action budget.</summary>
    [Fact]
    public void AConfiguredBridge_CarriesTheThirtySecondPerActionBudget()
    {
        ReviewPublicationBridgeClient
            .TryCreate(new HttpClient(), Scope, "https://daemon.test", Secret)!
            .RequestTimeout.Should()
            .Be(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// A backend — or a proxy in front of it — answering with an unbounded body must not be read into
    /// the host's memory. The stream reports what was actually pulled from it, which pins BOTH halves of
    /// the guard: the 128 KiB ceiling, and that the send completes on HEADERS.
    /// <see cref="HttpCompletionOption.ResponseContentRead"/> buffers the whole body inside
    /// <c>SendAsync</c>, so under it the stream would be drained in full before any ceiling could apply.
    /// </summary>
    [Fact]
    public async Task AnUnboundedResponse_IsRefused_AfterPullingAtMostTheCap()
    {
        var body = new CountingStream(300 * 1024);
        var provider = BuildProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });

        var result = await InvokeAsync(provider, "CreateRootSummary", MinimalArgumentsFor("CreateRootSummary"));

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(ReviewPublicationBridgeClient.BackendUnavailableCode);
        body.BytesRead.Should()
            .BeGreaterThan(0, "the body was streamed, so the ceiling is a read bound and not a skipped read")
            .And.BeLessThanOrEqualTo(ReviewPublicationBridgeClient.MaxResponseBytes + 1);
    }

    /// <summary>The boundary from the other side: an answer that fits is still read and parsed.</summary>
    [Fact]
    public async Task AResponseExactlyAtTheCap_IsStillReadAsAReceipt()
    {
        var provider = BuildProvider(_ =>
            Json(HttpStatusCode.OK, ReceiptOfExactly(ReviewPublicationBridgeClient.MaxResponseBytes))
        );

        var result = await InvokeAsync(provider, "CreateRootSummary", MinimalArgumentsFor("CreateRootSummary"));

        result.Payload.IsError.Should().BeFalse();
    }

    /// <summary>
    /// The request side of the same bound, refused BEFORE the send — so the action id is never consumed
    /// and the agent can retry the shortened action under the same id, which is what keeps the retry
    /// idempotent (spec §6.5).
    /// </summary>
    [Fact]
    public async Task AnOversizedRequest_IsRefusedWithoutReachingTheBackend()
    {
        var (result, called) = await InvokeExpectingNoCallAsync(
            "CreateRootSummary",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = new string('x', ReviewPublicationBridgeClient.MaxRequestBytes + 1),
                    sources = Sources,
                }
            )
        );

        called.Should().BeFalse();
        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(ReviewPublicationBridgeClient.RequestTooLargeCode);
    }

    /// <summary>
    /// A backend that accepts the connection and then never answers collapses into the SAME typed
    /// unavailable answer as any other non-answer — and leaves the caller's own token alone, because the
    /// review turn still has work to finish.
    /// </summary>
    /// <remarks>
    /// The fixture's <see cref="HttpClient.Timeout"/> is raised well above the bridge budget on purpose.
    /// Left at its 100-second default it would end this call by itself, and the test would pass without
    /// the bridge owning a budget at all; the elapsed assertion is what makes the bridge's budget the
    /// only thing that can produce this answer this quickly.
    /// </remarks>
    [Fact]
    public async Task AnUnansweredRequest_ExpiresIntoTheUnavailableCode_WithoutCancellingTheCaller()
    {
        using var caller = new CancellationTokenSource();
        var bridge = BuildBridge(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Json(HttpStatusCode.OK, Receipt());
            },
            TimeSpan.FromMilliseconds(50)
        );

        using var arguments = JsonDocument.Parse(MinimalArgumentsFor("CreateRootSummary"));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await bridge.SendAsync("CreateRootSummary", arguments.RootElement, caller.Token);
        started.Stop();

        result.Accepted.Should().BeFalse();
        result.RejectionCode.Should().Be(ReviewPublicationBridgeClient.BackendUnavailableCode);
        caller.IsCancellationRequested.Should().BeFalse("the budget is the bridge's, not the review turn's");
        started
            .Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(5),
                "the 50ms bridge budget ended the call, not the fixture's much larger transport timeout"
            );
    }

    /// <summary>
    /// Caller cancellation is a DIFFERENT event from the budget expiring: the review turn itself was
    /// abandoned, so it must surface as a cancellation rather than as a backend answer the agent could
    /// mistake for a decision about its action. The linked budget must not swallow that distinction.
    /// </summary>
    [Fact]
    public async Task CallerCancellation_PropagatesInsteadOfBecomingABackendAnswer()
    {
        using var caller = new CancellationTokenSource();
        var bridge = BuildBridge(
            async (_, ct) =>
            {
                await caller.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
                return Json(HttpStatusCode.OK, Receipt());
            }
        );

        using var arguments = JsonDocument.Parse(MinimalArgumentsFor("CreateRootSummary"));

        await FluentActions
            .Awaiting(() => bridge.SendAsync("CreateRootSummary", arguments.RootElement, caller.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// A backend failure carrying a raw provider body (which can hold a token, an internal URL or a
    /// stack trace) is reduced to a stable code before the model can see it.
    /// </summary>
    [Fact]
    public async Task RawErrorBody_IsNotSurfacedToTheModel()
    {
        const string RawBody = "System.Net.Http.HttpRequestException: token ghp_SECRETVALUE at Provider.Post()";
        var provider = BuildProvider(_ => Json(HttpStatusCode.InternalServerError, RawBody));

        var result = await InvokeAsync(
            provider,
            "CreateRootSummary",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Sources,
                }
            )
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(ReviewPublicationBridgeClient.BackendUnavailableCode);
        result.ResultText.Should().NotContain("ghp_SECRETVALUE").And.NotContain("HttpRequestException");
    }

    /// <summary>
    /// A 401 from <c>ReviewBridgeAuthAttribute</c> has an EMPTY body and no rejection code. It must not
    /// read as a typed rejection, and it must not tell the model anything about the credential.
    /// </summary>
    [Fact]
    public async Task UnauthorizedBridge_IsReducedToTheUnavailableCode()
    {
        var provider = BuildProvider(_ => Json(HttpStatusCode.Unauthorized, string.Empty));

        var result = await InvokeAsync(
            provider,
            "CreateRootSummary",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Sources,
                }
            )
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(ReviewPublicationBridgeClient.BackendUnavailableCode);
    }

    [Fact]
    public async Task TransportFailure_IsReducedToATypedCode_WithoutTheExceptionText()
    {
        var provider = BuildProvider(_ => throw new HttpRequestException("connect to 10.0.0.5:9443 refused"));

        var result = await InvokeAsync(
            provider,
            "FinalizeRound",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    noOp = false,
                    sources = Sources,
                }
            )
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(ReviewPublicationBridgeClient.BackendUnavailableCode);
        result.ResultText.Should().NotContain("10.0.0.5");
    }

    // --- Argument validation (fail closed, no call) -------------------------------------------

    [Fact]
    public async Task MissingActionId_IsRefusedWithoutReachingTheBackend()
    {
        var (result, called) = await InvokeExpectingNoCallAsync(
            "CreateRootSummary",
            JsonSerializer.Serialize(new { body = "text" })
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task MalformedArguments_AreRefusedWithoutReachingTheBackend()
    {
        var (result, called) = await InvokeExpectingNoCallAsync("ReplyToDiscussion", "not json at all");

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task ReplyToDiscussion_WithoutAProviderTargetId_IsRefusedWithoutReachingTheBackend()
    {
        var (result, called) = await InvokeExpectingNoCallAsync(
            "ReplyToDiscussion",
            JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Sources,
                }
            )
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task FinalizeRound_WithoutNoOp_IsRefusedWithoutReachingTheBackend()
    {
        var (result, called) = await InvokeExpectingNoCallAsync(
            "FinalizeRound",
            JsonSerializer.Serialize(new { actionId = "A-1" })
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
        called.Should().BeFalse();
    }

    /// <summary>
    /// <c>noOp: false</c> is the COMMON case — a round that published something. A required-boolean
    /// check that treats JSON <c>false</c> as absent would refuse every normal finalize, so the empty
    /// check must not fire on it.
    /// </summary>
    [Fact]
    public async Task FinalizeRound_WithNoOpFalse_IsForwarded()
    {
        var (_, payload) = await CaptureAsync(
            "FinalizeRound",
            new
            {
                actionId = "A-1",
                noOp = false,
                sources = Sources,
            }
        );

        payload.GetProperty("noOp").GetBoolean().Should().BeFalse();
    }

    // --- Harness ------------------------------------------------------------------------------

    private static string Receipt() =>
        """
            {
              "status": "Accepted",
              "actionId": "A-9",
              "providerReviewId": "rev-1",
              "providerThreadId": "thr-2",
              "providerCommentId": "cmt-3",
              "providerPermalink": "https://example.test/c/3",
              "relationshipDegraded": true,
              "acceptedAt": "2026-09-02T10:00:00+00:00",
              "rejectionCode": null
            }
            """;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ReviewPublicationFunctionProvider BuildProvider(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        ReviewPublicationScope? scope = null
    )
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler((request, _) => Task.FromResult(respond(request))));
        return new ReviewPublicationFunctionProvider(
            new ReviewPublicationBridgeClient(httpClient, new Uri("https://daemon.test"), scope ?? Scope, Secret)
        );
    }

    /// <summary>Invokes <paramref name="operation"/> and returns the request URI and parsed payload.</summary>
    private static async Task<(Uri Route, JsonElement Payload)> CaptureAsync(
        string operation,
        object arguments,
        ReviewPublicationScope? scope = null
    )
    {
        Uri? route = null;
        JsonElement payload = default;
        var provider = BuildProvider(
            request =>
            {
                route = request.RequestUri;
                payload = JsonDocument
                    .Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                    .RootElement.Clone();
                return Json(HttpStatusCode.OK, Receipt());
            },
            scope
        );

        _ = await InvokeAsync(provider, operation, JsonSerializer.Serialize(arguments));

        return (route!, payload);
    }

    /// <summary>
    /// A bridge over a handler that can observe cancellation, for the budget tests. Goes straight to the
    /// bridge because the budget is a transport concern the function provider does not participate in.
    /// The transport timeout is raised far above any budget under test so that the bridge's own budget,
    /// not <see cref="HttpClient"/>'s 100-second default, is what ends a stalled call.
    /// </summary>
    private static ReviewPublicationBridgeClient BuildBridge(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? timeout = null
    ) =>
        new(
            new HttpClient(new FakeHttpMessageHandler(respond)) { Timeout = TimeSpan.FromSeconds(30) },
            new Uri("https://daemon.test"),
            Scope,
            Secret,
            timeout
        );

    /// <summary>Well-formed CreateRootSummary arguments plus one extra member under a chosen name.</summary>
    private static Dictionary<string, object?> WithExtraMember(string name, object? value) =>
        new()
        {
            ["actionId"] = "A-1",
            ["body"] = "text",
            ["sources"] = Sources,
            [name] = value,
        };

    /// <summary>A well-formed receipt padded to exactly <paramref name="bytes"/> UTF-8 bytes (ASCII).</summary>
    private static string ReceiptOfExactly(int bytes)
    {
        const string Prefix = "{\"status\":\"Accepted\",\"actionId\":\"A-1\",\"providerPermalink\":\"";
        const string Suffix = "\"}";
        return Prefix + new string('x', bytes - Prefix.Length - Suffix.Length) + Suffix;
    }

    /// <summary>Invokes an operation whose arguments should be refused before any HTTP call.</summary>
    private static async Task<(ToolHandlerResult.Resolved Result, bool Called)> InvokeExpectingNoCallAsync(
        string operation,
        string argsJson
    )
    {
        var called = false;
        var provider = BuildProvider(_ =>
        {
            called = true;
            return Json(HttpStatusCode.OK, Receipt());
        });

        return (await InvokeAsync(provider, operation, argsJson), called);
    }

    private static async Task<ToolHandlerResult.Resolved> InvokeAsync(
        ReviewPublicationFunctionProvider provider,
        string operation,
        string argsJson
    )
    {
        var handler = provider.GetFunctions().Single(f => f.Contract.Name == operation).Handler;
        var result = await handler(argsJson, new ToolCallContext(), CancellationToken.None);
        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
    }

    /// <summary>Removes the <c>sources</c> key from an otherwise well-formed argument object.</summary>
    private static string StripSources(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        return JsonSerializer.Serialize(
            document
                .RootElement.EnumerateObject()
                .Where(p => p.Name != "sources")
                .ToDictionary(p => p.Name, p => p.Value)
        );
    }

    private static string MinimalArgumentsFor(string operation) =>
        operation switch
        {
            "SubmitInlineFindings" => JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    findings = new[]
                    {
                        new
                        {
                            path = "a.cs",
                            side = "RIGHT",
                            endLine = 1,
                            body = "f",
                        },
                    },
                    sources = Sources,
                }
            ),
            "ReplyToDiscussion" => JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    providerTargetId = "thr-2",
                    sources = Sources,
                }
            ),
            "FinalizeRound" => JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    noOp = false,
                    sources = Sources,
                }
            ),
            _ => JsonSerializer.Serialize(
                new
                {
                    actionId = "A-1",
                    body = "text",
                    sources = Sources,
                }
            ),
        };

    /// <summary>
    /// A response body of a fixed length that reports how many bytes the host actually pulled from it.
    /// Deliberately NON-SEEKABLE: that leaves <c>Content-Length</c> unset, so the host has to bound the
    /// READ rather than trust a size the backend declared about itself.
    /// </summary>
    private sealed class CountingStream(int length) : Stream
    {
        private int _remaining = length;

        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var served = Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)'x', offset, served);
            _remaining -= served;
            BytesRead += served;
            return served;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
