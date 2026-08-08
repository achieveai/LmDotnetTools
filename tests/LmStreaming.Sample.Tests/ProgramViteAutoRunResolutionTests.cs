using System.Reflection;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Covers <c>Program.ResolveViteAutoRun()</c> — the flag that decides whether
///     <c>Vite.AspNetCore</c> spawns/supervises its own <c>npm run dev</c> child (AutoRun), or
///     whether an external supervisor (e.g. <c>publish-launch.ps1</c>) owns that process instead.
///     Follows the same reflection + env-var save/restore pattern as
///     <see cref="ProgramPortResolutionTests" />.
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
