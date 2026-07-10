using System.Text;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public class HostGitCredentialEnvTests
{
    [Fact]
    public void Build_EncodesTokenAsBasicExtraHeader_OffArgv()
    {
        var env = HostGitCredentialEnv.Build("ghs_secretTOKEN");

        env["GIT_CONFIG_COUNT"].Should().Be("1");
        env["GIT_CONFIG_KEY_0"].Should().Be("http.https://github.com/.extraHeader");
        env["GIT_TERMINAL_PROMPT"].Should().Be("0");

        var expected =
            "Authorization: Basic "
            + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:ghs_secretTOKEN"));
        env["GIT_CONFIG_VALUE_0"].Should().Be(expected);
    }

    [Fact]
    public void Build_BlankToken_Throws()
    {
        Action act = () => HostGitCredentialEnv.Build("  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_EmitsPerProviderExtraHeaders_ForGithubAndAdo()
    {
        var env = HostGitCredentialEnv.Build(
            [new GitProviderToken("github", "gh"), new GitProviderToken("ado", "ado-tok")]);

        env["GIT_CONFIG_COUNT"].Should().Be("2");
        env["GIT_TERMINAL_PROMPT"].Should().Be("0");

        // GitHub keeps its documented x-access-token scheme; ADO sends the Entra token in the password
        // field with an empty username (mirrors AdoPrProvider/AdoReviewCommentPublisher's Basic ":{token}").
        var headers = ExtraHeadersByHost(env);
        headers["http.https://github.com/.extraHeader"].Should().Be(
            "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:gh")));
        headers["http.https://dev.azure.com/.extraHeader"].Should().Be(
            "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":ado-tok")));
    }

    [Fact]
    public void Build_EmptyList_EmitsNoCredentialsButDisablesPrompt()
    {
        var env = HostGitCredentialEnv.Build([]);

        env["GIT_CONFIG_COUNT"].Should().Be("0");
        env.ContainsKey("GIT_CONFIG_KEY_0").Should().BeFalse();
        env["GIT_TERMINAL_PROMPT"].Should().Be("0");
    }

    [Fact]
    public void Build_UnknownProvider_IsSkipped()
    {
        // A provider with no git-host mapping (e.g. m365) contributes no credential rather than failing.
        var env = HostGitCredentialEnv.Build([new GitProviderToken("m365", "x")]);

        env["GIT_CONFIG_COUNT"].Should().Be("0");
        env["GIT_TERMINAL_PROMPT"].Should().Be("0");
    }

    private static Dictionary<string, string> ExtraHeadersByHost(IReadOnlyDictionary<string, string> env)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; env.ContainsKey($"GIT_CONFIG_KEY_{i}"); i++)
        {
            map[env[$"GIT_CONFIG_KEY_{i}"]] = env[$"GIT_CONFIG_VALUE_{i}"];
        }

        return map;
    }
}
