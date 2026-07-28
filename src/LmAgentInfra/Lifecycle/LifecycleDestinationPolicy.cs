namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// Why a callback destination is not authorized, or <see cref="Allowed"/> when it is.
/// </summary>
internal enum LifecycleDestinationVerdict
{
    /// <summary>The destination satisfies every egress rule.</summary>
    Allowed,

    /// <summary>Null, relative, or otherwise not an absolute URL.</summary>
    NotAbsolute,

    /// <summary>A scheme other than http or https.</summary>
    UnsupportedScheme,

    /// <summary>Carries <c>user:pass@</c> credentials in the URL.</summary>
    CarriesUserInfo,

    /// <summary>Plaintext while <see cref="LifecycleDeliveryOptions.RequireHttpsCallbacks"/> is set.</summary>
    NotHttps,

    /// <summary>The host is absent from <see cref="LifecycleDeliveryOptions.AllowedCallbackHosts"/>.</summary>
    HostNotAllowed,
}

/// <summary>
/// The one place the egress rules for a callback destination are decided.
/// <para>
/// These rules are applied at three separate moments — registration, enqueue, and every delivery
/// attempt (ADR 0005) — and the whole point of re-checking is that an operator who narrows
/// <see cref="LifecycleDeliveryOptions.AllowedCallbackHosts"/> to contain an incident sees it take
/// effect on in-flight and retrying deliveries. That guarantee only holds if all three moments
/// decide identically, so they share this evaluation rather than each carrying a copy: a rule that
/// existed in one place and not another would make the narrowing partial, which is indistinguishable
/// from it not having worked.
/// </para>
/// </summary>
internal static class LifecycleDestinationPolicy
{
    /// <summary>
    /// Evaluates <paramref name="callbackUri"/> against the configured egress rules.
    /// </summary>
    /// <param name="callbackUri">Destination to check. Null is a verdict, not an exception —
    /// <c>required</c> does not survive deserialization of a malformed body.</param>
    /// <param name="options">The egress configuration in force <i>now</i>, which is not necessarily
    /// the configuration that admitted the subscription.</param>
    /// <returns>The reason the destination is refused, or <see cref="LifecycleDestinationVerdict.Allowed"/>.</returns>
    public static LifecycleDestinationVerdict Evaluate(
        Uri? callbackUri,
        LifecycleDeliveryOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        // Null despite the `required` declaration is reachable from a deserialized body, and a
        // NullReferenceException is a far worse answer to a malformed request than a rejection.
        if (callbackUri is null || !callbackUri.IsAbsoluteUri)
        {
            return LifecycleDestinationVerdict.NotAbsolute;
        }

        // Scheme is settled before anything else looks at the URL. The host allow-list below
        // constrains only the host, so without this a `file:` or custom-scheme callback would pass
        // an allow-listed host check and reach the delivery client.
        if (!IsHttps(callbackUri) && !IsScheme(callbackUri, Uri.UriSchemeHttp))
        {
            return LifecycleDestinationVerdict.UnsupportedScheme;
        }

        // `https://user:pass@host/` is refused outright rather than stripped. Credentials in a URL
        // become credentials in the host's own storage, they leak into anything that logs a URL, and
        // a redirect hands them to whatever host answers next — which is not the host that was
        // allow-listed.
        if (callbackUri.UserInfo.Length > 0)
        {
            return LifecycleDestinationVerdict.CarriesUserInfo;
        }

        if (options.RequireHttpsCallbacks && !IsHttps(callbackUri))
        {
            return LifecycleDestinationVerdict.NotHttps;
        }

        return IsAllowedHost(callbackUri.Host, options)
            ? LifecycleDestinationVerdict.Allowed
            : LifecycleDestinationVerdict.HostNotAllowed;
    }

    /// <summary>
    /// Whether <paramref name="callbackUri"/> may be dispatched to under the configuration in force
    /// now. Used on the delivery path, where the reason is a log line rather than a caller-facing
    /// rejection.
    /// </summary>
    public static bool IsAuthorized(Uri? callbackUri, LifecycleDeliveryOptions options) =>
        Evaluate(callbackUri, options) == LifecycleDestinationVerdict.Allowed;

    /// <summary>
    /// The identity a quarantine is held against: scheme, host, and port, lower-cased, with the path
    /// and query deliberately discarded.
    /// <para>
    /// Quarantine tracks the <i>endpoint that is down</i>, not the URL that happened to be dialled.
    /// Keying on the full URL would let a subscriber walk away from a quarantine by appending a
    /// query string, and keying on the host alone would let one dead port take down a healthy
    /// service on another.
    /// </para>
    /// </summary>
    public static string DestinationKey(Uri callbackUri)
    {
        ArgumentNullException.ThrowIfNull(callbackUri);

        // IdnHost, not Host, so a unicode hostname and its punycode spelling are one destination
        // rather than two — otherwise re-registering under the other spelling escapes the quarantine.
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{callbackUri.Scheme.ToLowerInvariant()}://{callbackUri.IdnHost.ToLowerInvariant()}:{callbackUri.Port}"
        );
    }

    /// <summary>
    /// Whether the configured allow-list admits <paramref name="host"/>. An empty (or absent)
    /// list admits nothing, which is what keeps delivery fail-closed: enabling the feature without
    /// naming a destination yields refused registrations, not an open outbound relay.
    /// </summary>
    private static bool IsAllowedHost(string host, LifecycleDeliveryOptions options) =>
        // OrdinalIgnoreCase because DNS names are case-insensitive; a host that differs only in case
        // is the same machine, and refusing it would be a configuration trap, not a security gain.
        options.AllowedCallbackHosts is { Length: > 0 } allowed
        && allowed.Contains(host, StringComparer.OrdinalIgnoreCase);

    private static bool IsHttps(Uri uri) => IsScheme(uri, Uri.UriSchemeHttps);

    private static bool IsScheme(Uri uri, string scheme) =>
        string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase);
}
