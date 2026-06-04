using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Detects whether a GitHub sign-in (an existing persisted token) is available for the gated
/// <c>git clone</c> egress E2E test, and applies the matching <c>Auth:*</c> configuration so the
/// test host reuses that token and exposes a webhook the sandbox gateway can call back.
/// </summary>
/// <remarks>
/// <para>
/// A real <c>git clone</c> from a sandbox traverses the egress proxy → the gateway's auth webhook →
/// this app, which injects the GitHub credential. That requires (1) the <c>github.com</c> allow-rule,
/// which <c>SandboxSessionRegistry</c> only emits when <c>Auth:Github:ClientId</c> is set, and (2) a
/// signed-in token the webhook can return. Rather than perform an interactive sign-in inside a test,
/// this gate reuses the token a developer already minted by running the sample and signing in once.
/// </para>
/// <para>
/// The token directory is resolved from <c>SANDBOX_OAUTH_TOKENS_DIR</c> if set, else the sample's
/// conventional <c>bin/&lt;config&gt;/net9.0/oauth-tokens</c> output. The test is skipped (green) when
/// no <c>github.json</c> is found — keeping CI green without a sign-in — and runs on any machine where
/// the sample has been signed in to GitHub. Pairs with <c>SandboxGatewayPrerequisites</c>,
/// which gates on the gateway itself.
/// </para>
/// </remarks>
public sealed class GitHubClonePrerequisites
{
    private GitHubClonePrerequisites(bool available, string skipReason, string? tokenStoreDir)
    {
        Available = available;
        SkipReason = skipReason;
        TokenStoreDir = tokenStoreDir;
    }

    /// <summary>True when a persisted GitHub token was found and the gated clone test may run.</summary>
    public bool Available { get; }

    /// <summary>Human-readable reason shown when the test is skipped.</summary>
    public string SkipReason { get; }

    /// <summary>Resolved directory holding the persisted <c>github.json</c> token (when available).</summary>
    public string? TokenStoreDir { get; }

    /// <summary>Probes the environment for a persisted GitHub sign-in and returns availability.</summary>
    public static GitHubClonePrerequisites Detect()
    {
        foreach (var dir in CandidateTokenDirs())
        {
            if (File.Exists(Path.Combine(dir, "github.json")))
            {
                return new GitHubClonePrerequisites(available: true, skipReason: string.Empty, tokenStoreDir: dir);
            }
        }

        return new GitHubClonePrerequisites(
            available: false,
            skipReason:
                "No persisted GitHub sign-in found. Run the sample and sign in to GitHub "
                    + "(or set SANDBOX_OAUTH_TOKENS_DIR to a directory containing github.json) to enable "
                    + "the sandbox git-clone egress test.",
            tokenStoreDir: null);
    }

    /// <summary>
    /// Resolves the workspace base directory the <em>adopted</em> gateway itself uses
    /// (<c>WORKSPACE_BASE_PATH</c>). A session's workspace must be a leaf under this base — a temp dir
    /// the gateway cannot see is rejected with "workspace path not found". Resolved from
    /// <c>SANDBOX_WORKSPACE_BASE</c> if set, else the sample's <c>SandboxGateway:WorkspaceBasePath</c>
    /// in <c>appsettings(.Development).json</c>. Returns <c>null</c> when it cannot be determined.
    /// </summary>
    public static string? ResolveGatewayWorkspaceBase()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SANDBOX_WORKSPACE_BASE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var root = EnvironmentHelper.FindWorkspaceRoot(AppContext.BaseDirectory);
        foreach (var file in new[] { "appsettings.Development.json", "appsettings.json" })
        {
            var path = Path.Combine(root, "samples", "LmStreaming.Sample", file);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("SandboxGateway", out var sg)
                    && sg.TryGetProperty("WorkspaceBasePath", out var wbp)
                    && wbp.GetString() is { Length: > 0 } baseDir)
                {
                    return baseDir;
                }
            }
            catch (JsonException)
            {
                // Try the next candidate file.
            }
        }

        return null;
    }

    /// <summary>The fixed (non-expiring classic OAuth) client id placeholder — see <see cref="GitHubClonePrerequisites"/>.</summary>
    public const string PlaceholderClientId = "e2e-clone-test";

    /// <summary>
    /// Reserves a free loopback TCP port and returns it. There is a tiny window between releasing the
    /// probe socket and Kestrel binding, acceptable for a single-developer gated test.
    /// </summary>
    public static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static IEnumerable<string> CandidateTokenDirs()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SANDBOX_OAUTH_TOKENS_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            yield return fromEnv;
        }

        // Conventional sample output: <repoRoot>/samples/LmStreaming.Sample/bin/<config>/net9.0/oauth-tokens.
        var root = EnvironmentHelper.FindWorkspaceRoot(AppContext.BaseDirectory);
        var sampleBin = Path.Combine(root, "samples", "LmStreaming.Sample", "bin");
        foreach (var config in new[] { "Debug", "Release" })
        {
            yield return Path.Combine(sampleBin, config, "net9.0", "oauth-tokens");
        }
    }
}

/// <summary>
/// Sets a batch of environment variables for the duration of a test and restores their prior values
/// on dispose. Mirrors <c>GatewayConfigScope</c>'s restore semantics for the auth-side vars.
/// </summary>
public sealed class EnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private bool _disposed;

    public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> vars)
    {
        foreach (var (key, value) in vars)
        {
            _previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var (key, value) in _previous)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
