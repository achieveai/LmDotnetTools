using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Auth;

public sealed class CliCredentialCopilotTokenProviderTests
{
    [Fact]
    public void TryReadOAuthTokenFromJson_reads_copilot_hosts_shape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-hosts-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "github.com": { "user": "octocat", "oauth_token": "gho_from_hosts" } }""");

        try
        {
            CliCredentialCopilotTokenProvider.TryReadOAuthTokenFromJson(path).Should().Be("gho_from_hosts");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadOAuthTokenFromJson_reads_apps_shape_with_composite_key()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-apps-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "github.com:Iv1.b507a08c87ecfe98": { "oauth_token": "gho_from_apps" } }""");

        try
        {
            CliCredentialCopilotTokenProvider.TryReadOAuthTokenFromJson(path).Should().Be("gho_from_apps");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadOAuthTokenFromJson_returns_null_for_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json");
        CliCredentialCopilotTokenProvider.TryReadOAuthTokenFromJson(path).Should().BeNull();
    }

    [Fact]
    public void TryScanGitHubTokenFromJson_reads_token_from_jsonc_with_comments()
    {
        // The GitHub Copilot CLI's config.json is JSONC: leading // comments + the token nested
        // somewhere in the document, with other properties before it.
        var path = Path.Combine(Path.GetTempPath(), $"copilot-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            "// User settings belong in settings.json.\n"
                + "// This file is managed automatically.\n"
                + "{\n  \"trustedFolders\": [\"a\", \"b\"],\n"
                + "  \"auth\": { \"host\": \"github.com\", \"token\": \"gho_jsoncTokenAAAAAAAAAAAAAAAAAAAAAA\" }\n}\n"
        );

        try
        {
            CliCredentialCopilotTokenProvider
                .TryScanGitHubTokenFromJson(path)
                .Should()
                .Be("gho_jsoncTokenAAAAAAAAAAAAAAAAAAAAAA");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadOAuthTokenFromYaml_reads_gh_hosts_yml()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gh-hosts-{Guid.NewGuid():N}.yml");
        File.WriteAllText(
            path,
            "github.com:\n    user: octocat\n    oauth_token: gho_from_yaml_bbbbbbbbbbbbbbbbbbbbbb\n    git_protocol: https\n"
        );

        try
        {
            CliCredentialCopilotTokenProvider
                .TryReadOAuthTokenFromYaml(path)
                .Should()
                .Be("gho_from_yaml_bbbbbbbbbbbbbbbbbbbbbb");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveToken_prefers_environment_variable()
    {
        var original = Environment.GetEnvironmentVariable("GITHUB_COPILOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_COPILOT_TOKEN", "gho_from_env");
            new CliCredentialCopilotTokenProvider().ResolveToken().Should().Be("gho_from_env");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_COPILOT_TOKEN", original);
        }
    }
}
