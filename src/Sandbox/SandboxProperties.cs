namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// A sandbox's property bag and partition keys (gateway ADR 0042), as
/// <see cref="SandboxClient.GetPropertiesAsync"/> and <see cref="SandboxClient.UpdatePropertiesAsync"/> return them.
/// </summary>
/// <param name="Properties">Flat <c>string → string</c> metadata about the workspace; empty when none were sent.</param>
/// <param name="Partition">
/// The ordered property keys the sandbox's workspace is partitioned by (directory levels under the app
/// directory). Empty for an unpartitioned app. These keys are immutable for the sandbox's life.
/// </param>
public sealed record SandboxProperties(IReadOnlyDictionary<string, string> Properties, IReadOnlyList<string> Partition);
