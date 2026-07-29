using System.Globalization;
using System.Net;
using System.Net.Sockets;

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

    /// <summary>
    /// The host resolved only to addresses a callback may not reach — loopback, link-local, or
    /// private space. Distinct from <see cref="HostNotAllowed"/> because the name passed the
    /// allow-list and the <i>address behind it</i> is what was refused, which is the difference
    /// between a configuration mistake and a rebinding attempt.
    /// </summary>
    AddressNotAllowed,
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
    private static readonly IdnMapping Idn = new();

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

        if (!IsAllowedHost(callbackUri, options))
        {
            return LifecycleDestinationVerdict.HostNotAllowed;
        }

        // A literal address can be judged here, before the subscription is ever persisted. A name
        // cannot: what it resolves to is not knowable until the moment of connection, which is why
        // the same address rule is applied again on every connect (see IsAllowedAddress).
        return IPAddress.TryParse(callbackUri.Host, out var literal)
            && !IsAllowedAddress(literal, options)
            ? LifecycleDestinationVerdict.AddressNotAllowed
            : LifecycleDestinationVerdict.Allowed;
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
    /// Whether the configured allow-list admits <paramref name="callbackUri"/>'s host. An empty (or
    /// absent) list admits nothing, which is what keeps delivery fail-closed: enabling the feature
    /// without naming a destination yields refused registrations, not an open outbound relay.
    /// <para>
    /// Both sides are reduced to punycode first, for the same reason <see cref="DestinationKey"/>
    /// normalizes to it: <c>ünicode.example</c> and <c>xn--nicode-hva.example</c> are one machine.
    /// Comparing the spellings as written would make an allow-list entry admit or refuse the same
    /// destination depending on how the subscriber happened to type it — and would leave a
    /// destination the quarantine treats as one endpoint looking like two to the allow-list.
    /// Normalizing only the URL is not enough, because .NET reduces a unicode host to punycode but
    /// never expands a punycode host back: an entry written in unicode would then admit only the
    /// unicode spelling.
    /// </para>
    /// </summary>
    private static bool IsAllowedHost(Uri callbackUri, LifecycleDeliveryOptions options)
    {
        if (options.AllowedCallbackHosts is not { Length: > 0 } allowed)
        {
            return false;
        }

        // OrdinalIgnoreCase because DNS names are case-insensitive; a host that differs only in case
        // is the same machine, and refusing it would be a configuration trap, not a security gain.
        return Array.Exists(
            allowed,
            entry =>
                string.Equals(
                    ToPunycode(entry),
                    callbackUri.IdnHost,
                    StringComparison.OrdinalIgnoreCase
                )
                // The entry as written, for the case where it is not a name punycode applies to. An
                // entry that cannot be canonicalized is a configuration mistake, and letting it match
                // itself keeps that mistake from being reported as a rebinding refusal.
                || string.Equals(entry, callbackUri.Host, StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// An allow-list entry in the spelling <see cref="Uri.IdnHost"/> reports, or the entry unchanged
    /// when it is not a name that can be canonicalized.
    /// </summary>
    private static string ToPunycode(string host)
    {
        try
        {
            return Idn.GetAscii(host);
        }
        catch (ArgumentException)
        {
            // Empty, over-long, or otherwise malformed. This method is called from a check whose
            // whole job is to refuse things, so it answers rather than throwing: the caller's
            // literal comparison gets the final word, and a bad entry simply matches nothing.
            return host;
        }
    }

    /// <summary>
    /// Whether a resolved address may actually be dialled.
    /// </summary>
    /// <param name="address">One address the callback host resolved to.</param>
    /// <param name="options">The egress configuration in force now.</param>
    /// <returns><see langword="true"/> when the address is a legitimate outbound destination.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why a name check is not enough.</b> <see cref="Evaluate"/> authorizes a <i>name</i>, and
    /// the mapping from a name to an address belongs to whoever controls that name's DNS — including,
    /// for a subscriber-supplied host, the subscriber. An allow-listed name repointed at
    /// <c>169.254.169.254</c> or at a service on the host's own loopback would otherwise be dialled
    /// with a signed body containing conversation content. Checking the address is what makes the
    /// allow-list a statement about a machine rather than about a string.
    /// </para>
    /// <para>
    /// IPv4-mapped IPv6 addresses are unwrapped first. <c>::ffff:127.0.0.1</c> is loopback, and a
    /// check that tested the mapped form directly would answer that it is not.
    /// </para>
    /// </remarks>
    public static bool IsAllowedAddress(IPAddress address, LifecycleDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(options);

        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        // Never a callback destination under any configuration, so these are not covered by the
        // development escape hatch below: nothing answers on the unspecified address, and a
        // multicast delivery is a delivery to an unknown set of listeners.
        if (
            candidate.Equals(IPAddress.Any)
            || candidate.Equals(IPAddress.IPv6Any)
            || IsMulticast(candidate)
        )
        {
            return false;
        }

        return options.AllowPrivateCallbackAddresses || !IsPrivate(candidate);
    }

    /// <summary>
    /// Whether an address belongs to space a callback should not reach: the host itself, its link,
    /// or the private network it sits on. This is the SSRF surface — cloud metadata endpoints,
    /// sidecars, and internal admin services all live here.
    /// </summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }

        // Unique local addresses, fc00::/7 — the IPv6 counterpart of the RFC 1918 ranges below.
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            // An address family this policy cannot reason about is not one it will vouch for.
            return true;
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true, // 10.0.0.0/8
            127 => true, // 127.0.0.0/8, beyond the single loopback address checked above
            169 => octets[1] == 254, // 169.254.0.0/16 link-local, which is where cloud metadata lives
            172 => octets[1] is >= 16 and <= 31, // 172.16.0.0/12
            192 => octets[1] == 168, // 192.168.0.0/16
            100 => octets[1] is >= 64 and <= 127, // 100.64.0.0/10 carrier-grade NAT
            _ => false,
        };
    }

    private static bool IsMulticast(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? address.IsIPv6Multicast
            : (address.GetAddressBytes()[0] & 0xF0) == 0xE0; // 224.0.0.0/4

    private static bool IsHttps(Uri uri) => IsScheme(uri, Uri.UriSchemeHttps);

    private static bool IsScheme(Uri uri, string scheme) =>
        string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase);
}
