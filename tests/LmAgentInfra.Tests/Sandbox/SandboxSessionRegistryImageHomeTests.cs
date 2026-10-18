using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// A workspace's client image and home reach the sandbox create, the gateway's acknowledgement is kept on the
/// session, and a rejected image names its reason in the failure the UI shows.
/// </summary>
public class SandboxSessionRegistryImageHomeTests
{
    [Fact]
    public async Task GetOrCreateSessionAsync_WorkspaceImageAndHome_ReachTheCreateAndTheAckIsKeptOnTheSession()
    {
        string? createBody = null;
        await using var registry = CreateRegistry(
            (request, body) =>
            {
                createBody = body;
                return Json(
                    HttpStatusCode.Created,
                    """
                    {"session_id":"sess-1","container_id":"c-1",
                     "volumes":{"workspace":{"container_path":"/workspace","read_only":false,"home":"wt/issue-1"}},
                     "image":"debian:bookworm-slim","image_digest":"debian@sha256:0104"}
                    """
                );
            }
        );

        var session = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws-1", Image: "debian:bookworm-slim", Home: "wt/issue-1")
        );

        using var sent = JsonDocument.Parse(createBody!);
        sent.RootElement.GetProperty("image").GetString().Should().Be("debian:bookworm-slim");
        sent.RootElement.GetProperty("home").GetString().Should().Be("wt/issue-1");
        session.Image.Should().Be("debian:bookworm-slim");
        session.ImageDigest.Should().Be("debian@sha256:0104");
        session.WorkspaceHome.Should().Be("wt/issue-1");
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_ImageRejected_TheFailureNamesTheGatewayErrorCode()
    {
        await using var registry = CreateRegistry(
            (_, _) =>
                Json(
                    HttpStatusCode.BadRequest,
                    """{"error":"not allowed","code":400,"error_code":"image_not_allowed","image":"alpine:3.20"}"""
                )
        );

        var act = () => registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", Image: "alpine:3.20"));

        (await act.Should().ThrowAsync<SandboxSessionUnavailableException>())
            .Which.Message.Should()
            .Contain("400 (image_not_allowed)");
    }

    private static SandboxSessionRegistry CreateRegistry(Func<HttpRequestMessage, string?, HttpResponseMessage> create)
    {
        var options = new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new Gateway((_, _) => new HttpResponseMessage(HttpStatusCode.OK)))
        );
        return new SandboxSessionRegistry(
            lifetime,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(
                new Gateway(
                    (request, body) =>
                        request.Method == HttpMethod.Post
                            ? create(request, body)
                            : new HttpResponseMessage(HttpStatusCode.OK)
                )
            ),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmagentinfra-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Gateway(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request, body);
        }
    }
}
