namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped where the process cannot create a file symlink,
/// with the reason stated in the run.
/// <para>
/// Windows grants symlink creation only under Developer Mode or elevation, so a build agent can lack it while a
/// developer box has it. Silently substituting a junction would be worse than not running the body: the setup
/// would succeed, the assertion would hold because a junction redirects too, and the test would report green
/// without ever exercising the redirected-but-absent path it exists for. Skipping says so in the one place
/// anyone reads — the test run itself.
/// </para>
/// <para>
/// As with <see cref="WindowsOnlyFactAttribute"/>, xunit 2 has no <c>Assert.Skip</c> and <c>Skip</c> on the
/// attribute is a compile-time constant, so the decision is made here in the constructor.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresFileSymlinkFactAttribute : FactAttribute
{
    public RequiresFileSymlinkFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        if (!FileLink.Supported)
        {
            Skip = $"Needs file-symlink creation (Developer Mode or elevation): {because}";
        }
    }
}
