using System.Reflection;
using CoreOutcomes = AchieveAi.LmDotnetTools.LmCore.Approval.ToolApprovalOutcomes;
using WireOutcomes = AchieveAi.LmDotnetTools.LmLifecycle.ToolApprovalOutcomes;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Approval;

/// <summary>
/// Pins LmCore's copy of the approval outcome codes to the wire contract's.
/// </summary>
/// <remarks>
/// The codes are deliberately duplicated rather than shared: LmCore does not reference the wire
/// contract, so a host can enforce approval without taking a dependency on the event schema. That
/// duplication is only safe if the two sets cannot drift, which is what this test guarantees — a
/// value renamed on one side and not the other would let a host allow a call the wire believed was
/// blocked.
/// </remarks>
public class ToolApprovalOutcomeParityTests
{
    private static IReadOnlyDictionary<string, string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

    [Fact]
    public void CoreOutcomeCodes_MatchTheWireContract()
    {
        var core = ConstantsOf(typeof(CoreOutcomes));
        var wire = ConstantsOf(typeof(WireOutcomes));

        Assert.NotEmpty(core);
        Assert.Equal(wire.Keys.OrderBy(k => k, StringComparer.Ordinal), core.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (name, value) in wire)
        {
            Assert.Equal(value, core[name]);
        }
    }

    [Theory]
    [InlineData("allowed", true)]
    [InlineData("denied", false)]
    [InlineData("Allowed", false)]
    [InlineData("ALLOWED", false)]
    [InlineData(" allowed", false)]
    [InlineData("something_this_build_has_never_heard_of", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowed_AgreesWithTheWireContract(string? outcome, bool expected)
    {
        Assert.Equal(expected, CoreOutcomes.IsAllowed(outcome));
        Assert.Equal(expected, WireOutcomes.IsAllowed(outcome));
    }
}
