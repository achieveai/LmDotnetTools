using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;
using FluentAssertions;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Deterministic coverage of the LIVE half of
/// <see cref="GitHubClonePrerequisites.VerifyHostVerifiableWorkspaceAsync"/> — the throwaway probe
/// session it creates and must always tear down — driven against a stub gateway transport rather than
/// a real one, so both contracts below are provable on any machine with no gateway running.
/// </summary>
public sealed class GitHubClonePrerequisitesProbeTests
{
    private const string StubBaseUrl = "http://127.0.0.1:9/";

    [Fact]
    public async Task Probe_tears_down_its_session_with_a_token_the_caller_cannot_cancel()
    {
        // The probe creates a real session on a real gateway. If its teardown were threaded with the
        // CALLER's token, a caller that cancels (a test run aborting, a gate timing out) would skip the
        // DELETE for exactly the sessions it had just created — leaking them on the gateway, where they
        // hold a container and a workspace mount open indefinitely.
        //
        // The stub cancels the caller's token AT the moment the DELETE arrives and records, from inside
        // the transport, whether the token that request is actually flowing under is cancelled too. That
        // is a synchronous observation: CancellationTokenSource.Cancel propagates to a linked child
        // before it returns, so if the DELETE were still linked to the caller's token it would already
        // read as cancelled here. No sleeps, no polling, no timing assumptions.
        using var callerCts = new CancellationTokenSource();
        var stub = new ProbeGatewayStub(callerCts);
        using var transport = new HttpClient(stub) { BaseAddress = new Uri(StubBaseUrl) };

        var workspaceBase = Directory.CreateTempSubdirectory("probe-teardown-");
        try
        {
            _ = await GitHubClonePrerequisites.VerifyHostVerifiableWorkspaceAsync(
                StubBaseUrl,
                workspaceBase.FullName,
                callerCts.Token,
                clientFactory: options => new SandboxClient(options, transport));

            var delete = stub.Observations.Should().ContainSingle(o => o.Method == "DELETE").Subject;
            delete.CallerTokenCancelled.Should().BeTrue("the stub cancels the caller's token as the DELETE arrives");
            delete.RequestTokenCancelled.Should()
                .BeFalse("the teardown must run under its own bounded token, not the caller's");

            // The probe leaf is also the caller's directory to lose: cleanup must leave nothing behind.
            Directory.GetFileSystemEntries(workspaceBase.FullName).Should().BeEmpty();
        }
        finally
        {
            workspaceBase.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Probe_failure_reason_names_the_category_without_republishing_the_exception_message()
    {
        // This probe's catch is unfiltered, so the exception it reports on is whatever the transport,
        // the JSON layer or the filesystem threw — text that originates outside this process and lands
        // verbatim in a skip message, test output and CI logs. The reason must therefore carry the
        // failure CATEGORY only. The injected message below stands in for any such payload.
        const string Payload = "bearer sk-live-7f3a9c SECRET-PAYLOAD";

        var workspaceBase = Directory.CreateTempSubdirectory("probe-reason-");
        try
        {
            var result = await GitHubClonePrerequisites.VerifyHostVerifiableWorkspaceAsync(
                StubBaseUrl,
                workspaceBase.FullName,
                CancellationToken.None,
                clientFactory: _ => throw new InvalidOperationException(Payload));

            result.Verified.Should().BeFalse();
            result.Reason.Should().NotContain("SECRET-PAYLOAD").And.NotContain("sk-live-7f3a9c");
            result.Reason.Should()
                .Contain(nameof(InvalidOperationException), "the category still has to be actionable");
        }
        finally
        {
            workspaceBase.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Minimal stub of the two gateway endpoints the probe calls. On the DELETE it cancels
    /// <see cref="_callerCts"/> and then records whether the request's own token followed suit — the
    /// single observation the teardown-independence assertion rests on.
    /// </summary>
    private sealed class ProbeGatewayStub(CancellationTokenSource callerCts) : HttpMessageHandler
    {
        private const string CreateResponse =
            """
            { "session_id": "probe-sess-1", "container_id": "probe-c-1",
              "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
            """;

        private readonly CancellationTokenSource _callerCts = callerCts;
        private readonly List<Observation> _observations = [];
        private readonly object _gate = new();

        public IReadOnlyList<Observation> Observations
        {
            get
            {
                lock (_gate)
                {
                    return [.. _observations];
                }
            }
        }

        public readonly record struct Observation(
            string Method,
            bool CallerTokenCancelled,
            bool RequestTokenCancelled);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                _callerCts.Cancel();
            }

            lock (_gate)
            {
                _observations.Add(
                    new Observation(
                        request.Method.Method,
                        _callerCts.IsCancellationRequested,
                        cancellationToken.IsCancellationRequested));
            }

            var body = request.Method == HttpMethod.Post ? CreateResponse : "{}";
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }
}
