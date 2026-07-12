namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>Shared test fixtures: a valid client secret and a helper to wire a <see cref="SandboxClient"/> over a <see cref="FakeGatewayHandler"/>.</summary>
internal static class TestSupport
{
    public static readonly string ValidSecret = Convert.ToBase64String(new byte[32]);

    public static readonly Uri LoopbackAddress = new("http://127.0.0.1:3000");

    public static (SandboxClient Client, FakeGatewayHandler Handler) CreateBorrowedClient(
        TimeSpan? transportTimeout = null,
        string appId = "app-1",
        string? clientSecret = null
    )
    {
        var handler = new FakeGatewayHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = LoopbackAddress };
        var options = new SandboxClientOptions(
            LoopbackAddress,
            appId,
            clientSecret ?? ValidSecret,
            TimeSpan.FromMinutes(5),
            transportTimeout ?? TimeSpan.FromSeconds(30)
        );
        var client = new SandboxClient(options, httpClient);
        return (client, handler);
    }
}
