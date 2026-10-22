using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// The two pure halves of a rotation: which keys stay loaded, and what the tier PATCH sends for them.
/// </summary>
public class CapabilityTiersRotationTests
{
    [Fact]
    public void SkillTierSet_CatalogWithAnAgentItem_PatchesSkillsOnly()
    {
        CapabilityItem[] items =
        [
            new(
                "skill:/m/gb/debugging/skills/a",
                CapabilityCatalog.SkillKind,
                "gb:debugging:a",
                "debugging",
                "gb",
                "."
            ),
            new(
                "skill:/m/gb/debugging/skills/b",
                CapabilityCatalog.SkillKind,
                "gb:debugging:b",
                "debugging",
                "gb",
                "."
            ),
            new("agent:reviewer", "agent", "reviewer", null, null, "."),
        ];

        var set = CapabilityTiers.SkillTierSet(items, ["skill:/m/gb/debugging/skills/a", "agent:reviewer"]);

        set.Should()
            .BeEquivalentTo(
                new Dictionary<string, SandboxSkillTier>
                {
                    ["/m/gb/debugging/skills/a"] = SandboxSkillTier.Included,
                    ["/m/gb/debugging/skills/b"] = SandboxSkillTier.Available,
                }
            );
    }

    [Fact]
    public void Rotate_KeepsEveryPinnedKindUnused_AndCapsEachUsedKindByMostRecentUse()
    {
        var pinned = new HashSet<string>(StringComparer.Ordinal) { "skill:p", "agent:x" };
        (string Key, long Seq)[] uses =
        [
            ("skill:a", 3),
            ("skill:a", 5),
            ("skill:b", 7),
            ("skill:p", 9), // pinned: never takes a cap slot
            ("agent:y", 4),
            ("tool:z", 2),
        ];

        var loaded = CapabilityTiers.Rotate(
            pinned,
            uses,
            kind => kind == CapabilityCatalog.SkillKind ? 1 : int.MaxValue
        );

        // agent:x is pinned and never used, yet stays; the skill cap of 1 keeps b (seq 7) over a (seq 5).
        loaded.Should().Equal("agent:x", "agent:y", "skill:b", "skill:p", "tool:z");
    }
}
