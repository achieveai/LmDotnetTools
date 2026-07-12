using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox.Transfer;

/// <summary>
/// Pure helpers for the workspace-relative POSIX paths a transfer operates on: the stable hex key a
/// script's marker line carries (never the raw path — see <see cref="TransferScripts"/>), the sibling
/// temp / listing-artifact relative paths, and the NUL-delimited listing split.
/// </summary>
/// <remarks>
/// A transfer path is validated exactly like a command working directory —
/// via <see cref="WorkspaceRelativePath.Normalize(string?, string)"/>, which rejects rooted,
/// drive/UNC/device-qualified, backslash-bearing, and <c>..</c>-escaping inputs (and NUL). The lexical
/// checks are input validation only; the gateway stays authoritative for symlink containment.
/// </remarks>
internal static class TransferPath
{
    /// <summary>Reserved per-session directory for transient listing artifacts, under the workspace root.</summary>
    public const string ArtifactRootRelative = ".lmsbx-sdk/xfer";

    /// <summary>
    /// The stable, injection-safe key for a workspace-relative path: 64 lowercase-hex of its SHA-256. It
    /// is placed on a script's marker comment line so a classifier/test double can identify the target
    /// without a raw (space/newline/quote-bearing) path ever appearing there.
    /// </summary>
    public static string Key(string relativePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>The sibling temp path for an atomic write: <c>&lt;target&gt;.&lt;opId&gt;.tmp</c>, in the target's own directory.</summary>
    public static string TempRelative(string targetRelative, string opId) => $"{targetRelative}.{opId}.tmp";

    /// <summary>The transient listing-artifact path for a directory read-back: <c>.lmsbx-sdk/xfer/list.&lt;opId&gt;</c>.</summary>
    public static string ListArtifactRelative(string opId) => $"{ArtifactRootRelative}/list.{opId}";

    /// <summary>A fresh, collision-resistant per-operation id (32 lowercase hex) for a temp/artifact name.</summary>
    public static string NewOperationId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Splits the NUL-delimited listing artifact bytes into entry names. A trailing NUL (every name is
    /// NUL-terminated) does not produce a spurious empty entry; an empty artifact yields no entries.
    /// Names may contain any byte except NUL (including spaces and newlines), so splitting on NUL — not
    /// whitespace — is what makes the listing exact.
    /// </summary>
    public static IReadOnlyList<string> SplitNulListing(string listing)
    {
        if (listing.Length == 0)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var name in listing.Split('\0'))
        {
            if (name.Length != 0)
            {
                names.Add(name);
            }
        }

        return names;
    }
}
