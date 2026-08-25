using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// <see cref="IEnforcementGate"/> over <c>Identity:Enforce</c>.
/// </summary>
/// <remarks>
/// Read through <see cref="IOptions{TOptions}"/> rather than captured at construction so a test host
/// that sets the flag after the container is built still sees it. The value is process-wide by
/// design (spec 4.1): enforcement is a property of the deployment, not of a customer.
/// </remarks>
public sealed class OptionsEnforcementGate : IEnforcementGate
{
    private readonly IOptions<IdentityOptions> _options;

    /// <summary>Creates the gate.</summary>
    /// <param name="options">Identity configuration.</param>
    public OptionsEnforcementGate(IOptions<IdentityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public bool IsEnforced => _options.Value.Enforce;
}
