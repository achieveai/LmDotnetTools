using System.Reflection;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Covers <c>Program.ResolveViteAutoRun()</c> — the flag that decides whether
///     <c>Vite.AspNetCore</c> spawns/supervises its own <c>npm run dev</c> child (AutoRun) when
///     the app is run in DEVELOPMENT mode via <c>dotnet run</c>. This is unrelated to
///     <c>publish-launch.ps1</c>, which now builds+publishes a standalone Production artifact
///     and never spawns, proxies to, or otherwise involves a Vite dev server at all. Follows the
///     same reflection + env-var save/restore pattern as <see cref="ProgramPortResolutionTests" />.
/// </summary>
public class ProgramViteAutoRunResolutionTests
{
    private const string EnvVarName = "VITE_AUTO_RUN";

    [Fact]
    public void ResolveViteAutoRun_DefaultsTrue_WhenEnvVarUnset()
    {
        var previous = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, null);

            Invoke().Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, previous);
        }
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("False")]
    public void ResolveViteAutoRun_ReturnsFalse_OnlyWhenEnvVarIsFalse(string value)
    {
        var previous = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, value);

            Invoke().Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, previous);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("nonsense")]
    public void ResolveViteAutoRun_ReturnsTrue_ForAnyNonFalseValue(string value)
    {
        var previous = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, value);

            Invoke().Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, previous);
        }
    }

    private static bool Invoke()
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod("ResolveViteAutoRun", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        return (bool)method!.Invoke(null, null)!;
    }
}

/// <summary>
///     Covers <c>Program.BuildSpaRedirectTarget()</c> — the rewrite rule behind the
///     Development-only <c>GET /</c> → <c>/dist/index.html</c> redirect that fronts the Vite dev
///     server. It must carry the query string through the hop: a deep link like
///     <c>/?threadId=X</c> that lands on <c>/dist/index.html</c> with no query makes the app
///     silently open the most recent thread instead of the linked one.
/// </summary>
public class ProgramSpaRedirectTargetTests
{
    [Fact]
    public void BuildSpaRedirectTarget_PreservesTheQueryString()
    {
        global::Program
            .BuildSpaRedirectTarget(
                Microsoft.AspNetCore.Http.QueryString.FromUriComponent("?threadId=thread-123&tab=board")
            )
            .Should()
            .Be("/dist/index.html?threadId=thread-123&tab=board");
    }

    [Fact]
    public void BuildSpaRedirectTarget_KeepsQueryValuesEscaped()
    {
        // QueryString round-trips the already-escaped URI component; the redirect must not decode it.
        global::Program
            .BuildSpaRedirectTarget(Microsoft.AspNetCore.Http.QueryString.FromUriComponent("?q=a%20b%26c"))
            .Should()
            .Be("/dist/index.html?q=a%20b%26c");
    }

    [Fact]
    public void BuildSpaRedirectTarget_WithNoQuery_IsTheBareSpaPath()
    {
        global::Program
            .BuildSpaRedirectTarget(Microsoft.AspNetCore.Http.QueryString.Empty)
            .Should()
            .Be("/dist/index.html");
    }
}
