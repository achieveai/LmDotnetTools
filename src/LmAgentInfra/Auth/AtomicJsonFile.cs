using System.Text;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Shared atomic JSON-file write: serialize + stage to a temp file + <see cref="File.Move(string, string, bool)"/>
/// over the target, so a concurrent reader never observes a partially-written file. Extracted so both
/// <see cref="FileOAuthTokenStore"/> and <c>PredefinedKeyRegistry</c> use one implementation of the
/// tricky part. Callers own their own locking (both hold a per-store gate) and their own reads.
/// </summary>
internal static class AtomicJsonFile
{
    /// <summary>
    /// Serializes <paramref name="value"/> to <paramref name="filePath"/> atomically, creating the
    /// parent directory if needed.
    /// </summary>
    public static async Task WriteAsync<T>(
        string filePath,
        T value,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(value, options);

        // Unique same-directory staging file (not a fixed "<file>.tmp") so concurrent writers / processes
        // never collide, and delete it if the write or move fails so a secret-bearing partial file is never
        // left behind on a cancelled/failed write.
        var tempFilePath = filePath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempFilePath, json, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(tempFilePath, filePath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch
            {
                // Best-effort cleanup of the staging file; surface the original failure below.
            }

            throw;
        }
    }
}
