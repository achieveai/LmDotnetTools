namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>The terminal outcome of a source preparation that did not fail.</summary>
public enum SandboxSourcePreparationStatus
{
    /// <summary>Every source is an exact Git commit; <see cref="SandboxSourcePreparation.SourceVersion"/> is set.</summary>
    Ready,

    /// <summary>
    /// Some source is mutable (an operator filesystem marketplace), or the gateway runs without a marketplace store, so
    /// there is no version to guard a create with (<see cref="SandboxSourcePreparation.ErrorCode"/> is
    /// <c>source_snapshot_unsupported</c>). Create unguarded, or stop.
    /// </summary>
    Unversioned,
}
