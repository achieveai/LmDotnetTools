using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeReviewDaemon.Sample.Agents;

namespace Reviewd.Sample;

/// <summary>
///     One A/B arm's manifest: every lever a review depends on, pinned (docs/plans/2026-11-16-reviewer-evolution-ab.md,
///     "How an arm is built"). Paths in it are relative to the manifest's folder. Its SHA-256 goes in every receipt of
///     the arm, so a result always names exactly what produced it.
/// </summary>
internal sealed partial record VariantManifest(
    string Id,
    string Sha256,
    string Path,
    string? ReviewRepo,
    string? ReviewRepoRef,
    string? ReviewRepoSha,
    string? ReviewRepoDirectory,
    string? PluginAlias,
    string? PluginUrl,
    string? PluginSha,
    string? ModeName,
    string? ModeFile,
    string? TaskPromptFile,
    string? Provider,
    string? Effort
)
{
    internal static async Task<VariantManifest> LoadAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var root =
            JsonNode.Parse(bytes) as JsonObject
            ?? throw new ArgumentException($"Variant manifest {path} is not a JSON object.");
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        string? Text(JsonNode? node, string name, string section = "") =>
            node?[name] switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) && text.Length > 0 => text,
                _ => throw new ArgumentException(
                    $"Variant manifest {path}: {section}{name} must be a non-empty string."
                ),
            };
        string? Sha(JsonNode? node, string section) =>
            Text(node, "sha", section) is { } sha && !CommitSha().IsMatch(sha)
                ? throw new ArgumentException(
                    $"Variant manifest {path}: {section}sha must be a 40-character commit SHA."
                )
                : Text(node, "sha", section);
        string? Local(string? relative) =>
            relative is null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, relative));

        var id = Text(root, "id") ?? throw new ArgumentException($"Variant manifest {path} has no id.");
        if (!CliOptions.VariantLabel.IsMatch(id))
        {
            throw new ArgumentException($"Variant manifest {path}: id '{id}' is not a variant label.");
        }
        var effort = Text(root, "effort");
        if (effort is not (null or "low" or "medium" or "high" or "xhigh"))
        {
            throw new ArgumentException($"Variant manifest {path}: effort must be low, medium, high or xhigh.");
        }
        var reviewRepo = root["reviewRepo"];
        var plugins = root["plugins"];
        var mode = root["mode"];
        return new VariantManifest(
            id,
            Sha256Hex(bytes),
            System.IO.Path.GetFullPath(path),
            Text(reviewRepo, "repo"),
            Text(reviewRepo, "ref"),
            Sha(reviewRepo, "reviewRepo."),
            Text(reviewRepo, "directory", "reviewRepo."),
            Text(plugins, "alias"),
            Text(plugins, "url"),
            Sha(plugins, "plugins."),
            Text(mode, "name", "mode."),
            Local(Text(mode, "file")),
            Local(Text(root, "taskPrompt")),
            Text(root, "provider"),
            effort
        );
    }

    /// <summary>
    ///     The chat mode the manifest pins, as <c>variant up</c> sends it: the mode file, named <see cref="ModeName"/>,
    ///     with every field it omits set to the host's create default.
    /// </summary>
    internal async Task<JsonObject> ModePayloadAsync(CancellationToken ct) =>
        ModePayload(await File.ReadAllBytesAsync(ModeFile!, ct));

    /// <summary>
    ///     <see cref="ModePayloadAsync"/> from the mode file's bytes already read, so a hash of the same bytes names
    ///     exactly this payload.
    /// </summary>
    internal JsonObject ModePayload(byte[] bytes)
    {
        var mode = LmStreamingS2SClient.ChatModeReplacement(
            JsonNode.Parse(bytes) as JsonObject
                ?? throw new ArgumentException($"Mode file {ModeFile} is not a JSON object.")
        );
        if (mode["name"] is { } named && named.ToString() != ModeName)
        {
            throw new ArgumentException(
                $"Mode file {ModeFile} is named '{named}', but the manifest pins mode {ModeName}."
            );
        }
        mode["name"] = ModeName;
        return mode;
    }

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();
}
