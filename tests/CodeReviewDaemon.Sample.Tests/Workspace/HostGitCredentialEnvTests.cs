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
}
