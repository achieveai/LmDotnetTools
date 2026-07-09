namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Per-app credential the sandbox gateway requires on every app-facing call (ADR 0029), sent as
/// the <c>X-Sbx-App-Id</c> and <c>X-Sbx-App-Key</c> headers. A pure value type — no ASP.NET types,
/// no logging — so it can be threaded through <see cref="LmAgentInfra"/>'s singleton services as a
/// plain value without pulling in any host-specific dependency.
/// </summary>
/// <param name="AppId">App identifier sent alongside the key.</param>
/// <param name="AppKey">Base64-encoded app secret. SECRET — never log this value or include it in
/// any exception message.</param>
public readonly record struct SandboxCredential(string AppId, string AppKey)
{
    /// <summary>Minimum number of decoded bytes an app key must contain.</summary>
    private const int MinKeyBytes = 32;

    /// <summary>Header the gateway reads the caller's app identity from (ADR 0029).</summary>
    public const string AppIdHeader = "X-Sbx-App-Id";

    /// <summary>Header the gateway reads the caller's app secret from (ADR 0029). SECRET.</summary>
    public const string AppKeyHeader = "X-Sbx-App-Key";

    /// <summary>
    /// Redacted rendering — the auto-generated <c>record struct</c> <see cref="object.ToString"/>
    /// would otherwise print every positional member, including <see cref="AppKey"/>. Overriding it
    /// makes accidental leakage via structured logging (e.g. <c>logger.LogDebug("cred={Cred}", cred)</c>)
    /// structurally impossible rather than relying on the never-log social contract.
    /// </summary>
    public override string ToString() => $"SandboxCredential {{ AppId = {AppId}, AppKey = [REDACTED] }}";

    /// <summary>
    /// Stamps <see cref="AppIdHeader"/> (and <see cref="AppKeyHeader"/> when the key is non-blank)
    /// onto <paramref name="headers"/>. The key header is omitted entirely on the keyless
    /// <c>AUTH_ENFORCE=off</c> dev path so an empty value never reaches the gateway. Single home for
    /// the "stamp id, conditionally stamp key" rule shared by every transport.
    /// </summary>
    public void StampHeaders(IDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        headers[AppIdHeader] = AppId;
        if (!string.IsNullOrEmpty(AppKey))
        {
            headers[AppKeyHeader] = AppKey;
        }
    }

    /// <summary>
    /// Stamps <see cref="AppIdHeader"/> (and <see cref="AppKeyHeader"/> when the key is non-blank)
    /// onto <paramref name="request"/> via <see cref="System.Net.Http.Headers.HttpHeaders.TryAddWithoutValidation(string,string)"/>.
    /// Per-request only — never touches <c>HttpClient.DefaultRequestHeaders</c>, which is process-global
    /// and would leak one caller's credential onto every other concurrent caller's request.
    /// </summary>
    public void StampHeaders(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation(AppIdHeader, AppId);
        if (!string.IsNullOrEmpty(AppKey))
        {
            request.Headers.TryAddWithoutValidation(AppKeyHeader, AppKey);
        }
    }

    /// <summary>
    /// Resolves the default credential from <paramref name="options"/>. Returns <c>null</c> when
    /// <see cref="SandboxGatewayOptions.AppKey"/> is unset/blank — the keyless dev path used when
    /// the gateway runs with <c>AUTH_ENFORCE=off</c>. When a key is configured it is validated via
    /// <see cref="ValidateKeyOrThrow"/> before the credential is returned.
    /// </summary>
    public static SandboxCredential? FromOptions(SandboxGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.AppKey))
        {
            return null;
        }

        ValidateKeyOrThrow(options.AppId, options.AppKey);
        return new SandboxCredential(options.AppId, options.AppKey);
    }

    /// <summary>
    /// Validates that <paramref name="appKey"/> decodes as STANDARD base64 (never URL-safe) to at
    /// least <see cref="MinKeyBytes"/> bytes. Throws <see cref="ArgumentException"/> with a
    /// REDACTED message — <paramref name="appId"/> and the decoded byte length only, NEVER the key
    /// or its contents — on failure.
    /// </summary>
    /// <param name="appId">App id the key belongs to; included in the error message for
    /// diagnosability.</param>
    /// <param name="appKey">Candidate base64-encoded app secret. SECRET — never included in any
    /// exception message this method throws.</param>
    public static void ValidateKeyOrThrow(string appId, string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            throw new ArgumentException(
                $"Sandbox app key for app '{appId}' is missing or blank. "
                    + "(The key value itself is never included in this message.)",
                nameof(appKey)
            );
        }

        byte[] decoded;
        try
        {
            // Standard base64 only — Convert.FromBase64String rejects the URL-safe alphabet
            // ('-'/'_' in place of '+'/'/'), which is exactly the rejection the caller wants.
            decoded = Convert.FromBase64String(appKey);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                $"Sandbox app key for app '{appId}' is not valid standard base64 (URL-safe base64 "
                    + "is not accepted). (The key value itself is never included in this message.)",
                nameof(appKey),
                ex
            );
        }

        if (decoded.Length < MinKeyBytes)
        {
            throw new ArgumentException(
                $"Sandbox app key for app '{appId}' decodes to {decoded.Length} byte(s); at least "
                    + $"{MinKeyBytes} are required. (The key value itself is never included in this message.)",
                nameof(appKey)
            );
        }
    }
}
