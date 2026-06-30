using System.Net;
using System.Net.Http.Headers;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
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
        : base(output)
    {
    }

    private static OperationPolicy CreateGitHubPolicy(bool allowWriteOperations = true) =>
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
                AllowedSubmodules: []),
            allowWriteOperations);

    private (HttpClient Client, FakeHttpMessageHandler Inner) BuildClient(OperationPolicy policy)
    {
        var inner = new FakeHttpMessageHandler();
        _ = inner.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new OperationPolicyHandler(
            policy,
            "github",
            LoggerFactory.CreateLogger<OperationPolicyHandler>())
        {
            InnerHandler = inner,
        };
        return (new HttpClient(handler), inner);
    }

    [Fact]
    public async Task Allows_a_metadata_get_and_passes_the_credential_through()
    {
        var (client, inner) = BuildClient(CreateGitHubPolicy());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/acme/widgets/pulls?state=open")
            .WithOperation(SandboxOperation.ReadProviderMetadata);
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

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/repos/acme/widgets/issues/7/comments")
            .WithOperation(SandboxOperation.PostReviewComment);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/acme/widgets/issues/7/comments")
            .WithOperation(SandboxOperation.PostReviewComment);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var act = () => client.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<OperationDeniedException>();
        inner.Requests.Should().BeEmpty("a denied request must never reach the network");
    }

    [Fact]
    public async Task Denies_a_post_when_the_policy_is_collect_only_and_withholds_the_credential()
    {
        var (client, inner) = BuildClient(CreateGitHubPolicy(allowWriteOperations: false));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/repos/acme/widgets/issues/7/comments")
            .WithOperation(SandboxOperation.PostReviewComment);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var act = () => client.SendAsync(request, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<OperationDeniedException>()).Which;
        thrown.Operation.Should().Be(SandboxOperation.PostReviewComment);
        // The credential must be stripped from the request the moment the policy denies it.
        request.Headers.Authorization.Should().BeNull("a denied operation must withhold the credential (fail closed both ways)");
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
}
