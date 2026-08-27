using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Wire-level tests for <see cref="SandboxClient.DeleteOperationAsync"/> against the gateway's
/// <c>DELETE .../operations/{operation_id}</c> route (ADR 0031 §5 / issue #464): the <c>204 No Content</c>
/// success, the <c>409 operation_running</c> refusal that ADR 0031 singles out (cancellation is out of
/// scope — a running operation cannot be deleted), and the rest of the direct-API error vocabulary this
/// call inherits from the shared <c>MapDirectErrorAsync</c> seam.
/// </summary>
/// <remarks>
/// Unlike <see cref="SandboxClient.ExecuteAsync"/>, this call resolves no workspace mount — it addresses
/// the operation by id alone — so no mount-resolution route is registered here.
/// </remarks>
public sealed class OperationDeleteTests
{
    private const string SessionId = "sess-del";
    private const string OperationId = "op-del";

    private static void RegisterDelete(FakeGatewayHandler handler, HttpStatusCode status, string? json = null) =>
        handler.On(
            req => req.Method == HttpMethod.Delete && req.RequestUri!.AbsolutePath.EndsWith($"/operations/{OperationId}", StringComparison.Ordinal),
            _ =>
                json is null
                    ? new HttpResponseMessage(status)
                    : new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
        );

    [Fact]
    public async Task DeleteOperationAsync_NoContent_SendsDeleteToTheOperationRoute_WithSessionAndAuthHeaders()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        RegisterDelete(handler, HttpStatusCode.NoContent);

        await client.DeleteOperationAsync(SessionId, OperationId);

        var recorded = handler.Requests.Should().ContainSingle().Subject;
        recorded.Method.Should().Be(HttpMethod.Delete);
        recorded.Uri.AbsolutePath.Should().Be($"/api/v1/sandboxes/{SessionId}/operations/{OperationId}");
        recorded.SessionId.Should().Be(SessionId);
        recorded.SbxAppId.Should().Be("app-1");
        recorded.SbxAppKey.Should().Be(TestSupport.ValidSecret);
        // No request body: the gateway's DELETE carries none, and a 204 answer has none to read.
        recorded.Body.Should().BeNull();
    }

    [Fact]
    public async Task DeleteOperationAsync_Ok_IsAcceptedLikeNoContent_BecauseTheSuccessGuardIsAny2xx()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        // The gateway answers 204 today, but the SDK classifies on IsSuccessStatusCode, not on the exact
        // code — so a 200 (with or without a body) is a successful delete, not a protocol violation. This
        // pins that lenient direction: narrowing the guard to exactly-204 would make a future gateway that
        // answers 200 look like a failed cleanup, and the caller would re-issue a delete that already ran.
        RegisterDelete(handler, HttpStatusCode.OK, """{"deleted":true}""");

        await client.DeleteOperationAsync(SessionId, OperationId);

        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteOperationAsync_PercentEncodesSessionAndOperationIds()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        handler.On(req => req.Method == HttpMethod.Delete, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.DeleteOperationAsync("sess/1", "op 2");

        var recorded = handler.Requests.Should().ContainSingle().Subject;
        // Both segments are escaped, so a caller-supplied id can never traverse out of the route it
        // addresses (the gateway treats the operation id as a single workspace directory component).
        recorded.Uri.AbsoluteUri.Should().EndWith("/api/v1/sandboxes/sess%2F1/operations/op%202");
    }

    [Fact]
    public async Task DeleteOperationAsync_OperationRunning_ThrowsConflict_CarryingTheOperationRunningErrorCode()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        // ADR 0031 §5: a still-running operation is refused with 409 operation_running — deletion is not
        // cancellation. The caller must be able to tell this apart from the OTHER 409s that also classify
        // as Conflict (idempotency_conflict, target_locked), because only this one becomes deletable by
        // simply waiting for the operation to reach a terminal state.
        RegisterDelete(
            handler,
            HttpStatusCode.Conflict,
            """{"error":"operation is running","code":409,"error_code":"operation_running","retryable":false}"""
        );

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Conflict);
        exception.Which.ErrorCode.Should().Be("operation_running");
        exception.Which.StatusCode.Should().Be(409);
        exception.Which.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task DeleteOperationAsync_CodelessConflict_ThrowsConflict_WithNoErrorCodeToMistakeForOperationRunning()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        // The discriminator for "still running" is the error_code, never the 409 status alone: a bodyless
        // (or unparseable) 409 still classifies as Conflict, but must NOT present as operation_running,
        // or a caller that waits-and-retries would loop on a conflict waiting can never clear.
        RegisterDelete(handler, HttpStatusCode.Conflict);

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Conflict);
        exception.Which.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task DeleteOperationAsync_OperationNotFound_ThrowsNotFound_CarryingTheOperationNotFoundErrorCode()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        // An already-deleted operation, a TTL-pruned record, a record dropped by a gateway restart, and an
        // id that never existed are ONE uniform 404 (ADR 0031 §6's no-existence-oracle boundary).
        RegisterDelete(
            handler,
            HttpStatusCode.NotFound,
            """{"error":"not found","code":404,"error_code":"operation_not_found","retryable":false}"""
        );

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.NotFound);
        exception.Which.ErrorCode.Should().Be("operation_not_found");
        exception.Which.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task DeleteOperationAsync_SessionNotFound_ThrowsNotFound()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        RegisterDelete(
            handler,
            HttpStatusCode.NotFound,
            """{"error":"not found","code":404,"error_code":"session_not_found","retryable":false}"""
        );

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.NotFound);
        exception.Which.ErrorCode.Should().Be("session_not_found");
    }

    [Fact]
    public async Task DeleteOperationAsync_Forbidden_ThrowsAuthorization_WithoutReadingTheBody()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        // A 401/403 body is never read (it is the response most likely to echo credential material), so a
        // 403 carrying an error_code still classifies on status alone and surfaces NO ErrorCode.
        RegisterDelete(
            handler,
            HttpStatusCode.Forbidden,
            """{"error":"forbidden","code":403,"error_code":"reserved_path","retryable":false}"""
        );

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Authorization);
        exception.Which.ErrorCode.Should().BeNull();
        exception.Which.StatusCode.Should().Be(403);
        exception.Which.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task DeleteOperationAsync_SandboxBusy_ThrowsUnavailable()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        // The direct-API admission gate (a quiescing session, or the concurrency limiter) answers 503 with
        // a retryable code — a cleanup caller may simply try again later.
        RegisterDelete(
            handler,
            HttpStatusCode.ServiceUnavailable,
            """{"error":"busy","code":503,"error_code":"sandbox_busy","retryable":true}"""
        );

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Unavailable);
        exception.Which.ErrorCode.Should().Be("sandbox_busy");
    }

    [Fact]
    public async Task DeleteOperationAsync_Redirect_IsRefusedAsProtocol_NeverFollowed()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        RegisterDelete(handler, HttpStatusCode.Found);

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        exception.Which.StatusCode.Should().Be(302);
        exception.Which.OperationId.Should().Be(OperationId);
        // Only the DELETE was ever sent: the SDK never chased the Location (which would replay the
        // X-Sbx-* credential headers to the redirect target).
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteOperationAsync_GatewayNeverAnswers_ThrowsTransportTimeout_CarryingTheOperationId()
    {
        // The hang route NEVER completes, so the only way out is the per-call transport budget elapsing —
        // there is no competing completion for a slow runner to win, and the outcome is the same at any
        // speed. The operation id must ride along: this is precisely the ambiguous case where the caller
        // cannot tell whether the artifacts were reclaimed, and the id is what lets it retry the delete.
        var (client, handler) = TestSupport.CreateBorrowedClient(transportTimeout: TimeSpan.FromMilliseconds(100));
        using var _ = client;
        handler.OnHang(req => req.Method == HttpMethod.Delete);

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        exception.Which.StatusCode.Should().BeNull();
        exception.Which.OperationId.Should().Be(OperationId);
    }

    [Fact]
    public async Task DeleteOperationAsync_CallerCancellation_ThrowsPlainOperationCanceledException()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        handler.OnHang(req => req.Method == HttpMethod.Delete);
        using var cts = new CancellationTokenSource();

        var pending = client.DeleteOperationAsync(SessionId, OperationId, cts.Token);
        await cts.CancelAsync();

        // Caller cancellation is deliberately NOT a SandboxErrorKind — it surfaces as a plain OCE.
        // SandboxException does not derive from OperationCanceledException, so this type assertion is
        // itself the discriminator: a masked cancellation would arrive as a SandboxException and fail here.
        var act = () => pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteOperationAsync_BlankSessionId_ThrowsArgumentException_WithoutSendingAnything(string? sessionId)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;

        var act = () => client.DeleteOperationAsync(sessionId!, OperationId);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteOperationAsync_BlankOperationId_ThrowsArgumentException_WithoutSendingAnything(string? operationId)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;

        var act = () => client.DeleteOperationAsync(SessionId, operationId!);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteOperationAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (client, _) = TestSupport.CreateBorrowedClient();
        client.Dispose();

        var act = () => client.DeleteOperationAsync(SessionId, OperationId);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}
