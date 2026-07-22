using System.Security.Cryptography;
using System.Text;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Derives the per-app workspace directory the sandbox gateway roots every workspace under
/// (SandboxedOstoolsMcpServer <c>app_dir.rs</c>, ADR 0028): the gateway maps a create request's relative
/// <c>workspace</c> field to <c>WORKSPACE_BASE_PATH/&lt;app-dir&gt;/&lt;workspace&gt;</c>. The daemon must
/// prepare its pooled store — and express slot paths relative to a base that already includes
/// <c>&lt;app-dir&gt;</c> — so the (app-dir-less) <c>workspace</c> field it sends re-roots to the prepared
/// store rather than an empty, freshly-created directory.
/// </summary>
internal static class SandboxAppDir
{
    /// <summary>
    /// <c>&lt;slug&gt;-&lt;shorthash&gt;</c>, mirroring the gateway exactly: <c>slug</c> is <paramref name="appId"/>
    /// lower-cased and filtered to <c>[a-z0-9._-]</c> (truncated to 32 chars); <c>shorthash</c> is the first
    /// 16 hex chars of <c>SHA-256(appId)</c> computed on the RAW app id (not the slug).
    /// </summary>
    public static string Derive(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        char[] slugChars =
        [
            .. appId.ToLowerInvariant()
                .Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'),
        ];
        var slug = new string(slugChars);
        if (slug.Length > 32)
        {
            slug = slug[..32];
        }

        var shortHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appId)))
            .ToLowerInvariant()[..16];

        return $"{slug}-{shortHash}";
    }

    /// <summary>
    /// The effective host workspace base the daemon prepares under and measures slot paths against:
    /// <c>&lt;configuredBase&gt;/&lt;app-dir&gt;</c> when <paramref name="perAppRooting"/> is on (the gateway
    /// re-adds <c>&lt;app-dir&gt;</c> to the app-dir-less workspace field), else the configured base unchanged
    /// (pre-ADR-0028 flat behavior). Forward-slashed so it round-trips through the gateway consistently.
    /// </summary>
    public static string? EffectiveBase(string? configuredBase, string appId, bool perAppRooting)
    {
        if (!perAppRooting || string.IsNullOrWhiteSpace(configuredBase))
        {
            return configuredBase;
        }

        return $"{configuredBase.TrimEnd('/', '\\')}/{Derive(appId)}";
    }
}
