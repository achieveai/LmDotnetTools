using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Wire-level tests for <see cref="SandboxClient.ExecuteAsync"/> against the gateway's direct
/// operations API (ADR 0031 / issue #119): <c>POST .../operations</c> submit, the bounded poll of
/// <c>GET .../operations/{operation_id}</c>, and the terminal stdout/stderr download through
/// <c>GET .../files/{mount_id}</c>. Every test registers the workspace-mount-resolution route first
/// (<see cref="RegisterWorkspaceMount"/>) since <see cref="SandboxClient.ExecuteAsync"/> resolves the
/// mount id before submitting.
/// </summary>
public sealed class OperationExecuteTests
{
    private static void RegisterWorkspaceMount(FakeGatewayHandler handler, string sessionId, long mountId) =>
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/sandboxes/{sessionId}", StringComparison.Ordinal),
            _ => Json(
                "{\"session_id\":\""
                    + sessionId
                    + "\",\"container_id\":null,\"volumes\":{\"workspace\":{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":"
                    + mountId
                    + "}}}"
            )
        );

    private static void RegisterSubmit(FakeGatewayHandler handler, string json, HttpStatusCode status = HttpStatusCode.Accepted) =>
        handler.On(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            _ => Json(json, status)
        );

    private static void RegisterPoll(FakeGatewayHandler handler, string operationId, string json) =>
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/operations/{operationId}", StringComparison.Ordinal),
            _ => Json(json)
        );

    private static void RegisterDownload(FakeGatewayHandler handler, string pathMarker, string content) =>
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.Query.Contains(pathMarker, StringComparison.Ordinal),
            _ => Text(content)
        );

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    [Fact]
    public async Task ExecuteAsync_SubmitAccepted_ThenPollSucceeded_ReturnsExitZeroAndExactOutput()
    {
        const string sessionId = "sess-1";
        const string operationId = "op-1";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 7);
        RegisterSubmit(handler, $$"""{"operation_id":"{{operationId}}","status":"running"}""");
        RegisterPoll(
            handler,
            operationId,
            "{\"operation_id\":\""
                + operationId
                + "\",\"status\":\"succeeded\",\"exit_code\":0,\"artifacts\":{\"mount_id\":7,\"stdout_path\":\".sbx/op-1/stdout\",\"stderr_path\":\".sbx/op-1/stderr\"}}"
        );
        RegisterDownload(handler, "stdout", "hello-exact");
        RegisterDownload(handler, "stderr", "");

        var command = new SandboxCommand(["echo", "hello-exact"], operationId: operationId);
        var result = await client.ExecuteAsync(sessionId, command);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Be("hello-exact");
        result.StandardError.Should().Be("");
        result.OperationId.Should().Be(operationId);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitReturnsTerminalImmediately_DoesNotPoll()
    {
        const string sessionId = "sess-2";
        const string operationId = "op-2";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 3);
        RegisterSubmit(
            handler,
            "{\"operation_id\":\""
                + operationId
                + "\",\"status\":\"succeeded\",\"exit_code\":0,\"artifacts\":{\"mount_id\":3,\"stdout_path\":\"out\",\"stderr_path\":\"err\"}}",
            HttpStatusCode.OK
        );
        RegisterDownload(handler, "path=out", "replayed");
        RegisterDownload(handler, "path=err", "");

        var command = new SandboxCommand(["echo", "hi"], operationId: operationId);
        var result = await client.ExecuteAsync(sessionId, command);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Be("replayed");
        // An idempotent-replay 200 answers the submit with a terminal snapshot directly, so the SDK
        // never issues a follow-up poll GET.
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith($"/operations/{operationId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_TerminalFailed_ReturnsNonZeroExitAndCapturedStderr()
    {
        const string sessionId = "sess-3";
        const string operationId = "op-3";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 5);
        RegisterSubmit(
            handler,
            "{\"operation_id\":\""
                + operationId
                + "\",\"status\":\"failed\",\"exit_code\":7,\"artifacts\":{\"mount_id\":5,\"stdout_path\":\"out\",\"stderr_path\":\"err\"}}",
            HttpStatusCode.OK
        );
        RegisterDownload(handler, "path=out", "");
        RegisterDownload(handler, "path=err", "boom");

        var command = new SandboxCommand(["false"], operationId: operationId);
        var result = await client.ExecuteAsync(sessionId, command);

        result.ExitCode.Should().Be(7);
        result.StandardOutput.Should().Be("");
        result.StandardError.Should().Be("boom");
    }

    [Fact]
    public async Task ExecuteAsync_SubmitIdempotencyConflict_ThrowsConflict()
    {
        const string sessionId = "sess-4";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 1);
        RegisterSubmit(handler, """{"error":"conflict","error_code":"idempotency_conflict"}""", HttpStatusCode.Conflict);

        var act = () => client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"], operationId: "op-4"));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Conflict);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitOperationApiUnavailable_ThrowsUnavailable()
    {
        const string sessionId = "sess-5";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 1);
        RegisterSubmit(handler, """{"error":"agent too old","error_code":"operation_api_unavailable"}""", HttpStatusCode.FailedDependency);

        var act = () => client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"], operationId: "op-5"));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Unavailable);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitWorkspaceRequired_ThrowsWorkspaceRequired()
    {
        const string sessionId = "sess-6";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 1);
        RegisterSubmit(handler, """{"error":"no writable workspace","error_code":"workspace_required"}""", HttpStatusCode.Conflict);

        var act = () => client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"], operationId: "op-6"));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.WorkspaceRequired);
    }

    [Fact]
    public async Task ExecuteAsync_TerminalTimedOut_ThrowsExecutionTimeout()
    {
        const string sessionId = "sess-7";
        const string operationId = "op-7";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 1);
        RegisterSubmit(handler, $$"""{"operation_id":"{{operationId}}","status":"timed_out"}""", HttpStatusCode.OK);

        var act = () => client.ExecuteAsync(sessionId, new SandboxCommand(["sleep", "999"], operationId: operationId));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.ExecutionTimeout);
    }

    [Fact]
    public async Task ExecuteAsync_TerminalOutputLimitExceeded_ThrowsOutputLimitExceeded()
    {
        const string sessionId = "sess-8";
        const string operationId = "op-8";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 1);
        RegisterSubmit(handler, $$"""{"operation_id":"{{operationId}}","status":"output_limit_exceeded"}""", HttpStatusCode.OK);

        var act = () => client.ExecuteAsync(sessionId, new SandboxCommand(["yes"], operationId: operationId));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.OutputLimitExceeded);
    }

    [Fact]
    public async Task ExecuteAsync_UnrecognizedTerminalStatus_ThrowsProtocol()
    {
        const string sessionId = "sess-9";
        const string operationId = "op-9";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 1);
        RegisterSubmit(handler, $$"""{"operation_id":"{{operationId}}","status":"quantum_superposition"}""", HttpStatusCode.OK);

        var act = () => client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"], operationId: operationId));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ExecuteAsync_SuppliedOperationId_IsEchoedInSubmitBody_PollUrl_AndResult()
    {
        const string sessionId = "sess-10";
        const string operationId = "custom-op-123";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(handler, sessionId, mountId: 9);
        RegisterSubmit(handler, $$"""{"operation_id":"{{operationId}}","status":"running"}""");
        RegisterPoll(
            handler,
            operationId,
            "{\"operation_id\":\""
                + operationId
                + "\",\"status\":\"succeeded\",\"exit_code\":0,\"artifacts\":{\"mount_id\":9,\"stdout_path\":\"out\",\"stderr_path\":\"err\"}}"
        );
        RegisterDownload(handler, "path=out", "");
        RegisterDownload(handler, "path=err", "");

        var command = new SandboxCommand(["echo", "hi"], operationId: operationId);
        var result = await client.ExecuteAsync(sessionId, command);

        result.OperationId.Should().Be(operationId);
        handler.Requests.Should().Contain(r => r.Method == HttpMethod.Post && r.Body != null && r.Body.Contains($"\"operation_id\":\"{operationId}\"", StringComparison.Ordinal));
        handler.Requests.Should().Contain(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith($"/operations/{operationId}", StringComparison.Ordinal));
    }
}
