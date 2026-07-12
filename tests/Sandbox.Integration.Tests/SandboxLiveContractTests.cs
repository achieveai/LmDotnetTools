using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// The live pinned-gateway contract matrix. Every test drives the real SDK against a real gateway
/// running with <c>AUTH_ENFORCE=true</c>; there are no mocks and no in-process fakes. Each test owns
/// the sandbox it creates and deletes it in a <c>finally</c>, so the app-scoped session list stays
/// clean and the matrix is re-runnable.
/// </summary>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxLiveContractTests
{
    private readonly LiveGatewayFixture _fixture;

    public SandboxLiveContractTests(LiveGatewayFixture fixture) => _fixture = fixture;

    private SandboxClient Client
    {
        get
        {
            Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
            return _fixture.Client;
        }
    }

    private async Task<string> CreateSandboxAsync()
    {
        // The pinned gateway requires a non-empty workspace identifier; use a unique one per sandbox
        // so concurrently-running tests never share a workspace directory.
        var workspace = "wi187-" + Guid.NewGuid().ToString("N");
        var info = await Client.CreateAsync(new SandboxCreateRequest(workspace: workspace));
        info.SessionId.Should().NotBeNullOrWhiteSpace();
        return info.SessionId;
    }

    [SkippableFact]
    public async Task Lifecycle_Create_Get_List_Delete_RoundTrips()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var fetched = await Client.GetAsync(sessionId);
            fetched.SessionId.Should().Be(sessionId);

            var listed = await Client.ListAsync();
            listed.Select(s => s.SessionId).Should().Contain(sessionId);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }

        // After deletion the session is gone: a foreign/missing session is a uniform NotFound.
        var afterDelete = await CaptureAsync(() => Client.GetAsync(sessionId));
        afterDelete.Should().NotBeNull();
        afterDelete!.Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [SkippableFact]
    public async Task Execute_ReturnsExactStdout_AndZeroExit()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var result = await Client.ExecuteAsync(
                sessionId,
                new SandboxCommand(["echo", "-n", "hello-exact"])
            );

            result.ExitCode.Should().Be(0);
            result.StandardOutput.Should().Be("hello-exact");
            result.OperationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Execute_NonZeroExit_CapturesStderr()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var result = await Client.ExecuteAsync(
                sessionId,
                new SandboxCommand(["sh", "-c", "echo boom 1>&2; exit 7"])
            );

            result.ExitCode.Should().Be(7);
            result.StandardError.Should().Contain("boom");
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Execute_SameOperationId_IsNotReRun()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var opId = Guid.NewGuid().ToString("N");
            var marker = "marker-" + opId;

            // A side-effecting append: if the op were re-run, the file would contain two lines.
            var command = new SandboxCommand(
                ["sh", "-c", $"echo {marker} >> /workspace/opid-probe.txt; echo done"],
                operationId: opId
            );

            var first = await Client.ExecuteAsync(sessionId, command);
            first.ExitCode.Should().Be(0);

            var second = await Client.ExecuteAsync(sessionId, command);
            second.ExitCode.Should().Be(0);
            second.OperationId.Should().Be(first.OperationId);

            var content = await Client.ReadTextFileAsync(sessionId, "opid-probe.txt");
            content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Should().ContainSingle(line => line == marker);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task File_WriteThenRead_RoundTripsExactUtf8()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            const string path = "nested/dir/greeting.txt";
            var content = "héllo\nwörld\t— 日本語 🌐\nlast line no newline";

            await Client.WriteTextFileAsync(sessionId, path, content);
            var readBack = await Client.ReadTextFileAsync(sessionId, path);

            readBack.Should().Be(content);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ListDirectory_IncludesDotfilesAndSpacedNames()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            await Client.WriteTextFileAsync(sessionId, "listing/plain.txt", "a");
            await Client.WriteTextFileAsync(sessionId, "listing/a b.txt", "b");
            await Client.WriteTextFileAsync(sessionId, "listing/.hidden", "c");

            var names = await Client.ListDirectoryAsync(sessionId, "listing");

            names.Should().Contain(["plain.txt", "a b.txt", ".hidden"]);
            names.Should().NotContain([".", ".."]);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task PreviewMarketplaces_DoesNotThrow()
    {
        _ = Client; // gate on availability
        var catalog = await Client.PreviewMarketplacesAsync();
        catalog.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task ListDiscovered_ReturnsAList()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var discovered = await Client.ListDiscoveredAsync(sessionId);
            discovered.Should().NotBeNull();
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ForeignOrMissingSession_Get_IsNotFound()
    {
        var captured = await CaptureAsync(() => Client.GetAsync(Guid.NewGuid().ToString()));
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [SkippableFact]
    public async Task WrongCredential_IsAuthorization()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var serverAddress = new Uri(Environment.GetEnvironmentVariable("SANDBOX_BASE_URL")!.Trim());
        var appId = Environment.GetEnvironmentVariable("SANDBOX_APP_ID")!.Trim();

        // A well-formed but WRONG secret (valid standard base64, >=32 bytes) — the SDK accepts it at
        // construction; the gateway rejects it as 401 -> Authorization.
        var wrongKey = Convert.ToBase64String(new byte[32]);
        var options = new SandboxClientOptions(
            serverAddress,
            appId,
            wrongKey,
            executionTimeout: TimeSpan.FromSeconds(30),
            transportTimeout: TimeSpan.FromSeconds(15),
            allowInsecureDevelopmentTransport: string.Equals(serverAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        );

        using var wrongClient = new SandboxClient(options);
        var captured = await CaptureAsync(() => wrongClient.ListAsync());
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(SandboxErrorKind.Authorization);
    }

    private static async Task<SandboxException?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (SandboxException ex)
        {
            return ex;
        }
    }
}
