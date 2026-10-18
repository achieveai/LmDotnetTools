using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Control-plane (REST) error classification against the gateway's real error bodies
/// (SandboxedOstoolsMcpServer origin/master 7f19413): the closed-vocabulary <c>error_code</c> and the
/// structured fields beside it drive <see cref="SandboxException.Kind"/>, while a credential rejection's
/// body is never read and the free-text <c>error</c> is never echoed.
/// </summary>
public sealed class SandboxClientGatewayErrorTests
{
    private const string FreeText = "free-text-that-must-not-be-echoed";

    [Theory]
    // image_policy.rs ImageError::to_json — no `retryable` field on any image error.
    [InlineData(400, "image_not_allowed", SandboxErrorKind.ImageRejected, null)]
    [InlineData(400, "image_unsupported_backend", SandboxErrorKind.ImageRejected, null)]
    [InlineData(404, "image_not_found", SandboxErrorKind.ImageRejected, null)]
    [InlineData(422, "image_unsupported", SandboxErrorKind.ImageRejected, null)]
    [InlineData(502, "image_pull_failed", SandboxErrorKind.Unavailable, null)]
    [InlineData(504, "image_pull_timeout", SandboxErrorKind.Unavailable, null)]
    // api/backup.rs prepare_error_response / api_error_response.
    [InlineData(410, "workspace_purged", SandboxErrorKind.WorkspacePurged, false)]
    [InlineData(503, "workspace_restore_failed", SandboxErrorKind.Unavailable, true)]
    [InlineData(409, "workspace_in_use", SandboxErrorKind.Conflict, true)]
    public async Task CreateAsync_GatewayErrorCode_ClassifiesByCode(
        int status,
        string errorCode,
        SandboxErrorKind expectedKind,
        bool? retryable
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var retryableField = retryable is { } value ? $",\"retryable\":{(value ? "true" : "false")}" : string.Empty;
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            $$"""{"error":"{{FreeText}}","code":{{status}},"error_code":"{{errorCode}}","image":"repo/x:1"{{retryableField}}}""",
            (HttpStatusCode)status
        );

        var thrown = await CreateAndCatchAsync(client);

        thrown.Kind.Should().Be(expectedKind);
        thrown.StatusCode.Should().Be(status);
        thrown.ErrorCode.Should().Be(errorCode);
        thrown.Retryable.Should().Be(retryable);
        thrown.Message.Should().NotContain(FreeText);
    }

    [Fact]
    public async Task CreateAsync_OnlyPartialSnapshots_IsConflictCarryingTheSnapshotIds()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"error":"The workspace has only partial (incomplete) backups; restore one explicitly with POST /api/v1/backup/restore, then retry","code":409,"error_code":"only_partial_snapshots","retryable":false,"snapshots":["a1b2c3","d4e5f6"]}""",
            HttpStatusCode.Conflict
        );

        var thrown = await CreateAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Conflict);
        thrown.ErrorCode.Should().Be("only_partial_snapshots");
        thrown.Retryable.Should().BeFalse();
        thrown.SnapshotIds.Should().Equal("a1b2c3", "d4e5f6");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, SandboxErrorKind.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SandboxErrorKind.Unavailable)]
    [InlineData(HttpStatusCode.Gone, SandboxErrorKind.WorkspacePurged)]
    public async Task GetAsync_CodelessStatus_ClassifiesByStatus(HttpStatusCode status, SandboxErrorKind expectedKind)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // The gateway's legacy AppError body: {error, code} with no error_code.
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/sess-1",
            $$"""{"error":"x","code":{{(int)status}}}""",
            status
        );

        var thrown = (await Record.ExceptionAsync(() => client.GetAsync("sess-1")))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

        thrown.Kind.Should().Be(expectedKind);
        thrown.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_RetryAfterHeader_IsSurfaced()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(
            req => req.Method == HttpMethod.Get,
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"error":"busy","code":503,"error_code":"sandbox_busy","retryable":true}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(5)
                );
                return response;
            }
        );

        var thrown = (await Record.ExceptionAsync(() => client.GetAsync("sess-1")))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

        thrown.Kind.Should().Be(SandboxErrorKind.Unavailable);
        thrown.Retryable.Should().BeTrue();
        thrown.RetryAfter.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CreateAsync_CredentialRejection_BodyIsNeverRead(HttpStatusCode status)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // A body that WOULD reclassify the failure if it were read: the credential-rejection path must not.
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"error":"secret-echo","code":401,"error_code":"image_not_found","retryable":true}""",
            status
        );

        var thrown = await CreateAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Authorization);
        thrown.ErrorCode.Should().BeNull();
        thrown.Retryable.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_OversizedErrorBody_FallsBackToStatusOnly()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // Not a gateway error body: past the read bound, the failure is classified by status alone rather
        // than surfacing the SDK's own read-cap refusal.
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            "{\"error_code\":\"image_not_found\",\"pad\":\""
                + new string('x', (int)SandboxClient.MaxErrorBodyBytes)
                + "\"}",
            HttpStatusCode.Conflict
        );

        var thrown = await CreateAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Conflict);
        thrown.ErrorCode.Should().BeNull();
        thrown.IsDirectReadCapExceeded.Should().BeFalse();
    }

    private static async Task<SandboxException> CreateAndCatchAsync(SandboxClient client) =>
        (await Record.ExceptionAsync(() => client.CreateAsync(new SandboxCreateRequest("ws"))))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;
}
