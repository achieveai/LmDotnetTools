namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Immutable, constructor-validated configuration for a <see cref="SandboxClient"/>: the gateway
/// address, the per-app credential (ADR 0029), and the two independently configurable timeouts.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a plain sealed class, NOT a record: a record's generated <c>ToString</c>,
/// equality, and deconstruction would each print or expose <see cref="ClientSecret"/> verbatim.
/// Overriding <see cref="ToString"/> is not enough on a record because the compiler-generated
/// <c>PrintMembers</c> still exists and equality/deconstruction still leak the raw value through
/// other paths — a plain class with no generated members closes all of them at once.
/// </para>
/// <para>
/// <see cref="ClientSecret"/> is validated at construction (well-formed standard base64, minimum
/// decoded length) so a malformed credential fails fast at startup rather than on the first
/// gateway call, and the validation failure message never includes the secret value itself.
/// </para>
/// </remarks>
public sealed class SandboxClientOptions
{
    /// <summary>Minimum number of bytes <see cref="ClientSecret"/> must decode to.</summary>
    private const int MinClientSecretBytes = 32;

    /// <summary>Absolute base address of the sandbox gateway (e.g. <c>https://sandbox.internal:3443</c>).</summary>
    public Uri ServerAddress { get; }

    /// <summary>App identifier sent as the <c>X-Sbx-App-Id</c> header on every app-facing request.</summary>
    public string AppId { get; }

    /// <summary>
    /// Base64-encoded per-app shared secret sent as the <c>X-Sbx-App-Key</c> header on every
    /// app-facing request. SECRET — never logged, never included in an exception message, and
    /// never rendered by <see cref="ToString"/>. An empty string denotes the KEYLESS dev path
    /// (the gateway running with <c>AUTH_ENFORCE=off</c>): no <c>X-Sbx-App-Key</c> header is sent
    /// and no base64 validation is performed. A non-blank value is always validated at construction.
    /// </summary>
    public string ClientSecret { get; }

    /// <summary>
    /// Upper bound on how long a remote gateway operation (a command execution) is allowed to run.
    /// This is a GATEWAY-side deadline communicated to the gateway; it is distinct from
    /// <see cref="TransportTimeout"/>, the SDK's own client-side HTTP call deadline. Consumed by
    /// <see cref="SandboxClient.ExecuteAsync"/> as the operation's <c>timeout_secs</c> (in whole
    /// seconds) and as the client-side ceiling for polling the operation to a terminal state.
    /// </summary>
    public TimeSpan ExecutionTimeout { get; }

    /// <summary>
    /// Upper bound on a single client-side HTTP call to the gateway — headers and body. Elapsing this
    /// deadline raises a <see cref="SandboxException"/> with <see cref="SandboxErrorKind.TransportTimeout"/>
    /// — it does NOT guarantee the gateway aborts whatever remote operation was in flight; the
    /// gateway may still complete (or continue running) the request after the client gives up
    /// waiting for its response.
    /// </summary>
    public TimeSpan TransportTimeout { get; }

    /// <summary>
    /// Explicit development-only opt-in to allow a non-HTTPS <see cref="ServerAddress"/> for a
    /// non-loopback host. Loopback addresses (<c>127.0.0.1</c>, <c>::1</c>, <c>localhost</c>) are
    /// always allowed over plain HTTP regardless of this flag — every other host requires HTTPS
    /// unless this is explicitly set to <c>true</c>. Defaults to <c>false</c> so a production
    /// misconfiguration (a remote plaintext address) fails fast at construction instead of silently
    /// sending credentials in the clear.
    /// </summary>
    public bool AllowInsecureDevelopmentTransport { get; }

    public SandboxClientOptions(
        Uri serverAddress,
        string appId,
        string clientSecret,
        TimeSpan executionTimeout,
        TimeSpan transportTimeout,
        bool allowInsecureDevelopmentTransport = false
    )
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        if (!serverAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("Sandbox server address must be an absolute URI.", nameof(serverAddress));
        }

        if (
            !string.Equals(serverAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(serverAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ArgumentException(
                $"Sandbox server address must use the 'http' or 'https' scheme, not '{serverAddress.Scheme}'.",
                nameof(serverAddress)
            );
        }

        if (
            !string.Equals(serverAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !serverAddress.IsLoopback
            && !allowInsecureDevelopmentTransport
        )
        {
            throw new ArgumentException(
                "Sandbox server address must use HTTPS for a non-loopback host. Set "
                    + $"{nameof(allowInsecureDevelopmentTransport)} to true to explicitly allow plaintext "
                    + "HTTP for local development only.",
                nameof(serverAddress)
            );
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        // A blank/null secret is the KEYLESS dev path (AUTH_ENFORCE=off): store empty and skip
        // validation so no X-Sbx-App-Key header is ever sent. A NON-blank secret is still validated
        // so a genuinely malformed key fails fast at startup.
        var keyless = string.IsNullOrWhiteSpace(clientSecret);
        if (!keyless)
        {
            ValidateClientSecretOrThrow(appId, clientSecret);
        }

        if (executionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(executionTimeout), executionTimeout, "Execution timeout must be positive.");
        }

        if (transportTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(transportTimeout), transportTimeout, "Transport timeout must be positive.");
        }

        ServerAddress = serverAddress;
        AppId = appId;
        ClientSecret = keyless ? string.Empty : clientSecret;
        ExecutionTimeout = executionTimeout;
        TransportTimeout = transportTimeout;
        AllowInsecureDevelopmentTransport = allowInsecureDevelopmentTransport;
    }

    /// <summary>
    /// Redacted rendering — never prints <see cref="ClientSecret"/>. Structured logging of these
    /// options (e.g. <c>logger.LogDebug("options={Options}", options)</c>) is therefore safe by
    /// construction rather than relying on callers to remember not to log the secret separately.
    /// </summary>
    public override string ToString() =>
        $"SandboxClientOptions {{ ServerAddress = {ServerAddress}, AppId = {AppId}, "
            + $"ClientSecret = [REDACTED], ExecutionTimeout = {ExecutionTimeout}, "
            + $"TransportTimeout = {TransportTimeout}, "
            + $"AllowInsecureDevelopmentTransport = {AllowInsecureDevelopmentTransport} }}";

    /// <summary>
    /// Validates that a NON-blank <paramref name="clientSecret"/> decodes as STANDARD base64 (never
    /// URL-safe) to at least <see cref="MinClientSecretBytes"/> bytes. Every failure message includes
    /// only <paramref name="appId"/> and (on a length failure) the decoded byte count — never the
    /// secret or any substring of it. A blank secret is the keyless dev path and is handled by the
    /// caller before this method is reached.
    /// </summary>
    private static void ValidateClientSecretOrThrow(string appId, string clientSecret)
    {
        byte[] decoded;
        try
        {
            // Standard base64 only — Convert.FromBase64String rejects the URL-safe alphabet
            // ('-'/'_' in place of '+'/'/'), which is exactly the rejection callers want.
            decoded = Convert.FromBase64String(clientSecret);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                $"Sandbox client secret for app '{appId}' is not valid standard base64 (URL-safe base64 "
                    + "is not accepted). (The secret value itself is never included in this message.)",
                nameof(clientSecret),
                ex
            );
        }

        if (decoded.Length < MinClientSecretBytes)
        {
            throw new ArgumentException(
                $"Sandbox client secret for app '{appId}' decodes to {decoded.Length} byte(s); at least "
                    + $"{MinClientSecretBytes} are required. (The secret value itself is never included in this message.)",
                nameof(clientSecret)
            );
        }
    }
}
