using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Client image (ADR 0039) and workspace home (gateway issue #142) on sandbox create, against response
/// bodies captured from a live v0.1.16 gateway: the request fields, the create acknowledgement
/// (<c>image</c>, <c>image_digest</c>, <c>image_report</c>, <c>volumes.workspace.home</c>), the exact-home
/// check, and the 422 conformance report on the exception.
/// </summary>
public sealed class SandboxClientImageHomeTests
{
    private const string ReportJson = """
        {"os_id":"debian","debian_family":true,"arch":"x86_64","user":"sandbox","uid":1000,"gid":1000,
         "user_reused":false,"has_bash":true,"has_pwsh":false,"has_system_ca_bundle":false,
         "warnings":["image has no pwsh; the PowerShell tool will fail"]}
        """;

    private static string CreatedJson(string? home) =>
        "{\"session_id\":\"s-1\",\"container_id\":\"c-1\",\"volumes\":{\"workspace\":{\"container_path\":\"/workspace\","
        + "\"read_only\":false,\"id\":31"
        + (home is null ? string.Empty : $",\"home\":\"{home}\"")
        + "}},\"image\":\"debian:bookworm-slim\",\"image_digest\":\"debian@sha256:0104b3\",\"image_report\":"
        + ReportJson
        + "}";

    [Fact]
    public async Task CreateAsync_ImageAndHome_AreSentAndTheAcknowledgementIsSurfaced()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreatedJson("worktrees/issue-1"), HttpStatusCode.Created);

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", image: "debian:bookworm-slim", home: "worktrees/issue-1")
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.GetProperty("image").GetString().Should().Be("debian:bookworm-slim");
        sent.RootElement.GetProperty("home").GetString().Should().Be("worktrees/issue-1");
        info.Image.Should().Be("debian:bookworm-slim");
        info.ImageDigest.Should().Be("debian@sha256:0104b3");
        info.WorkspaceHome.Should().Be("worktrees/issue-1");
        info.ImageReport.Should()
            .BeEquivalentTo(
                new SandboxImageReport
                {
                    OsId = "debian",
                    DebianFamily = true,
                    Arch = "x86_64",
                    User = "sandbox",
                    Uid = 1000,
                    Gid = 1000,
                    UserReused = false,
                    HasBash = true,
                    HasPwsh = false,
                    HasSystemCaBundle = false,
                    Warnings = ["image has no pwsh; the PowerShell tool will fail"],
                }
            );
    }

    [Fact]
    public async Task CreateAsync_NoImageOrHome_OmitsBothFieldsSoTheGatewayDefaultsApply()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreatedJson(home: null), HttpStatusCode.Created);

        _ = await client.CreateAsync(new SandboxCreateRequest("ws"));

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.TryGetProperty("image", out _).Should().BeFalse();
        sent.RootElement.TryGetProperty("home", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_HomeAcknowledgedInNormalizedForm_Succeeds()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreatedJson("worktrees/issue-1"), HttpStatusCode.Created);

        var info = await client.CreateAsync(new SandboxCreateRequest("ws", home: "./worktrees//issue-1/"));

        info.WorkspaceHome.Should().Be("worktrees/issue-1");
        handler.Requests.Should().ContainSingle("a matching acknowledgement must not delete the sandbox");
    }

    [Theory]
    [InlineData(null)] // a gateway that predates per-sandbox homes never sends the field
    [InlineData("worktrees/other")]
    public async Task CreateAsync_HomeNotAcknowledgedExactly_DeletesTheSandboxAndThrowsProtocol(string? acknowledged)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreatedJson(acknowledged), HttpStatusCode.Created);
        handler.OnJson(HttpMethod.Delete, "/api/v1/sandboxes/s-1", "{\"session_id\":\"s-1\",\"message\":\"ok\"}");

        var act = () => client.CreateAsync(new SandboxCreateRequest("ws", home: "worktrees/issue-1"));

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.Message.Should().Contain("s-1").And.Contain("worktrees/issue-1").And.Contain("deleted");
        handler.Requests.Should().Contain(r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath.EndsWith("/s-1"));
    }

    [Fact]
    public async Task CreateAsync_ImageUnsupported_CarriesTheConformanceReport()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            "{\"error\":\"probe rejected\",\"code\":422,\"error_code\":\"image_unsupported\",\"image\":\"x:1\",\"report\":"
                + ReportJson.Replace("\"debian_family\":true", "\"debian_family\":false")
                + "}",
            HttpStatusCode.UnprocessableEntity
        );

        var act = () => client.CreateAsync(new SandboxCreateRequest("ws", image: "x:1"));

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.ImageRejected);
        thrown.ErrorCode.Should().Be("image_unsupported");
        thrown.ImageReport.Should().NotBeNull();
        thrown.ImageReport!.DebianFamily.Should().BeFalse();
        thrown.ImageReport.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAsync_ReportsTheHome_ButNoImageFieldsBecauseTheGatewaySendsNone()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // The gateway's GET detail (SandboxDetail): session, container, created_at and volumes only.
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s-1",
            "{\"session_id\":\"s-1\",\"container_id\":\"c-1\",\"created_at\":\"2026-10-09T20:55:00Z\",\"volumes\":"
                + "{\"workspace\":{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":31,\"home\":\"wt\"}}}"
        );

        var info = await client.GetAsync("s-1");

        info.WorkspaceHome.Should().Be("wt");
        info.Image.Should().BeNull();
        info.ImageDigest.Should().BeNull();
        info.ImageReport.Should().BeNull();
    }
}
