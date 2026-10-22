using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Client image (ADR 0039) and working directory (ADR 0044, formerly "home") on sandbox create, against
/// response bodies shaped like a live gateway's: the request fields, the create acknowledgement (<c>image</c>,
/// <c>image_digest</c>, <c>image_report</c>, <c>volumes.workspace.working_dir</c>), the exact-acknowledgement
/// check, and the 422 conformance report on the exception.
/// </summary>
public sealed class SandboxClientImageWorkingDirTests
{
    private const string ReportJson = """
        {"os_id":"debian","debian_family":true,"arch":"x86_64","user":"sandbox","uid":1000,"gid":1000,
         "user_reused":false,"has_bash":true,"has_pwsh":false,"has_system_ca_bundle":false,
         "warnings":["image has no pwsh; the PowerShell tool will fail"]}
        """;

    private static string CreatedJson(string? workingDir, string? legacyHome = null) =>
        "{\"session_id\":\"s-1\",\"container_id\":\"c-1\",\"volumes\":{\"workspace\":{\"container_path\":\"/workspace\","
        + "\"read_only\":false,\"id\":31"
        + (workingDir is null ? string.Empty : $",\"working_dir\":\"{workingDir}\"")
        + (legacyHome is null ? string.Empty : $",\"home\":\"{legacyHome}\"")
        + "}},\"image\":\"debian:bookworm-slim\",\"image_digest\":\"debian@sha256:0104b3\",\"image_report\":"
        + ReportJson
        + "}";

    [Fact]
    public async Task CreateAsync_ImageAndWorkingDir_AreSentAndTheAcknowledgementIsSurfaced()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            CreatedJson("agents/a1", legacyHome: "agents/a1"),
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", image: "debian:bookworm-slim", workingDir: "agents/a1")
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.GetProperty("image").GetString().Should().Be("debian:bookworm-slim");
        sent.RootElement.GetProperty("working_dir").GetString().Should().Be("agents/a1");
        sent.RootElement.TryGetProperty("home", out _).Should().BeFalse("the deprecated alias is never sent");
        info.Image.Should().Be("debian:bookworm-slim");
        info.ImageDigest.Should().Be("debian@sha256:0104b3");
        info.WorkspaceWorkingDir.Should().Be("agents/a1");
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
    public async Task CreateAsync_NoImageOrWorkingDir_OmitsBothFieldsSoTheGatewayDefaultsApply()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreatedJson(workingDir: null), HttpStatusCode.Created);

        _ = await client.CreateAsync(new SandboxCreateRequest("ws"));

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.TryGetProperty("image", out _).Should().BeFalse();
        sent.RootElement.TryGetProperty("working_dir", out _).Should().BeFalse();
        sent.RootElement.TryGetProperty("home", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WorkingDirAcknowledgedInNormalizedForm_Succeeds()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreatedJson("agents/a1"), HttpStatusCode.Created);

        var info = await client.CreateAsync(new SandboxCreateRequest("ws", workingDir: "./agents//a1/"));

        info.WorkspaceWorkingDir.Should().Be("agents/a1");
        handler.Requests.Should().ContainSingle("a matching acknowledgement must not delete the sandbox");
    }

    [Theory]
    [InlineData(null, null)] // a gateway that predates per-sandbox directories sends neither key
    [InlineData(null, "agents/a1")] // v0.1.16 echoes only `home` and does not make the workspace read-only
    [InlineData("agents/other", null)]
    public async Task CreateAsync_WorkingDirNotAcknowledgedExactly_DeletesTheSandboxAndThrowsProtocol(
        string? acknowledged,
        string? legacyHome
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            CreatedJson(acknowledged, legacyHome),
            HttpStatusCode.Created
        );
        handler.OnJson(HttpMethod.Delete, "/api/v1/sandboxes/s-1", "{\"session_id\":\"s-1\",\"message\":\"ok\"}");

        var act = () => client.CreateAsync(new SandboxCreateRequest("ws", workingDir: "agents/a1"));

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.Message.Should().Contain("s-1").And.Contain("agents/a1").And.Contain("deleted");
        handler.Requests.Should().Contain(r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath.EndsWith("/s-1"));
    }

    [Fact]
    public async Task CreateAsync_WorkingDirNotAcknowledged_CallerCancelsDuringCleanup_StillDeletesTheSandbox()
    {
        // Once CreateAsync throws, nothing else holds the session id: a caller cancelling while the cleanup DELETE
        // is in flight must not abandon it.
        using var cts = new CancellationTokenSource();
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreatedJson(null), HttpStatusCode.Created);
        handler.On(
            r => r.Method == HttpMethod.Delete,
            _ =>
            {
                cts.Cancel();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"session_id\":\"s-1\",\"message\":\"ok\"}",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }
        );

        var act = () => client.CreateAsync(new SandboxCreateRequest("ws", workingDir: "agents/a1"), cts.Token);

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Message.Should().Contain("It was deleted.");
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
    public async Task GetAsync_ReportsTheImageAndDigest_WhenTheGatewaysDetailCarriesThem()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s-1",
            "{\"session_id\":\"s-1\",\"container_id\":\"c-1\",\"image\":\"ghcr.io/acme/py:3.12\","
                + "\"image_digest\":\"ghcr.io/acme/py@sha256:abc\",\"volumes\":"
                + "{\"workspace\":{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":31}}}"
        );

        var info = await client.GetAsync("s-1");

        info.Image.Should().Be("ghcr.io/acme/py:3.12");
        info.ImageDigest.Should().Be("ghcr.io/acme/py@sha256:abc");
        info.ImageReport.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReportsTheWorkingDir_ButNoImageFields_FromAGatewayWhoseDetailSendsNone()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // A gateway before the GET image fields: session, container, created_at and volumes only.
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s-1",
            "{\"session_id\":\"s-1\",\"container_id\":\"c-1\",\"created_at\":\"2026-10-09T20:55:00Z\",\"volumes\":"
                + "{\"workspace\":{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":31,\"working_dir\":\"wt\"}}}"
        );

        var info = await client.GetAsync("s-1");

        info.WorkspaceWorkingDir.Should().Be("wt");
        info.Image.Should().BeNull();
        info.ImageDigest.Should().BeNull();
        info.ImageReport.Should().BeNull();
    }
}
