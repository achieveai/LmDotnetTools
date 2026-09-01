using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Thread #1 (PR #121) — the daemon's own outbound HTTP seam must enforce the canonical
/// <see cref="OperationPolicy"/> (plan §4): every provider-API request is classified into a
/// <see cref="SandboxOperation"/> and a denied operation is BOTH egress-blocked (the request never
/// reaches the network) AND credential-denied (no Authorization header leaves the process). The
/// <see cref="OperationPolicyHandler"/> is the request wrapper that closes this; a permitted request
/// passes through to the inner handler with its credential intact.
/// </summary>
public sealed class OperationPolicyHandlerTests : LoggingTestBase
{
    public OperationPolicyHandlerTests(ITestOutputHelper output)
        : base(output) { }

    private static OperationPolicy CreateGitHubPolicy(bool allowWriteOperations = true, int? graphQlPrNumber = null) =>
        new(
            new ReviewScope(
                Provider: "github",
                TargetHost: "github.com",
                TargetRepoPath: "/acme/widgets",
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: "github.com",
                ReviewBotRepoPath: "/acme/reviewbot",
                ApiHost: "api.github.com",
                AllowedSubmodules: []
            )
            {
                GraphQlOwner = "acme",
                GraphQlRepo = "widgets",
                GraphQlPrNumber = graphQlPrNumber,
            },
            allowWriteOperations
        );

    private (HttpClient Client, FakeHttpMessageHandler Inner) BuildClient(OperationPolicy policy)
    {
        var inner = new FakeHttpMessageHandler();
        _ = inner.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new OperationPolicyHandler(policy, "github", LoggerFactory.CreateLogger<OperationPolicyHandler>())
        {
            InnerHandler = inner,
        };
        return (new HttpClient(handler), inner);
    }

    /// <summary>
    /// Same wiring as <see cref="BuildClient"/>, but with a <see cref="CapturingLogger{T}"/> a test can
    /// assert against directly — used only by the diagnostic-logging tests below, which need to inspect
    /// what the handler logged rather than just the resulting decision.
    /// </summary>
    private static (HttpClient Client, CapturingLogger<OperationPolicyHandler> Logger) BuildClientWithCapturingLogger(
        OperationPolicy policy
    )
    {
        var inner = new FakeHttpMessageHandler();
        _ = inner.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK));
        var logger = new CapturingLogger<OperationPolicyHandler>();
        var handler = new OperationPolicyHandler(policy, "github", logger) { InnerHandler = inner };
        return (new HttpClient(handler), logger);
    }

    [Fact]
    public async Task Allows_a_metadata_get_and_passes_the_credential_through()
    {
        var (client, inner) = BuildClient(CreateGitHubPolicy());

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/acme/widgets/pulls?state=open"
        ).WithOperation(SandboxOperation.ReadProviderMetadata);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        using var response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().ContainSingle();
        inner.Requests[0].Authorization.Should().Be("Bearer secret-token", "an allowed request keeps its credential");
    }

    [Fact]
    public async Task Allows_a_post_review_comment_on_the_api_host()
    {
        var (client, inner) = BuildClient(CreateGitHubPolicy());

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.github.com/repos/acme/widgets/issues/7/comments"
        ).WithOperation(SandboxOperation.PostReviewComment);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        using var response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Denies_a_post_to_the_wrong_host_and_blocks_egress()
    {
        var (client, inner) = BuildClient(CreateGitHubPolicy());

        // PostReviewComment requires the API host; github.com (the git host) is not it.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://github.com/acme/widgets/issues/7/comments"
        ).WithOperation(SandboxOperation.PostReviewComment);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var act = () => client.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<OperationDeniedException>();
        inner.Requests.Should().BeEmpty("a denied request must never reach the network");
    }

    [Fact]
    public async Task Denies_a_post_when_the_policy_is_collect_only_and_withholds_the_credential()
    {
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.github.com/repos/acme/widgets/issues/7/comments"
        ).WithOperation(SandboxOperation.PostReviewComment);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var act = () => client.SendAsync(request, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<OperationDeniedException>()).Which;
        thrown.Operation.Should().Be(SandboxOperation.PostReviewComment);
        // The credential must be stripped from the request the moment the policy denies it.
        request
            .Headers.Authorization.Should()
            .BeNull("a denied operation must withhold the credential (fail closed both ways)");
        inner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Denies_an_untagged_request_rather_than_failing_open()
    {
        var (client, inner) = BuildClient(CreateGitHubPolicy());

        // No WithOperation tag — the handler must not let an unclassified request escape unenforced.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/acme/widgets/pulls");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var act = () => client.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<OperationDeniedException>();
        inner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Allows_the_reviewed_safe_graphql_query_end_to_end_with_the_body_preserved_intact()
    {
        // Issue #647 follow-up (MUST #1): a collect-only policy carves out exactly one GraphQL document.
        // Reading the body to make that decision must not consume or alter it — the inner handler must
        // still see the FULL original envelope, "query" and "variables" both (body-preservation pin).
        // GraphQlPrNumber must be bound (issue #666 second correction) or GraphQL denies outright.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false, graphQlPrNumber: 7));

        var requestBody = JsonSerializer.Serialize(
            new
            {
                query = GitHubIssueContextReader.Query,
                variables = new
                {
                    owner = "acme",
                    repo = "widgets",
                    number = 7,
                    pageSize = 20,
                    after = (string?)null,
                },
            }
        );
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
            .WithOperation(SandboxOperation.ReadProviderMetadata)
            .WithGitHubGraphQlScope(new GitHubGraphQlRequestScope("acme", "widgets", 7));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().ContainSingle();
        inner
            .Requests[0]
            .Authorization.Should()
            .Be("Bearer secret-token", "the reviewed-safe GraphQL read must keep its credential");
        inner
            .Requests[0]
            .Body.Should()
            .Be(requestBody, "reading the body for the policy check must not alter what the inner handler receives");
    }

    [Fact]
    public async Task Denies_a_graphql_post_carrying_a_mutation_document_and_never_reaches_the_inner_handler()
    {
        // A hidden mutation tagged as ReadProviderMetadata must never egress and must never keep its credential.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new { query = "mutation { addComment(input: { body: \"pwned\" }) { clientMutationId } }" }
            ),
            Encoding.UTF8,
            "application/json"
        );

        var act = () => client.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<OperationDeniedException>();
        inner
            .Requests.Should()
            .BeEmpty(
                "a mutation document must never reach the inner handler, even when tagged as ReadProviderMetadata"
            );
        request
            .Headers.Authorization.Should()
            .BeNull("a denied GraphQL request must withhold the credential (fail closed both ways)");
    }

    /// <summary>
    /// Sends <paramref name="request"/> through <paramref name="client"/> and asserts the fail-closed
    /// contract every denied GraphQL request-envelope boundary must uphold: the handler throws
    /// <see cref="OperationDeniedException"/>, the request's credential is stripped, and the inner handler
    /// — and thus the network — is never reached.
    /// </summary>
    private static async Task AssertDeniedAndNeverReachedTheInnerHandlerAsync(
        HttpClient client,
        HttpRequestMessage request,
        FakeHttpMessageHandler inner
    )
    {
        var act = () => client.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<OperationDeniedException>();
        inner.Requests.Should().BeEmpty("a denied GraphQL request must never reach the inner handler");
        request
            .Headers.Authorization.Should()
            .BeNull("a denied GraphQL request must withhold the credential (fail closed both ways)");
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_declared_content_length_exceeds_the_cap()
    {
        // The declared Content-Length header alone must short-circuit the read before anything is parsed.
        // Proven by attaching the actual SAFE document as the body and lying only about its declared
        // length: if the length gate did not fire first, this body would otherwise be allowed.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        var content = new StringContent(
            JsonSerializer.Serialize(new { query = GitHubIssueContextReader.Query }),
            Encoding.UTF8,
            "application/json"
        );
        content.Headers.ContentLength = (16 * 1024) + 1; // one byte over the 16 KiB MaxGraphQlBodyBytes cap
        request.Content = content;

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_actual_body_exceeds_the_cap_when_content_length_is_absent()
    {
        // No declared Content-Length (as with a chunked transfer) must not bypass the cap — the handler
        // falls back to measuring the body AFTER reading it, and an oversized body must still be denied.
        // The padding lives in a SIBLING field, not inside "query" itself: "query" is byte-identical to the
        // safe document, so this isolates the post-read size guard specifically — if it did not fire, the
        // exact-match check downstream would otherwise let this body through.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        var content = new StringContent(
            JsonSerializer.Serialize(
                new { query = GitHubIssueContextReader.Query, padding = new string('x', 16 * 1024) }
            ),
            Encoding.UTF8,
            "application/json"
        );
        content.Headers.ContentLength = null; // simulate a transfer with no declared length
        request.Content = content;

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_with_malformed_json()
    {
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent("{\"query\": \"unterminated", Encoding.UTF8, "application/json");

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_with_top_level_non_object_json()
    {
        // Valid JSON, but not an object — the "query" property lookup only ever applies to an object
        // root, so a top-level array (or any other JSON value kind) must be denied, not throw.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new[] { "query", GitHubIssueContextReader.Query }),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_query_differs_only_by_case_from_the_safe_document()
    {
        // A byte-for-byte copy of the safe document except for one letter's case must still be denied —
        // this is what proves the comparison is StringComparison.Ordinal, not OrdinalIgnoreCase.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        var caseFlipped = FlipFirstLetterCase(GitHubIssueContextReader.Query);
        caseFlipped
            .Should()
            .NotBe(GitHubIssueContextReader.Query, "the test fixture must actually differ from the safe document");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query = caseFlipped }),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_query_differs_only_by_trailing_whitespace_from_the_safe_document()
    {
        // A byte-for-byte copy of the safe document plus one trailing space must still be denied — this is
        // what proves the comparison is exact, not trimmed/normalized.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query = GitHubIssueContextReader.Query + " " }),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_that_was_never_tagged_with_the_expected_scope()
    {
        // Issue #666 review (MUST #1) — a byte-identical safe query with a fully well-formed, in-scope
        // variables envelope, sent WITHOUT the trusted caller's WithGitHubGraphQlScope tag. Body content
        // alone (attacker-influenceable) must never be sufficient; only the ONE trusted caller
        // (GitHubIssueContextReader.FetchPageAsync) attaches that tag.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        // Deliberately no .WithGitHubGraphQlScope(...) call.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new
                {
                    query = GitHubIssueContextReader.Query,
                    variables = new
                    {
                        owner = "acme",
                        repo = "widgets",
                        number = 7,
                    },
                }
            ),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_body_variables_are_missing()
    {
        // The envelope carries the safe query but no "variables" object at all — the all-or-nothing parse
        // in TryParseGraphQlScope yields null, so there is nothing to compare the trusted tag against.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
            .WithOperation(SandboxOperation.ReadProviderMetadata)
            .WithGitHubGraphQlScope(new GitHubGraphQlRequestScope("acme", "widgets", 7));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query = GitHubIssueContextReader.Query }),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_variables_number_is_the_wrong_json_type()
    {
        // "number" as a JSON string ("7") rather than a JSON number must not be coerced — the all-or-nothing
        // parse requires a JSON number, so a wrong-typed field collapses the whole scope to null (deny).
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
            .WithOperation(SandboxOperation.ReadProviderMetadata)
            .WithGitHubGraphQlScope(new GitHubGraphQlRequestScope("acme", "widgets", 7));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new
                {
                    query = GitHubIssueContextReader.Query,
                    variables = new
                    {
                        owner = "acme",
                        repo = "widgets",
                        number = "7",
                    },
                }
            ),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_variables_owner_field_is_missing()
    {
        // A required field simply absent (not just wrong-typed) must also collapse the whole scope to null.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
            .WithOperation(SandboxOperation.ReadProviderMetadata)
            .WithGitHubGraphQlScope(new GitHubGraphQlRequestScope("acme", "widgets", 7));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new { query = GitHubIssueContextReader.Query, variables = new { repo = "widgets", number = 7 } }
            ),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_variables_repo_field_is_missing()
    {
        // The symmetric case to the owner test above: "repo" absent (not just wrong-typed) must also
        // collapse the whole scope to null, isolating this field's own guard from owner's.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
            .WithOperation(SandboxOperation.ReadProviderMetadata)
            .WithGitHubGraphQlScope(new GitHubGraphQlRequestScope("acme", "widgets", 7));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new { query = GitHubIssueContextReader.Query, variables = new { owner = "acme", number = 7 } }
            ),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_body_variables_target_a_different_pr_than_the_tag()
    {
        // Issue #666 review (MUST #1) end-to-end: the trusted tag names PR 7 (as FetchPageAsync would for
        // this run), but the body's own "variables.number" claims PR 8 — proving the handler's parsed
        // GraphQlVariables and the request's ExpectedGraphQlScope both reach OperationPolicy, and a
        // disagreement between them denies even though the query text and the tag's owner/repo are exact.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
            .WithOperation(SandboxOperation.ReadProviderMetadata)
            .WithGitHubGraphQlScope(new GitHubGraphQlRequestScope("acme", "widgets", 7));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new
                {
                    query = GitHubIssueContextReader.Query,
                    variables = new
                    {
                        owner = "acme",
                        repo = "widgets",
                        number = 8,
                    },
                }
            ),
            Encoding.UTF8,
            "application/json"
        );

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Denies_a_graphql_post_whose_body_cannot_be_read()
    {
        // Simulates a content stream that fails while being read (e.g. a connection torn mid-body). No
        // declared Content-Length forces the handler down the read path, where the failure must fail closed
        // rather than propagate an unhandled exception or fail open.
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new FailingHttpContent();

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task Logs_a_debug_diagnostic_naming_the_exception_type_when_a_graphql_body_fails_to_read()
    {
        // SHOULD #2 (issue #666 follow-up): a body-read failure must leave a diagnostic trail — but only
        // the exception TYPE, never the raw request content or the Authorization header — so an operator
        // can tell "the read failed" apart from "the JSON was malformed" without this becoming a body leak.
        var (client, logger) = BuildClientWithCapturingLogger(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new FailingHttpContent();

        var act = () => client.SendAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<OperationDeniedException>();

        // The .NET content-reading pipeline wraps a stream failure (e.g. FailingHttpContent's IOException)
        // as HttpRequestException — that is the type the handler's catch actually observes and logs.
        logger
            .CountAtLevel(LogLevel.Debug, nameof(HttpRequestException))
            .Should()
            .BePositive("the read-failure diagnostic must name the exception type at Debug");
        logger
            .MessagesAtLevel(LogLevel.Debug)
            .Should()
            .OnlyContain(
                message => !message.Contains("secret-token", StringComparison.Ordinal),
                "the diagnostic must never carry the credential"
            );
    }

    [Fact]
    public async Task Logs_a_debug_diagnostic_naming_the_exception_type_when_a_graphql_body_is_malformed_json()
    {
        // SHOULD #2 (issue #666 follow-up): same diagnostic contract, but for the parse-failure branch —
        // must not log the raw (malformed) body text itself, only the exception type.
        var (client, logger) = BuildClientWithCapturingLogger(CreateGitHubPolicy(allowWriteOperations: false));

        const string malformedBody = "{\"query\": \"unterminated";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql").WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(malformedBody, Encoding.UTF8, "application/json");

        var act = () => client.SendAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<OperationDeniedException>();

        // JsonDocument.Parse throws the JsonException subclass JsonReaderException for malformed syntax
        // (JsonException itself is reserved for higher-level document/converter failures) — that concrete
        // type is what the handler's catch(JsonException) actually observes and logs. The type is
        // internal to System.Text.Json, so it is named as a literal rather than via nameof/typeof.
        logger
            .CountAtLevel(LogLevel.Debug, "JsonReaderException")
            .Should()
            .BePositive("the parse-failure diagnostic must name the exception type at Debug");
        logger
            .MessagesAtLevel(LogLevel.Debug)
            .Should()
            .OnlyContain(
                message => !message.Contains(malformedBody, StringComparison.Ordinal),
                "the diagnostic must never carry the raw request body"
            );
    }

    /// <summary>Flips the case of the first letter found in <paramref name="text"/>.</summary>
    private static string FlipFirstLetterCase(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
                return new string(chars);
            }
        }

        throw new InvalidOperationException("Expected at least one letter to flip case on.");
    }

    /// <summary>Test double whose body always fails to read, simulating a torn/broken connection.</summary>
    private sealed class FailingHttpContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("simulated read failure"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static readonly RepoIdentity AcmeWidgetsRepo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_1",
    };

    /// <summary>Builds a GraphQL request shaped exactly like <c>GitHubIssueContextReader.FetchPageAsync</c>'s
    /// own output: a tag and a self-consistent body naming <paramref name="prNumber"/>.</summary>
    private static HttpRequestMessage BuildGraphQlRequest(int prNumber)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
            .WithOperation(SandboxOperation.ReadProviderMetadata)
            .WithGitHubGraphQlScope(new GitHubGraphQlRequestScope("acme", "widgets", prNumber));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new
                {
                    query = GitHubIssueContextReader.Query,
                    variables = new
                    {
                        owner = "acme",
                        repo = "widgets",
                        number = prNumber,
                        pageSize = 20,
                        after = (string?)null,
                    },
                }
            ),
            Encoding.UTF8,
            "application/json"
        );
        return request;
    }

    [Fact]
    public async Task SendAsync_denies_a_same_repo_wrong_pr_graphql_request_when_no_override_is_present()
    {
        // Issue #666 second correction: the shared, PR-agnostic policy PolicyEnforcedHttpClientFactory
        // builds once at process startup (no prNumber) must deny GraphQL outright, not merely leave the PR
        // number unconstrained — otherwise any future caller that tags scope but forgets a per-request
        // override would still get a wrong-PR request through.
        var sharedPolicy = DaemonOperationPolicy.BuildForRun(
            AcmeWidgetsRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git"
        );
        var (client, inner) = BuildClient(sharedPolicy);

        using var request = BuildGraphQlRequest(prNumber: 99);

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task SendAsync_denies_a_per_request_policy_override_whose_pr_number_does_not_match_the_request()
    {
        // The override's PR number is itself mandatory-checked, not just present: bound to PR 7, a request
        // tagged/bodied for PR 99 is denied even though the override (not the shared policy) is what's
        // being evaluated.
        var sharedPolicy = DaemonOperationPolicy.BuildForRun(
            AcmeWidgetsRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git"
        );
        var (client, inner) = BuildClient(sharedPolicy);
        var runPolicy = DaemonOperationPolicy.BuildForRun(
            AcmeWidgetsRepo,
            reviewBotRepoUrl: null,
            allowWriteOperations: false,
            prNumber: 7
        );

        using var request = BuildGraphQlRequest(prNumber: 99);
        request.WithPolicyOverride(runPolicy);

        await AssertDeniedAndNeverReachedTheInnerHandlerAsync(client, request, inner);
    }

    [Fact]
    public async Task SendAsync_allows_a_per_request_policy_override_whose_pr_number_matches_the_request()
    {
        // The override's happy path: when the request's own (tag, body) genuinely matches the number the
        // override was built for, the same real SendAsync path allows it and keeps the credential.
        var sharedPolicy = DaemonOperationPolicy.BuildForRun(
            AcmeWidgetsRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git"
        );
        var (client, inner) = BuildClient(sharedPolicy);
        var runPolicy = DaemonOperationPolicy.BuildForRun(
            AcmeWidgetsRepo,
            reviewBotRepoUrl: null,
            allowWriteOperations: false,
            prNumber: 7
        );

        using var request = BuildGraphQlRequest(prNumber: 7);
        request.WithPolicyOverride(runPolicy);

        using var response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().ContainSingle();
        inner.Requests[0].Authorization.Should().Be("Bearer secret-token");
    }
}
