using System.Diagnostics;

namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// Shared plumbing for the interactive (browser + loopback redirect) OAuth providers: thread-safe
/// sign-in status, a single-flight background sign-in runner, and a best-effort system-browser
/// launcher. Concrete providers (GitHub web-app flow, Entra/ADO via MSAL) supply the actual
/// authorization-code exchange and token persistence.
/// </summary>
/// <remarks>
/// SECURITY: access/refresh tokens are secrets. This type and its subclasses never log token
/// material — only provider id, sign-in state, account, expiry, and OAuth error codes.
/// </remarks>
public abstract class OAuthProviderBase : IOAuthTokenProvider
{
    private readonly object _statusGate = new();
    private OAuthStatus _status = new(OAuthSignInState.NotStarted, Account: null, Scopes: [], ExpiresAtUtc: null, Error: null);
    private CancellationTokenSource? _signInCts;
    private Task? _signInTask;

    /// <summary>Creates the base provider.</summary>
    /// <param name="logger">Logger; token material is never written to it.</param>
    protected OAuthProviderBase(ILogger logger) => Logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Logger scoped to the concrete provider.</summary>
    protected ILogger Logger { get; }

    /// <inheritdoc />
    public abstract string ProviderId { get; }

    /// <inheritdoc />
    public OAuthStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public abstract Task HydrateFromStoreAsync(CancellationToken ct = default);

    /// <inheritdoc />
    public abstract Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default);

    /// <inheritdoc />
    public abstract Task SignOutAsync(CancellationToken ct = default);

    /// <inheritdoc />
    public abstract Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default);

    /// <summary>Atomically swaps the current status.</summary>
    protected void SetStatus(OAuthStatus status)
    {
        lock (_statusGate)
        {
            _status = status;
        }
    }

    /// <summary>Marks the provider failed, preserving the current account/expiry for context.</summary>
    protected void SetFailed(string error)
    {
        lock (_statusGate)
        {
            _status = _status with { State = OAuthSignInState.Failed, Error = error };
        }
    }

    /// <summary>
    /// Cancels any in-flight sign-in and starts a new background sign-in task running
    /// <paramref name="signIn"/>. The task is fire-and-forget: it must surface its own outcome via
    /// <see cref="SetStatus"/>/<see cref="SetFailed"/> and never throw into the runtime.
    /// </summary>
    protected async Task StartBackgroundSignInAsync(Func<CancellationToken, Task> signIn)
    {
        await CancelSignInAsync().ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        var task = Task.Run(
            async () =>
            {
                try
                {
                    await signIn(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogDebug("Provider {ProviderId} sign-in cancelled.", ProviderId);
                }
                catch (Exception ex)
                {
                    SetFailed("sign_in_error");
                    Logger.LogError(ex, "Provider {ProviderId} interactive sign-in failed.", ProviderId);
                }
            },
            CancellationToken.None);

        // Publish under the same gate as the status so a concurrent CancelSignInAsync (sign-out,
        // re-sign-in) never observes a torn cts/task pair.
        lock (_statusGate)
        {
            _signInCts = cts;
            _signInTask = task;
        }
    }

    /// <summary>Cancels and awaits the current background sign-in task (if any).</summary>
    protected async Task CancelSignInAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_statusGate)
        {
            cts = _signInCts;
            task = _signInTask;
            _signInCts = null;
            _signInTask = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the sign-in task observes cancellation.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Best-effort launch of the system default browser at <paramref name="url"/>. Returns false
    /// (without throwing) when no browser could be started — the caller can then surface the URL for
    /// the user to open manually.
    /// </summary>
    protected bool OpenBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return process is not null || OperatingSystem.IsWindows();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Provider {ProviderId} could not open the system browser.", ProviderId);
            return false;
        }
    }
}
