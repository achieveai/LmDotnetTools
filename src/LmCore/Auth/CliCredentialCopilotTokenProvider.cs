using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmCore.Auth;

/// <summary>
///     Resolves a GitHub OAuth token from credentials already present on the machine, in priority order:
///     <list type="number">
///         <item>environment variables (<c>GITHUB_COPILOT_TOKEN</c>, <c>GH_COPILOT_TOKEN</c>, <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>);</item>
///         <item>the GitHub Copilot CLI config (<c>~/.copilot/config.json</c>), which stores its own GitHub token;</item>
///         <item>the GitHub Copilot editor credential files (<c>apps.json</c> / <c>hosts.json</c>) under the user config dir;</item>
///         <item>the <c>gh</c> CLI hosts file (<c>hosts.yml</c>);</item>
///         <item>the <c>gh auth token</c> command.</item>
///     </list>
///     This is a zero-setup path for users already signed in to Copilot or the <c>gh</c> CLI.
/// </summary>
public sealed partial class CliCredentialCopilotTokenProvider : ICopilotTokenProvider
{
    private static readonly string[] EnvVarNames =
    [
        "GITHUB_COPILOT_TOKEN",
        "GH_COPILOT_TOKEN",
        "GH_TOKEN",
        "GITHUB_TOKEN",
    ];

    // The Copilot CLI's config.json is JSONC (leading // comments, possible trailing commas).
    private static readonly JsonDocumentOptions LenientJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <inheritdoc />
    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = ResolveToken();
        return token is not null
            ? Task.FromResult(token)
            : throw new InvalidOperationException(
                "No GitHub Copilot token found. Set GITHUB_COPILOT_TOKEN (or GH_TOKEN), sign in with the "
                    + "GitHub Copilot CLI / `gh auth login`, or use a device-flow token provider."
            );
    }

    /// <summary>Attempts to resolve a token without throwing. Returns null when none is found.</summary>
    public string? ResolveToken()
    {
        // 1. Environment variables.
        foreach (var name in EnvVarNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        // 2. GitHub Copilot CLI config — holds the CLI's own (Copilot-entitled) GitHub token.
        foreach (var path in CopilotCliConfigFiles())
        {
            var token = TryScanGitHubTokenFromJson(path);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        // 3. GitHub Copilot editor credential files (apps.json / hosts.json).
        foreach (var path in CopilotEditorCredentialFiles())
        {
            var token = TryReadOAuthTokenFromJson(path);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        // 4. gh CLI hosts.yml (YAML).
        foreach (var path in GhCliHostsYamlFiles())
        {
            var token = TryReadOAuthTokenFromYaml(path);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        // 5. gh auth token (shell out, last resort).
        return TryGetTokenFromGhCli();
    }

    private static IEnumerable<string> ConfigDirectories()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            yield return xdg;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".config");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return localAppData;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return appData;
        }
    }

    private static IEnumerable<string> CopilotCliConfigFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".copilot", "config.json");
        }
    }

    private static IEnumerable<string> CopilotEditorCredentialFiles()
    {
        foreach (var dir in ConfigDirectories())
        {
            yield return Path.Combine(dir, "github-copilot", "apps.json");
            yield return Path.Combine(dir, "github-copilot", "hosts.json");
            yield return Path.Combine(dir, "gh", "hosts.json");
        }
    }

    private static IEnumerable<string> GhCliHostsYamlFiles()
    {
        foreach (var dir in ConfigDirectories())
        {
            yield return Path.Combine(dir, "gh", "hosts.yml");
            yield return Path.Combine(dir, "GitHub CLI", "hosts.yml");
        }
    }

    /// <summary>
    ///     Reads a GitHub OAuth token from a Copilot/gh JSON credential file. The files map a host
    ///     key (e.g. <c>github.com</c> or <c>github.com:Iv1.xxxx</c>) to an object containing
    ///     <c>oauth_token</c>.
    /// </summary>
    public static string? TryReadOAuthTokenFromJson(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path), LenientJsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!entry.Name.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Value.ValueKind == JsonValueKind.Object
                    && entry.Value.TryGetProperty("oauth_token", out var tokenEl)
                    && tokenEl.ValueKind == JsonValueKind.String)
                {
                    var token = tokenEl.GetString();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token.Trim();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Ignore unreadable/malformed credential files and fall through to the next candidate.
        }

        return null;
    }

    /// <summary>
    ///     Recursively scans a JSON file for the first string value that looks like a GitHub token
    ///     (<c>gho_</c>/<c>ghu_</c>/<c>ghp_</c>/<c>ghs_</c>/<c>ghr_</c> prefix). Used for the Copilot
    ///     CLI config, which embeds its token without a fixed, documented key path.
    /// </summary>
    public static string? TryScanGitHubTokenFromJson(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path), LenientJsonOptions);
            return FindGitHubToken(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? FindGitHubToken(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            return value is not null && GitHubTokenRegex().IsMatch(value) ? value : null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var found = FindGitHubToken(property.Value);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindGitHubToken(item);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Reads the first <c>oauth_token:</c> value from a <c>gh</c> CLI <c>hosts.yml</c> file using a
    ///     line-based parse (avoids taking a YAML dependency for one field).
    /// </summary>
    public static string? TryReadOAuthTokenFromYaml(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("oauth_token:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed["oauth_token:".Length..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore unreadable files.
        }

        return null;
    }

    private static string? TryGetTokenFromGhCli()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gh",
                    Arguments = "auth token",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited.
                }

                return null;
            }

            var token = output.Trim();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(token) ? token : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // gh not installed / not on PATH.
            return null;
        }
    }

    [GeneratedRegex(@"^gh[oupsr]_[A-Za-z0-9]{20,}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();
}
