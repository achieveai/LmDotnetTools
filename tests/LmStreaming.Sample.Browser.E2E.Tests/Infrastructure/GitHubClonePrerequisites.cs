using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.Sandbox;

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

    /// <summary>
    /// Upper bound on the probe session's teardown. Deliberately independent of the caller's token so
    /// the DELETE still runs after a cancellation, and bounded so a wedged gateway cannot hang the
    /// prerequisite gate indefinitely.
    /// </summary>
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

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

    /// <summary>
    /// Probes whether the ADOPTED gateway at <paramref name="gatewayBaseUrl"/> reports a workspace
    /// path that is genuinely verifiable on THIS host under <paramref name="workspaceBase"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SandboxGitCloneInstructionChainTests</c> asserts against real files written under a
    /// Windows host directory (<paramref name="workspaceBase"/>). That assertion is only meaningful
    /// when the adopted gateway's own notion of "the workspace" is that same host directory. A
    /// gateway fronting a Docker backend (or any backend whose workspace root is a container-internal
    /// mount, a named volume, or otherwise not the resolved host path) reports a
    /// <see cref="SandboxInfo.WorkspaceContainerPath"/> that never matches — the clone can genuinely
    /// succeed inside the container while leaving nothing for this host-side test to find, which is
    /// an environment/topology mismatch, not a product regression.
    /// </para>
    /// <para>
    /// This creates one throwaway sandbox session under a unique probe leaf, inspects what the
    /// gateway reports back, and tears the session down again — mirroring exactly the trust
    /// <see cref="SandboxSessionRegistry" /> production code places in that same field (it takes
    /// <c>info.WorkspaceContainerPath</c> as the session's host path verbatim). Never throws: any
    /// failure (unreachable gateway, malformed response, timeout) degrades to a not-verified result
    /// with a descriptive reason, so a broken probe skips the gated test rather than failing it.
    /// </para>
    /// </remarks>
    /// <param name="gatewayBaseUrl">Base address of the adopted gateway to probe.</param>
    /// <param name="workspaceBase">The host workspace base the gateway is expected to resolve against.</param>
    /// <param name="ct">Cancels the probe itself; never the teardown of a session it already created.</param>
    /// <param name="clientFactory">
    /// Test seam. Builds the <see cref="SandboxClient"/> this probe talks to; defaults to a real
    /// client over its own transport. Supplying a stub transport is what makes the teardown and
    /// error-reporting contracts above verifiable without a live gateway.
    /// </param>
    public static async Task<HostWorkspaceVerification> VerifyHostVerifiableWorkspaceAsync(
        string gatewayBaseUrl,
        string workspaceBase,
        CancellationToken ct = default,
        Func<SandboxClientOptions, SandboxClient>? clientFactory = null)
    {
        var leaf = "e2e-prereq-probe-" + Guid.NewGuid().ToString("N")[..8];
        var probeHostPath = Path.Combine(workspaceBase, leaf);

        // Declared out here so the teardown below can reach the ONE client the probe built; still
        // CONSTRUCTED inside the try, because both the Uri parse and the client's own construction can
        // throw on a malformed gateway address, and this method's contract is to degrade to
        // not-verified rather than fail the caller's gate.
        SandboxClient? client = null;
        string? sessionId = null;
        try
        {
            Directory.CreateDirectory(probeHostPath);

            var options = new SandboxClientOptions(
                new Uri(gatewayBaseUrl),
                appId: "e2e-prereq-probe",
                clientSecret: string.Empty,
                executionTimeout: TimeSpan.FromSeconds(30),
                transportTimeout: TimeSpan.FromSeconds(15),
                allowInsecureDevelopmentTransport: true);
            client = clientFactory?.Invoke(options) ?? new SandboxClient(options);

            var info = await client.CreateAsync(new SandboxCreateRequest(leaf), ct).ConfigureAwait(false);
            sessionId = info.SessionId;

            return HostWorkspacePathVerifier.Verify(info.WorkspaceContainerPath, probeHostPath);
        }
        catch (Exception ex)
        {
            // Report the failure CATEGORY only. This catch is deliberately unfiltered, so ex is
            // whatever the transport, the JSON layer or the filesystem threw, and its Message is
            // text this reason string is not entitled to republish into test output and CI logs.
            return new HostWorkspaceVerification(
                Verified: false,
                Reason: "Could not verify the adopted gateway's workspace against this host: the probe "
                    + $"failed with {ex.GetType().Name}. Run the probe manually against the gateway for "
                    + "the underlying detail.");
        }
        finally
        {
            if (client is not null)
            {
                if (sessionId is not null)
                {
                    try
                    {
                        // The throwaway session must be deleted even when the CALLER's token is already
                        // cancelled — passing ct here would skip the DELETE in exactly the case that
                        // created a session and then abandoned it, leaking it on the real gateway. An
                        // independent, bounded token keeps the cleanup both unconditional and finite.
                        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
                        await client.DeleteAsync(sessionId, cleanupCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort teardown of the throwaway probe session.
                    }
                }

                client.Dispose();
            }

            try
            {
                if (Directory.Exists(probeHostPath))
                {
                    Directory.Delete(probeHostPath, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of the probe directory.
            }
        }
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

/// <summary>
/// Result of <see cref="GitHubClonePrerequisites.VerifyHostVerifiableWorkspaceAsync"/>: whether the
/// adopted gateway's reported workspace path is verifiable on this host, and why not when it isn't.
/// </summary>
public sealed record HostWorkspaceVerification(bool Verified, string Reason);

/// <summary>
/// Pure decision logic for whether a gateway-reported workspace path is the same, real, existing
/// directory on this host as the one the caller expects — deliberately separated from the live probe
/// in <see cref="GitHubClonePrerequisites"/> so it is unit-testable without a running gateway.
/// </summary>
/// <remarks>
/// On a local (non-Docker) gateway backend, <c>SandboxInfo.WorkspaceContainerPath</c> IS the real host
/// path (there is no container — "inside the sandbox" and "on the host" are the same filesystem), so
/// it round-trips back as a fully-qualified, existing Windows path. On a Docker backend that same
/// field is instead the fixed in-container mount point (conventionally <c>/workspace</c>) — a
/// Linux-style, non-drive-qualified path that is never a real Windows directory and can never match
/// the resolved host workspace. This mirrors (without duplicating) the trust
/// <c>SandboxSessionRegistry.CreateSessionAsync</c> places in the same field: it takes
/// <c>info.WorkspaceContainerPath</c> as the session's host path verbatim, so whatever that method
/// would treat as "the host path" is exactly what this verifies against the expected directory.
/// </remarks>
public static class HostWorkspacePathVerifier
{
    /// <summary>
    /// Verifies that <paramref name="reportedPath"/> — the value a gateway returned as
    /// <c>SandboxInfo.WorkspaceContainerPath</c> — both looks like and actually IS
    /// <paramref name="expectedHostPath"/> on this host.
    /// </summary>
    public static HostWorkspaceVerification Verify(string? reportedPath, string expectedHostPath)
    {
        if (string.IsNullOrWhiteSpace(reportedPath))
        {
            return new HostWorkspaceVerification(
                false,
                "The adopted gateway reported no workspace container path for the probe session.");
        }

        var strippedReported = StripLongPathPrefix(reportedPath);

        // A Docker-backend gateway reports its fixed in-container mount point (e.g. "/workspace").
        // Catch that generically, without hardcoding "Docker" or any specific mount literal — but only
        // where it is syntactically decidable at all (see LooksLikeAHostPath).
        if (!LooksLikeAHostPath(strippedReported, OperatingSystem.IsWindows()))
        {
            return new HostWorkspaceVerification(
                false,
                $"The adopted gateway reported workspace path '{reportedPath}', which is not shaped like "
                    + "an absolute path on this host — this looks like a container-internal mount point "
                    + "(typical of a Docker-backed gateway), not this host's filesystem.");
        }

        string normalizedReported;
        string normalizedExpected;
        try
        {
            normalizedReported = Path.TrimEndingDirectorySeparator(Path.GetFullPath(strippedReported));
            normalizedExpected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StripLongPathPrefix(expectedHostPath)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new HostWorkspaceVerification(
                false,
                $"The adopted gateway reported workspace path '{reportedPath}', which is not a valid "
                    + $"path on this host ({ex.GetType().Name}: {ex.Message}).");
        }

        if (!Directory.Exists(normalizedReported))
        {
            return new HostWorkspaceVerification(
                false,
                $"The adopted gateway reported workspace path '{normalizedReported}', but no such "
                    + "directory exists on this host — the gateway's workspace is not this host's "
                    + "filesystem.");
        }

        if (!string.Equals(normalizedReported, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return new HostWorkspaceVerification(
                false,
                $"The adopted gateway reported workspace path '{normalizedReported}', which does not "
                    + $"match the expected host workspace '{normalizedExpected}' — the gateway is not "
                    + "resolving workspaces against this host's directory tree.");
        }

        return new HostWorkspaceVerification(true, string.Empty);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is shaped like an absolute path on a host of the given flavour.
    /// This is only a PRE-check: its whole job is to reject the container-internal mount point a
    /// Docker-backed gateway reports, in the one case where that is syntactically decidable — a
    /// WINDOWS host, where a POSIX-rooted path such as <c>/workspace</c> can never name a local
    /// directory. On a Unix host that mount point and a real host path are syntactically IDENTICAL, so
    /// there is nothing to decide here and the existence/equality checks in <see cref="Verify"/> are
    /// what reject it (a container-internal mount is not a real directory on the host, and even if a
    /// same-named one existed it would not be the unique probe leaf the caller just created).
    /// </summary>
    /// <remarks>
    /// The host flavour is a PARAMETER, and <see cref="Path.IsPathRooted(string)"/> is deliberately not
    /// used: both that method and the previous drive-letter-only rule answer for whatever platform the
    /// test process happens to run on, which is exactly the coupling that made this verifier reject
    /// every legitimate path on a non-Windows CI agent. Windows UNC paths are intentionally out of
    /// scope — the workspace bases this gate is used with are drive-qualified.
    /// </remarks>
    internal static bool LooksLikeAHostPath(string path, bool windowsHost) =>
        windowsHost
            ? path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])
            : path.StartsWith('/');

    private static string StripLongPathPrefix(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
}
