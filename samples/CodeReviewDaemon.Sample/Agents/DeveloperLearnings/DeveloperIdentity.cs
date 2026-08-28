using System.Security.Cryptography;
using System.Text;

namespace CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

/// <summary>
/// Turns a PR author into the directory name their learnings live under.
/// <para>
/// Moved verbatim from <c>ReviewFeedbackAgent</c> rather than re-derived. The slug is a PATH SEGMENT, so
/// its character set is a security boundary and not a formatting choice: the builder below emits only
/// <c>[a-z0-9-]</c>, which makes <c>../</c> unconstructible rather than filtered. Re-deriving it would have
/// meant re-deriving that property too, and a second implementation is a second chance to get it wrong.
/// </para>
/// </summary>
internal static class DeveloperIdentity
{
    /// <summary>
    /// Bytes of the SHA-256 digest kept in the fingerprint suffix — enough that a collision between two real
    /// author identities is not a practical concern, short enough that the directory name stays readable.
    /// </summary>
    private const int FingerprintBytes = 6;

    /// <summary>
    /// The developer's directory name, or null when there is no addressable identity to record under.
    /// <para>
    /// Null has three causes and they are deliberately collapsed, because the caller's response to all
    /// three is the same and it is not an error: a missing author, a <c>[bot]</c> identity, and a name with
    /// no alphanumeric content at all. None of them can be retried into an identity the provider never
    /// reported.
    /// </para>
    /// </summary>
    public static string? SlugifyAuthor(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return null;
        }

        var trimmed = author.Trim();
        if (trimmed.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var builder = new StringBuilder(trimmed.Length);
        var pendingHyphen = false;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    _ = builder.Append('-');
                }

                _ = builder.Append(char.ToLowerInvariant(ch));
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var slug = builder.ToString();
        return slug.Length == 0 ? null : slug + "-" + IdentityFingerprint(trimmed);
    }

    /// <summary>
    /// Distinguishes two authors whose display names slugify identically. Lower-cased before hashing so the
    /// same person reported with different casing by two providers lands in one directory rather than two.
    /// </summary>
    private static string IdentityFingerprint(string identity)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant()));
        return Convert.ToHexStringLower(digest.AsSpan(0, FingerprintBytes));
    }
}
