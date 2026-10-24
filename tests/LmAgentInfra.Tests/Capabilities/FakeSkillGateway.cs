using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// One gateway session for <see cref="CapabilityTiers"/> tests: a literal catalog in marketplace <c>mk</c> and
/// per-agent skill tiers with <c>reset</c> semantics (gateway ADR 0041). The <c>Skill</c> text it renders for an
/// agent lists the names that agent has included, sorted; an agent never PATCHed sees the gateway default (every
/// skill). Every PATCH attempt is recorded, including the ones <see cref="FailPatches"/> makes throw.
/// </summary>
internal sealed class FakeSkillGateway
{
    /// <summary>The default catalog: plugin alpha (a, b, p, dup) and plugin beta (c, d, dup); "dup" is in both.</summary>
    public static readonly (string Plugin, string Skill)[] DefaultSkills =
    [
        ("alpha", "a"),
        ("alpha", "b"),
        ("alpha", "p"),
        ("alpha", "dup"),
        ("beta", "c"),
        ("beta", "d"),
        ("beta", "dup"),
    ];

    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, SandboxSkillTier>> _tiers = new(StringComparer.Ordinal);
    private readonly List<(string AgentId, IReadOnlyDictionary<string, SandboxSkillTier> Set)> _patches = [];
    private int _catalogCalls;
    private int _listCalls;

    public FakeSkillGateway()
        : this(DefaultSkills) { }

    public FakeSkillGateway(IReadOnlyList<(string Plugin, string Skill)> skills)
    {
        Skills = [.. skills.Select(s => (Name: $"{s.Plugin}:{s.Skill}", Path: PathOf(s.Plugin, s.Skill)))];
        Catalog = new SandboxSkillCatalog(
            "sess-1",
            "",
            SandboxSkillTier.Included,
            new SandboxSkillCounts(skills.Count, 0, 0, 0),
            [
                new SandboxSkillMarketplace(
                    "mk",
                    [
                        .. skills
                            .GroupBy(s => s.Plugin, StringComparer.Ordinal)
                            .Select(plugin => new SandboxSkillPlugin(
                                plugin.Key,
                                "1.0.0",
                                "",
                                [
                                    .. plugin.Select(s => new SandboxSkill(
                                        $"{s.Plugin}:{s.Skill}",
                                        PathOf(s.Plugin, s.Skill),
                                        $"Use when the task needs {s.Skill} from {s.Plugin}.",
                                        SandboxSkillTier.Included,
                                        SandboxSkillTierOrigin.Default,
                                        false,
                                        null,
                                        null,
                                        false
                                    )),
                                ]
                            )),
                    ]
                ),
            ],
            [],
            false
        );
    }

    public SandboxSkillCatalog Catalog { get; private set; }

    public IReadOnlyList<(string Name, string Path)> Skills { get; private set; }

    /// <summary>How many of the next PATCHes throw (after being recorded).</summary>
    public int FailPatches { get; set; }

    public int CatalogCalls => Volatile.Read(ref _catalogCalls);

    public int ListCalls => Volatile.Read(ref _listCalls);

    public IReadOnlyList<(string AgentId, IReadOnlyDictionary<string, SandboxSkillTier> Set)> Patches
    {
        get
        {
            lock (_lock)
            {
                return [.. _patches];
            }
        }
    }

    /// <summary>
    /// The gateway's discovery walk finds a workspace skill mid-session: from now on the catalog lists it, with no
    /// tier of its own, so an agent whose last reset PATCH left its path out lists it (the default, included).
    /// </summary>
    public void DiscoverWorkspaceSkill(string name, string path, string description)
    {
        lock (_lock)
        {
            Skills = [.. Skills, (name, path)];
            Catalog = Catalog with
            {
                WorkspaceSkills =
                [
                    .. Catalog.WorkspaceSkills,
                    new SandboxSkill(
                        name,
                        path,
                        description,
                        SandboxSkillTier.Included,
                        SandboxSkillTierOrigin.Default,
                        false,
                        null,
                        true,
                        false
                    ),
                ],
            };
        }
    }

    public static string PathOf(string plugin, string skill) => $"/marketplaces/mk/{plugin}/skills/{skill}";

    public static string KeyOf(string plugin, string skill) => CapabilityCatalog.SkillKeyPrefix + PathOf(plugin, skill);

    /// <summary>The <c>Skill</c> text the gateway renders for an agent that lists <paramref name="listedNames"/>.</summary>
    public static string SkillText(IEnumerable<string> listedNames)
    {
        var names = listedNames.ToList();
        return "Skills: " + (names.Count == 0 ? "(none listed)" : string.Join(", ", names));
    }

    /// <summary>
    /// The gateway calls bound to <paramref name="agentId"/>, the way the host binds them for a root. Like the host's
    /// resolver, a <c>Skill</c> listing made under a sub-agent's actor scope renders for that sub-agent's id.
    /// </summary>
    public SkillTierGateway For(string agentId) =>
        new(
            ct =>
            {
                _ = Interlocked.Increment(ref _catalogCalls);
                return Task.FromResult(Catalog);
            },
            (id, set, _) =>
            {
                lock (_lock)
                {
                    _patches.Add((id, new Dictionary<string, SandboxSkillTier>(set, StringComparer.Ordinal)));
                    if (FailPatches > 0)
                    {
                        FailPatches--;
                        throw new HttpRequestException("gateway refused the PATCH (test fault)");
                    }

                    _tiers[id] = new Dictionary<string, SandboxSkillTier>(set, StringComparer.Ordinal);
                }

                return Task.CompletedTask;
            },
            ct =>
            {
                _ = Interlocked.Increment(ref _listCalls);
                return Task.FromResult<FunctionContract?>(
                    SkillContract(SandboxSubAgentIdentity.ForCurrentFlow(agentId) ?? agentId)
                );
            }
        );

    /// <summary>The names <paramref name="agentId"/> has listed, sorted.</summary>
    public IReadOnlyList<string> ListedNames(string agentId)
    {
        lock (_lock)
        {
            var tiers = _tiers.GetValueOrDefault(agentId);
            // A path the last reset PATCH left out goes back to the default tier, included (gateway R4).
            return
            [
                .. Skills
                    .Where(s =>
                        tiers is null || !tiers.TryGetValue(s.Path, out var tier) || tier == SandboxSkillTier.Included
                    )
                    .Select(s => s.Name)
                    .Order(StringComparer.Ordinal),
            ];
        }
    }

    public FunctionContract SkillContract(string agentId) =>
        new()
        {
            Name = CapabilityTiers.SkillToolName,
            Description = SkillText(ListedNames(agentId)),
            Parameters = [],
        };
}
