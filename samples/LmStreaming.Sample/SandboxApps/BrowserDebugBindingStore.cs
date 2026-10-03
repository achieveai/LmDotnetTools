using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace LmStreaming.Sample.SandboxApps;

public sealed class BrowserDebugOptions
{
    public bool Enabled { get; set; }
    public string PreviewOrigin { get; set; } = "https://miniapp-debug.invalid";
    public string GatewayAppId { get; set; } = "lmstreaming-sample";
    public string SigningSecret { get; set; } = "";
}

public sealed record BrowserDebugTarget(SandboxAppDefinition? App, string? HtmlFilePath, string? AssetsRootPath);

public sealed class BrowserDebugBinding
{
    public required string Handle { get; init; }
    public required string BrowserId { get; init; }
    public string? BrowserInstance { get; internal set; }
    public required string ThreadId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public Principal? Principal { get; init; }
    public required BrowserDebugTarget Target { get; init; }
    public required string Prefix { get; init; }
    public required string PreviewUrl { get; init; }
    public required string CsrfToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Live leases and the signed request boundary for their selected targets.</summary>
public sealed class BrowserDebugBindingStore(BrowserDebugOptions options, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BrowserDebugBinding> _bindings = new(StringComparer.Ordinal);

    public string? UnavailableReason =>
        !options.Enabled ? "Browser debugging is disabled in host configuration."
        : !ValidOrigin(options.PreviewOrigin) ? "Browser debugging requires an HTTP(S) preview origin without a path."
        : string.IsNullOrWhiteSpace(options.GatewayAppId) || options.GatewayAppId.Any(char.IsControl)
            ? "Browser debugging requires the gateway app identity."
        : Encoding.UTF8.GetByteCount(options.SigningSecret) < 32
            ? "Browser debugging requires a gateway signing secret of at least 32 bytes."
        : null;

    public BrowserDebugBinding Create(
        string threadId,
        string workspaceId,
        string sessionId,
        Principal? principal,
        BrowserDebugTarget target
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(target);
        if (UnavailableReason is { } reason)
        {
            throw new InvalidOperationException(reason);
        }
        var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var prefix = $"/_debug-browser/{handle}/";
        var entry = target.HtmlFilePath is { } html
            ? html[(string.IsNullOrEmpty(target.AssetsRootPath) ? 0 : target.AssetsRootPath.Length + 1)..]
            : "";
        var binding = new BrowserDebugBinding
        {
            Handle = handle,
            BrowserId = "debug-" + Guid.NewGuid().ToString("N"),
            ThreadId = threadId,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            Principal = principal,
            Target = target,
            Prefix = prefix,
            PreviewUrl =
                new Uri(options.PreviewOrigin).GetLeftPart(UriPartial.Authority)
                + prefix
                + string.Join('/', entry.Split('/').Select(Uri.EscapeDataString)),
            CsrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            ExpiresAt = clock.GetUtcNow() + TimeSpan.FromMinutes(30),
        };
        lock (_gate)
        {
            foreach (var expired in _bindings.Values.Where(b => b.ExpiresAt <= clock.GetUtcNow()).ToArray())
            {
                _bindings.Remove(expired.Handle);
            }
            if (_bindings.Count >= 64)
            {
                throw new InvalidOperationException("Too many active debug previews. Close an existing preview first.");
            }
            _bindings.Add(handle, binding);
        }
        return binding;
    }

    public bool BindInstance(BrowserDebugBinding binding, string browserId, string instance)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            if (!Live(binding.Handle, out var current) || !ReferenceEquals(current, binding))
            {
                return false;
            }
            if (
                browserId != binding.BrowserId
                || string.IsNullOrWhiteSpace(instance)
                || instance.Length > 128
                || instance.Any(char.IsControl)
                || (binding.BrowserInstance is { } previous && instance != previous)
            )
            {
                _bindings.Remove(binding.Handle);
                return false;
            }
            binding.BrowserInstance = instance;
            return true;
        }
    }

    public bool TryGetOwned(
        string handle,
        string threadId,
        string workspaceId,
        string sessionId,
        out BrowserDebugBinding? binding
    )
    {
        lock (_gate)
        {
            if (
                Live(handle, out var current)
                && current!.ThreadId == threadId
                && current.WorkspaceId == workspaceId
                && current.SessionId == sessionId
            )
            {
                binding = current;
                return true;
            }
            binding = null;
            return false;
        }
    }

    public bool TryValidate(HttpRequest request, out BrowserDebugBinding? binding, out string? targetPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        binding = null;
        targetPath = null;
        if (
            UnavailableReason is not null
            || !Header(request, "X-Sbx-Original-Url", out var original)
            || !Header(request, "X-Sbx-App-Id", out var app)
            || !Header(request, "X-Sbx-Browser-Id", out var browser)
            || !Header(request, "X-Sbx-Browser-Instance", out var instance)
            || !Header(request, "X-Sbx-Browser-Signature", out var signature)
            || app != options.GatewayAppId
            || original.Length > 8192
            || !VerifySignature(signature, request.Method, original, app, browser, instance)
            || !Uri.TryCreate(original, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || uri.GetLeftPart(UriPartial.Authority) != new Uri(options.PreviewOrigin).GetLeftPart(UriPartial.Authority)
        )
        {
            return false;
        }

        // Inspect the signed wire path before System.Uri can normalize dot segments away.
        var pathStart = original.IndexOf('/', original.IndexOf("://", StringComparison.Ordinal) + 3);
        if (pathStart < 0)
        {
            return false;
        }
        var queryStart = original.IndexOf('?', pathStart);
        var rawPath = original[pathStart..(queryStart < 0 ? original.Length : queryStart)];
        var query = queryStart < 0 ? "" : original[queryStart..];
        var parts = rawPath.Split('/');
        if (
            rawPath.Length > 2048
            || query.Length > 2048
            || parts.Length < 4
            || parts[1] != "_debug-browser"
            || parts[2].Length != 64
            || !parts[2].All(Uri.IsHexDigit)
        )
        {
            return false;
        }
        var decoded = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var value = Uri.UnescapeDataString(part);
            if (
                value is "." or ".."
                || value.Contains('%')
                || value.Contains('/')
                || value.Contains('\\')
                || value.Any(char.IsControl)
            )
            {
                return false;
            }
            decoded.Add(value);
        }
        if (request.Path.Value != string.Join('/', decoded) || request.QueryString.Value != query)
        {
            return false;
        }
        lock (_gate)
        {
            if (
                !Live(parts[2], out var current)
                || current!.BrowserId != browser
                || current.BrowserInstance is null
                || current.BrowserInstance != instance
                || !rawPath.StartsWith(current.Prefix, StringComparison.Ordinal)
            )
            {
                return false;
            }
            binding = current;
            targetPath = string.Join('/', decoded.Skip(3));
            return true;
        }
    }

    public void Revoke(BrowserDebugBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            if (_bindings.TryGetValue(binding.Handle, out var current) && ReferenceEquals(current, binding))
            {
                _bindings.Remove(binding.Handle);
            }
        }
    }

    private bool Live(string handle, out BrowserDebugBinding? binding)
    {
        if (_bindings.TryGetValue(handle, out binding) && binding.ExpiresAt > clock.GetUtcNow())
        {
            return true;
        }
        _bindings.Remove(handle);
        binding = null;
        return false;
    }

    private bool VerifySignature(
        string signature,
        string method,
        string original,
        string app,
        string browser,
        string instance
    )
    {
        var parts = signature.Split(',');
        if (
            parts.Length != 2
            || !parts[0].StartsWith("t=", StringComparison.Ordinal)
            || !parts[1].StartsWith("v1=", StringComparison.Ordinal)
            || parts[1].Length != 67
            || !long.TryParse(parts[0].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
        )
        {
            return false;
        }
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (timestamp < now - 300 || timestamp > now + 300)
        {
            return false;
        }
        try
        {
            var message =
                $"v1\n{timestamp.ToString(CultureInfo.InvariantCulture)}\n{method}\n{original}\n{app}\n{browser}\n{instance}";
            var expected = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(options.SigningSecret),
                Encoding.UTF8.GetBytes(message)
            );
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(parts[1][3..]));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool Header(HttpRequest request, string name, out string value)
    {
        var values = request.Headers[name];
        value = values.Count == 1 ? values[0] ?? "" : "";
        return value.Length > 0 && !value.Any(char.IsControl);
    }

    private static bool ValidOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && uri.UserInfo.Length == 0;
}
