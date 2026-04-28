namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Marker collection for any test that mutates the process-wide <c>CODEX_HOME</c> environment
/// variable via <see cref="IsolatedCodexHome"/>. xUnit serialises tests in the same collection
/// across classes, preventing two parallel constructions from clobbering each other's
/// <c>CODEX_HOME</c> assignment.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CodexHomeCollection
{
    public const string Name = "CodexHome";
}
