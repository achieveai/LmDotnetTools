using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Provides the Agent, SendMessage, CheckAgent, and WaitAgent tool definitions for sub-agent
/// orchestration. Registered as an IFunctionProvider so these tools are included in
/// the parent agent's function registry alongside all other tools.
/// </summary>
/// <remarks>
/// The surface has two shapes. Without a collaboration it is the historical one — Agent,
/// SendMessage, CheckAgent — plus WaitAgent, the singular blocking wait that spares a legacy agent
/// from polling CheckAgent in a loop. With a collaboration the same delegation tools gain the
/// role/description the directory needs, plural observation replaces the singular check and wait, and
/// messaging becomes typed and hierarchy-wide rather than parent-to-child only.
/// </remarks>
public class SubAgentToolProvider : IFunctionProvider
{
    /// <summary>
    /// Name of the tool that starts a NEW sub-agent. Exposed so hosts can reason about the
    /// tool that <see cref="SuppressSpawning"/> gates without duplicating the literal.
    /// </summary>
    public const string SpawnToolName = "Agent";

    /// <summary>
    /// Name of the singular blocking wait offered only when collaboration is off; under
    /// collaboration <c>WaitForAgents</c> covers the same need for a whole fan-out.
    /// </summary>
    public const string WaitAgentToolName = "WaitAgent";

    /// <summary>Name of the tool that sends a message to another agent.</summary>
    public const string SendMessageToolName = "SendMessage";

    /// <summary>Name of the singular non-blocking status tool.</summary>
    public const string CheckAgentToolName = "CheckAgent";

    /// <summary>Name of the fan-out status tool, offered only under collaboration.</summary>
    public const string CheckAgentsToolName = "CheckAgents";

    /// <summary>Name of the fan-out blocking wait, offered only under collaboration.</summary>
    public const string WaitForAgentsToolName = "WaitForAgents";

    /// <summary>Name of the directory-listing tool, offered only under collaboration.</summary>
    public const string GetAgentsToolName = "GetAgents";

    /// <summary>
    /// Every tool name this provider can expose, across BOTH surface shapes — the legacy one
    /// (<c>Agent</c>/<c>SendMessage</c>/<c>CheckAgent</c>/<c>WaitAgent</c>) and the collaboration one,
    /// which swaps <c>WaitAgent</c> for <c>CheckAgents</c>/<c>WaitForAgents</c>/<c>GetAgents</c>. No
    /// single conversation sees all of these at once; a host enumerating the selectable sub-agent
    /// surface needs the union. Derived from the same name constants <see cref="GetFunctions"/> uses
    /// so the two cannot drift.
    /// </summary>
    public static readonly IReadOnlyList<string> AllToolNames =
    [
        SpawnToolName,
        SendMessageToolName,
        CheckAgentToolName,
        WaitAgentToolName,
        CheckAgentsToolName,
        WaitForAgentsToolName,
        GetAgentsToolName,
    ];

    /// <summary>
    /// How many known agent ids an unknown-id error may list. Bounded so a hierarchy with hundreds of
    /// agents cannot turn one mistyped id into a multi-kilobyte tool result; when it bites, the text
    /// says so rather than silently presenting a truncated list as the whole set.
    /// </summary>
    internal const int MaxListedAgentIds = 20;

    /// <summary>
    /// The sentence that redirects a workflow id to the tool that accepts it, appended verbatim to
    /// BOTH wait descriptors.
    /// </summary>
    /// <remarks>
    /// Agent ids and workflow ids are both opaque handles minted by tools that sit side by side in the
    /// Workspace Agent surface, so nothing about their shape stops one being passed where the other
    /// belongs. Telling the model it may not pass a workflow id here is only half the correction —
    /// naming the tool that does take one is the half it can act on. Shared as a constant because the
    /// two descriptors are edited independently and a redirect that drifts on one of them is worse
    /// than none: it would send the model to a tool that does not exist.
    /// </remarks>
    internal const string WorkflowIdRedirect =
        " A workflow started with StartWorkflowAgent is followed with CheckWorkflow/WaitWorkflow, "
        + "never with a wait on agents.";

    private readonly SubAgentManager _manager;
    private readonly MutableSubAgentTemplateSource _source;
    private readonly IReadOnlySet<string>? _exposedToolNames;

    /// <summary>
    /// Number of open <see cref="SuppressSpawning"/> scopes. Non-zero hides the spawn tool
    /// from the contract set and makes its handler refuse. Reference-counted so nested
    /// scopes compose, and read with <see cref="Volatile"/> because contract building and
    /// tool dispatch can happen on different threads than the scope owner.
    /// </summary>
    private int _spawnSuppressionDepth;

    /// <param name="manager">The manager whose sub-agents these tools drive.</param>
    /// <param name="source">The mutable template source backing the spawn tool's catalog.</param>
    /// <param name="exposedToolNames">
    /// An allow-list of tool names to expose, or null for the whole surface. Applied on top of the
    /// collaboration shape chosen by <paramref name="manager"/>, so it can only ever remove.
    /// </param>
    public SubAgentToolProvider(
        SubAgentManager manager,
        MutableSubAgentTemplateSource source,
        IReadOnlySet<string>? exposedToolNames = null
    )
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(source);
        _manager = manager;
        _source = source;
        _exposedToolNames = exposedToolNames;
    }

    public string ProviderName => "SubAgentTools";

    /// <summary>
    /// Low priority (high number) so parent tools take precedence.
    /// </summary>
    public int Priority => 100;

    /// <summary>
    /// Hides the spawn tool for the lifetime of the returned scope, keeping SendMessage and
    /// CheckAgent available. Used when a turn must consume what already-running sub-agents
    /// delivered without being able to start new ones (e.g. a synthesis turn that runs after
    /// a completion barrier). Contracts are rebuilt per turn, so the next contract build
    /// inside the scope simply omits the tool; the handler is additionally gated because the
    /// loop snapshots handlers at construction time and the model can replay an older call.
    /// Scopes are reference-counted and each scope's <c>Dispose</c> is idempotent.
    /// </summary>
    public IDisposable SuppressSpawning() => new SpawnSuppressionScope(this);

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var shape = EmitShape();
        return _exposedToolNames is null ? shape : shape.Where(d => _exposedToolNames.Contains(d.Contract.Name));
    }

    /// <summary>
    /// The tools this provider's collaboration shape emits, before any allow-list narrowing.
    /// </summary>
    private IEnumerable<FunctionDescriptor> EmitShape()
    {
        var collaboration = _manager.Collaboration;
        if (collaboration is null)
        {
            if (!IsSpawningSuppressed)
            {
                yield return CreateAgentDescriptor(collaborationEnabled: false);
            }

            yield return CreateSendMessageDescriptor(collaborationEnabled: false);
            yield return CreateCheckAgentDescriptor();
            yield return CreateWaitAgentDescriptor();
            yield break;
        }

        // Only the spawn tool is withdrawn at the delegation limit or while spawning is suppressed:
        // it is the only one whose whole purpose is to delegate. Observation and messaging are how an
        // agent that cannot currently delegate still coordinates — withdrawing those would leave it
        // able to be asked questions it had no tool to answer.
        if (collaboration.CanDelegate && !IsSpawningSuppressed)
        {
            yield return CreateAgentDescriptor(collaborationEnabled: true);
        }

        yield return CreateCheckAgentsDescriptor();
        yield return CreateWaitForAgentsDescriptor();
        yield return CreateGetAgentsDescriptor();
        yield return CreateSendMessageDescriptor(collaborationEnabled: true);
    }

    private bool IsSpawningSuppressed => Volatile.Read(ref _spawnSuppressionDepth) > 0;

    /// <summary>
    /// Reference-counted, idempotent suppression scope handed out by <see cref="SuppressSpawning"/>.
    /// </summary>
    private sealed class SpawnSuppressionScope : IDisposable
    {
        private SubAgentToolProvider? _owner;

        internal SpawnSuppressionScope(SubAgentToolProvider owner)
        {
            _owner = owner;
            _ = Interlocked.Increment(ref owner._spawnSuppressionDepth);
        }

        public void Dispose()
        {
            // Exchange-to-null so a repeated Dispose cannot decrement a depth it no longer owns.
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                _ = Interlocked.Decrement(ref owner._spawnSuppressionDepth);
            }
        }
    }

    private FunctionDescriptor CreateAgentDescriptor(bool collaborationEnabled)
    {
        // Snapshot the live templates view once per descriptor build so the catalog text
        // and the subagent_type enum list are consistent within this descriptor even when
        // TryRegister lands concurrently.
        var templates = _source.Templates;
        var typeList = string.Join(", ", templates.Keys);

        var contract = new FunctionContract
        {
            Name = SpawnToolName,
            Description =
                "Delegate a task to a specialized sub-agent. By default this BLOCKS "
                + "until the sub-agent finishes and returns its final answer as the "
                + "tool result — use it when you need the answer before continuing.\n\n"
                + "Set run_in_background: true to spawn asynchronously instead: the tool "
                + "returns an agent id immediately, you poll progress with CheckAgent, "
                + "and the final result is also delivered back to you as a follow-up "
                + "message. Use background mode for long-running work you want to run "
                + "while you keep working, or to fan out several sub-agents at once.\n\n"
                + "Each sub-agent starts fresh and does NOT see your conversation history, "
                + "so make the prompt self-contained.\n\n"
                + "PREFER CONTINUING AN EXISTING SUB-AGENT: before spawning a NEW sub-agent, "
                + "check whether one you already spawned is still live and already has the "
                + "context for this work — if so, continue it with SendMessage instead of "
                + "spawning a fresh agent for the same or a follow-up task. Only spawn a new "
                + "agent when no suitable live agent exists, or when you deliberately want "
                + "independent/parallel work.\n\n"
                + BuildTemplateCatalog(templates),
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "subagent_type",
                    Description = $"Which sub-agent to spawn. One of: {typeList}.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "prompt",
                    Description =
                        "The task or instruction for the sub-agent. Be specific and "
                        + "self-contained; the sub-agent does not share your context.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                // Present only under collaboration: without a directory there is nothing for a role to
                // label, and adding an inert parameter would change the legacy tool schema.
                .. collaborationEnabled
                    ? new FunctionParameterContract[]
                    {
                        new()
                        {
                            Name = "role",
                            Description =
                                "REQUIRED unless the chosen subagent_type pins its own role. A short "
                                + "label (a few words) for what this sub-agent is responsible for, "
                                + "e.g. 'auth migration reviewer'. Published in the shared agent "
                                + "directory so other agents can find the right counterpart. Every "
                                + "agent in the collaboration can read it, so put no secrets, "
                                + "credentials, private or customer data in it.",
                            ParameterType = new JsonSchemaObject { Type = new("string") },
                            IsRequired = false,
                        },
                    }
                    : [],
                new FunctionParameterContract
                {
                    Name = "description",
                    Description = collaborationEnabled
                        ? "REQUIRED. One or two sentences on what this sub-agent is doing and when "
                            + "another agent should contact it. Published in the shared agent "
                            + "directory, so write it for a stranger, not for yourself. Every agent "
                            + "in the collaboration can read it, so put no secrets, credentials, "
                            + "private or customer data in it."
                        : "Optional short 3-5 word label for this delegation " + "(used for telemetry/UI).",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = collaborationEnabled,
                },
                new FunctionParameterContract
                {
                    Name = "name",
                    Description =
                        "Recommended: a short, human-readable, unique handle for this "
                        + "sub-agent (e.g. 'auth-reviewer', 'db-migrator') so it is easy to "
                        + "identify in progress/telemetry and to address later via SendMessage. "
                        + "Optional — if omitted, a readable name is auto-derived from the "
                        + "subagent_type.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "model",
                    Description = BuildModelOverrideDescription(_manager.AvailableModelIds),
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "modelIntelligence",
                    Description =
                        "Optional model-intelligence tier (integer; ascending capability, 0 = cheapest) "
                        + "used to size this sub-agent's model when no explicit 'model' is given. The host "
                        + "resolves it to a concrete model, climbing to the nearest higher configured tier "
                        + "when the requested one is unmapped; omit it to keep the sub-agent's default "
                        + "(parent-inherited) model. An explicit 'model' always wins over this.",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "run_in_background",
                    Description =
                        "When true, return immediately with an agent id instead of "
                        + "blocking for the result. Poll progress with CheckAgent.",
                    ParameterType = new JsonSchemaObject { Type = new("boolean") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "add_tools",
                    Description = "Comma-separated list of additional tool names to enable.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "remove_tools",
                    Description = "Comma-separated list of tool names to disable.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleAgentToolAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateSendMessageDescriptor(bool collaborationEnabled)
    {
        var contract = collaborationEnabled
            ? BuildCollaborationSendMessageContract()
            : BuildLegacySendMessageContract();

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleSendMessageToolAsync,
            ProviderName = ProviderName,
        };
    }

    private static FunctionContract BuildLegacySendMessageContract()
    {
        return new FunctionContract
        {
            Name = SendMessageToolName,
            Description =
                "Continue an existing sub-agent with a follow-up message — PREFER THIS over "
                + "spawning a new Agent when you are iterating on, correcting, or extending "
                + "work a still-live sub-agent already has the context for. Address it "
                + "by the id returned from Agent, or by the name you gave it when "
                + "spawning. By default BLOCKS until the continued run finishes and "
                + "returns its final answer; set run_in_background: true to return "
                + "immediately and poll with CheckAgent.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "target",
                    Description = "The sub-agent's id (from Agent) or the name you assigned it.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "prompt",
                    Description = "The follow-up message or instruction to send.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "run_in_background",
                    Description =
                        "When true, return immediately instead of blocking for the "
                        + "result. Poll progress with CheckAgent.",
                    ParameterType = new JsonSchemaObject { Type = new("boolean") },
                    IsRequired = false,
                },
            ],
        };
    }

    private static FunctionContract BuildCollaborationSendMessageContract()
    {
        return new FunctionContract
        {
            Name = SendMessageToolName,
            Description =
                "Send a message to ANY agent in this collaboration — your own sub-agents, your "
                + "parent, or a peer you found with GetAgents. Address it by the agent_id from "
                + "GetAgents (always unambiguous) or by name.\n\n"
                + "This never blocks: it returns as soon as the message is accepted, and the "
                + "recipient handles it on its own turn. If you asked a question, the answer "
                + "arrives later as a message to you — keep working meanwhile, or use "
                + "WaitForAgents.\n\n"
                + "Choose msg_type honestly; it tells the recipient what is expected of it.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "target",
                    Description = "The recipient's agent_id (preferred — names can collide) or its name.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "content",
                    Description =
                        "What you want to say. The recipient does NOT see your conversation, so "
                        + "make it self-contained.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "msg_type",
                    Description =
                        "What kind of message this is. One of: "
                        + "'question' (you need an answer back), "
                        + "'delegate_task' (you are handing over work and expect a result), "
                        + "'task_update' (progress on a task delegated to you — set in_response_to), "
                        + "'steer' (a correction to work already in flight, no reply needed; the "
                        + "recipient must currently be running), "
                        + "'response' (an answer to a message you received — set in_response_to).",
                    // Enumerated in the schema, not only in prose: the vocabulary is closed, and a
                    // value outside it is refused, so the model should be told the whole set by the
                    // one part of the contract it cannot skim past.
                    ParameterType = new JsonSchemaObject
                    {
                        Type = new("string"),
                        Enum = ["question", "delegate_task", "task_update", "steer", "response"],
                    },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "in_response_to",
                    Description =
                        "REQUIRED when msg_type is 'response' or 'task_update': the message_id you "
                        + "are answering or reporting progress on, taken from the message you "
                        + "received. Omit otherwise.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
            ],
        };
    }

    private FunctionDescriptor CreateCheckAgentDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = CheckAgentToolName,
            Description =
                "Check the status and recent activity of a background sub-agent (one "
                + "spawned with run_in_background: true). Returns its status, recent "
                + "turns, and final result once completed. Synchronous Agent/SendMessage "
                + "calls already return the result directly and do not need CheckAgent.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "agent_id",
                    Description = "The id of the sub-agent to check (from Agent or SendMessage).",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleCheckAgentToolAsync,
            ProviderName = ProviderName,
        };
    }

    /// <summary>
    /// The singular blocking wait, offered ONLY when collaboration is off. Without it the legacy
    /// surface's sole way to find out that a background sub-agent finished is to call CheckAgent
    /// again — which costs a turn per poll and, with nothing else to do, is a spin loop the model pays
    /// for. Under collaboration <c>WaitForAgents</c> supersedes it, and no alias is kept: two waits in
    /// one surface would just invite waiting on one child at a time.
    /// </summary>
    private FunctionDescriptor CreateWaitAgentDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = WaitAgentToolName,
            Description =
                "Block until a background sub-agent finishes, instead of calling CheckAgent in a "
                + "loop. Returns the same status/result CheckAgent would, once the agent has reached a "
                + "terminal state — completed OR failed. Only agents you spawned with "
                + "run_in_background: true can be waited on; a synchronous Agent call already returned "
                + "its result.\n\n"
                + "Pass timeout_seconds so a wedged agent cannot stall you indefinitely: on expiry the "
                + "call returns status 'timeout', the agent keeps running, and you can wait again. Do "
                + "not wait while you still have work of your own — do it and wait afterwards.\n\n"
                + "Use an `agent_id` returned by `Agent`; do not pass workflow IDs."
                + WorkflowIdRedirect,
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "agent_id",
                    Description = "The id of the sub-agent to wait for (from Agent or SendMessage).",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "timeout_seconds",
                    Description =
                        "Optional cap on the wait. On expiry the call returns with status "
                        + "'timeout' and the agent keeps running — nothing is cancelled. "
                        + "Must be a positive whole number of seconds; omit it entirely to wait "
                        + "without a cap.",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleWaitAgentToolAsync,
            ProviderName = ProviderName,
        };
    }

    /// <summary>
    /// The plural replacement for CheckAgent under collaboration. Fanning out and then polling one id
    /// per tool call costs a turn per child; one call covering the whole fan-out is what makes parallel
    /// delegation practical.
    /// </summary>
    private FunctionDescriptor CreateCheckAgentsDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = CheckAgentsToolName,
            Description =
                "Check the status and recent activity of agents — several at once. Covers any agent "
                + "you can see with GetAgents, not just your own children, so you can tell whether a "
                + "counterpart is still running before you message or wait on it. Returns, per agent, "
                + "its status and — for your own sub-agents — recent turns and final result. Returns "
                + "immediately; use WaitForAgents when you would otherwise poll in a loop.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "agent_ids",
                    Description = "Comma-separated agent ids or names to check, e.g. " + "'agt_1, auth-reviewer'.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleCheckAgentsToolAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateWaitForAgentsDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = WaitForAgentsToolName,
            Description =
                "Block until sub-agents YOU spawned finish, instead of polling CheckAgents in a "
                + "loop. Only your own direct children can be waited on — to coordinate with "
                + "anyone else, message them and keep working.\n\n"
                + "WHEN TO USE IT: you have nothing useful to do until a child you spawned in the "
                + "background reports back. Prefer 'any' mode when you can make progress on the "
                + "first result, and always pass timeout_seconds so a wedged child cannot stall you "
                + "indefinitely — on expiry the agents keep running and you can wait again.\n\n"
                + "WHEN NOT TO USE IT: never wait on your parent, a peer, or anyone you merely sent "
                + "a message to — that is not what this waits for, and two agents each waiting on "
                + "the other is a deadlock. Do not wait when you still have work of your own; do it "
                + "and check afterwards.\n\n"
                + "The wait ends early if another agent asks YOU a question, so you are never the "
                + "reason someone else is stuck: answer it with SendMessage, then wait again. Each "
                + "question ends at most one wait, so a question you have chosen not to answer will "
                + "not keep interrupting.\n\n"
                + "Use `agent_ids` returned by `Agent` (or the names you gave them); do not pass "
                + "workflow IDs."
                + WorkflowIdRedirect,
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "agent_ids",
                    Description = "Comma-separated ids or names of your own sub-agents to wait for.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "mode",
                    Description =
                        "'all' (default) waits for every listed agent; 'any' returns as soon as "
                        + "the first one finishes.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "timeout_seconds",
                    Description =
                        "Optional cap on the wait. On expiry the call returns with status "
                        + "'timeout' and the agents keep running — nothing is cancelled. "
                        + "Must be a positive whole number of seconds; omit it entirely to wait "
                        + "without a cap.",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleWaitForAgentsToolAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateGetAgentsDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = GetAgentsToolName,
            Description =
                "List every agent in this collaboration — not just your own sub-agents — with its "
                + "agent_id, name, role, description, and where it sits in the hierarchy. Use it "
                + "to find who already owns a piece of work BEFORE spawning someone new to do it, "
                + "and to get the agent_id to address with SendMessage.",
            Parameters = [],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleGetAgentsToolAsync,
            ProviderName = ProviderName,
        };
    }

    /// <summary>
    /// Builds the <c>model</c> parameter description for the Agent tool. When the host surfaced the set of
    /// valid model ids (<see cref="SubAgentManager.AvailableModelIds"/>) the description lists them and
    /// spells out that this field is neither a <c>subagent_type</c> nor a capability tier — the two fields
    /// LLMs most often confuse it with — so the parent stops inventing ids or cross-filling from another
    /// argument. With no id list it keeps the generic wording (previous behavior). Pairs with the runtime
    /// <see cref="SubAgentOptions.ModelOverrideValidator"/>, which drops any id not in this set.
    /// </summary>
    private static string BuildModelOverrideDescription(IReadOnlyCollection<string>? availableModelIds)
    {
        // Usually OMIT this — a sub-agent inherits the right model from its template/the parent
        // automatically, which is almost always correct. Only set it to deliberately run this ONE
        // sub-agent on a different model.
        const string lead =
            "Optional model id override for this sub-agent. Usually OMIT this — the sub-agent inherits "
            + "the correct model automatically; set it only to deliberately run this one sub-agent on a "
            + "different model. This is a MODEL ID, not a subagent_type (that is the separate "
            + "'subagent_type' argument) and not a capability tier (use 'modelIntelligence' for that).";

        if (availableModelIds is { Count: > 0 })
        {
            return lead
                + " If set, it MUST be exactly one of: "
                + string.Join(", ", availableModelIds)
                + ". Any other value is ignored and the sub-agent keeps its inherited model.";
        }

        return lead + " Defaults to the template's configured model.";
    }

    /// <summary>
    /// Builds the per-template catalog embedded in the Agent tool description so the
    /// parent LLM can pick the right sub-agent type.
    /// </summary>
    private static string BuildTemplateCatalog(IReadOnlyDictionary<string, SubAgentTemplate> templates)
    {
        var sb = new StringBuilder();
        _ = sb.Append("Available sub-agent types (subagent_type):");

        foreach (var (key, template) in templates)
        {
            var description = string.IsNullOrWhiteSpace(template.Description)
                ? "(no description provided)"
                : template.Description.Trim();

            _ = sb.Append("\n- ").Append(key).Append(": ").Append(description);

            if (!string.IsNullOrWhiteSpace(template.WhenToUse))
            {
                _ = sb.Append("\n  When to use: ").Append(template.WhenToUse.Trim());
            }
        }

        return sb.ToString();
    }

    private async Task<ToolHandlerResult> HandleAgentToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        // Handlers are snapshotted by the loop at construction time, so dropping the contract
        // is not enough — refuse here too if the model replays a spawn from earlier history.
        if (IsSpawningSuppressed)
        {
            return ToolHandlerResult.FromError(
                "Spawning new sub-agents is not available for this turn. Use CheckAgent to read what "
                    + "the existing sub-agents delivered, or SendMessage to follow up with one of them.",
                "spawn_suppressed"
            );
        }

        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var prompt =
            GetOptionalString(root, "prompt") ?? throw new ArgumentException("The 'prompt' parameter is required.");
        var subagentType =
            GetOptionalString(root, "subagent_type")
            ?? throw new ArgumentException("The 'subagent_type' parameter is required.");

        var name = GetOptionalString(root, "name");
        var model = GetOptionalString(root, "model");
        var modelIntelligence = GetOptionalInt(root, "modelIntelligence");
        if (_manager.SpawnModelSelectionResolver?.Invoke(name) is { } authoritativeSelection)
        {
            model = authoritativeSelection.Model;
            modelIntelligence = authoritativeSelection.ModelIntelligence;
        }

        var runInBackground = GetOptionalBool(root, "run_in_background") ?? false;

        // Both are inert without a collaboration — 'description' stays the human-facing delegation
        // label it has always been — and both become published directory metadata with one. Reading
        // them unconditionally keeps this handler free of the mode branch; SpawnAsync owns validation.
        var role = GetOptionalString(root, "role");
        var description = GetOptionalString(root, "description");
        var addTools = ParseCommaSeparated(GetOptionalString(root, "add_tools"));
        var removeTools = ParseCommaSeparated(GetOptionalString(root, "remove_tools"));

        // Self-correcting spawn-name gate (host-supplied, workflow-agnostic here). When the host correlates
        // spawn results by an EXACT name (a workflow controller), a name that matches no ready unit would run
        // and then be silently discarded — the caller must fix the NAME, not the spawn. Surface the correction
        // as a recoverable tool error (mirroring unknown_subagent_type) so the caller re-issues the exact name
        // instead of looping on a discarded duplicate. No gate (ordinary hosts) = pass through unchanged.
        if (_manager.SpawnNameGate?.Invoke(name) is { } rejection)
        {
            return ToolHandlerResult.FromError(rejection, "spawn_name_unmatched");
        }

        try
        {
            var result = await _manager.SpawnAsync(
                subagentType,
                prompt,
                name,
                model,
                runInBackground,
                addTools,
                removeTools,
                cancellationToken,
                modelIntelligence,
                // The only place the spawning call's identity is in scope. Without it a
                // subscriber sees a sub-agent appear with a parent run but no reason.
                context.ToolCallId,
                role,
                description
            );

            return ToolHandlerResult.FromText(result);
        }
        catch (SubAgentQueueFullException ex)
        {
            return ToolHandlerResult.FromError(ex.Message, "queue_full");
        }
        catch (SubAgentCollaborationException ex)
        {
            // Refused before the sub-agent existed, for a reason the caller can act on (add a role,
            // wait for capacity, stop delegating). Must precede the ArgumentException catch only in
            // reading order — it derives from InvalidOperationException, so the two never overlap.
            return ToolHandlerResult.FromError(ex.Message, ex.FailureCode);
        }
        catch (ArgumentException ex)
        {
            // An unknown or ambiguous subagent_type is a MODEL mistake (a bare/mis-prefixed name, or
            // one that matches several agents), not a host fault. Return the actionable message as a
            // recoverable tool result — listing the valid/suggested names — so the caller re-issues
            // with an exact name instead of silently collapsing to general-purpose.
            return ToolHandlerResult.FromError(ex.Message, "unknown_subagent_type");
        }
        catch (SubAgentExecutionException ex)
        {
            return ToolHandlerResult.FromError(ex.Message, "subagent_failed");
        }
    }

    private async Task<ToolHandlerResult> HandleSendMessageToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var target =
            GetOptionalString(root, "target") ?? throw new ArgumentException("The 'target' parameter is required.");

        if (_manager.Collaboration is { } collaboration)
        {
            return SendCollaborationMessage(collaboration, root, target);
        }

        var prompt =
            GetOptionalString(root, "prompt") ?? throw new ArgumentException("The 'prompt' parameter is required.");
        var runInBackground = GetOptionalBool(root, "run_in_background") ?? false;

        try
        {
            var result = await _manager.SendMessageAsync(target, prompt, runInBackground, cancellationToken);

            return ToolHandlerResult.FromText(result);
        }
        catch (SubAgentExecutionException ex)
        {
            return ToolHandlerResult.FromError(ex.Message, "subagent_failed");
        }
    }

    /// <summary>
    /// Admits one typed message and reports the admission, not the delivery.
    /// </summary>
    /// <remarks>
    /// Deliberately does not await <see cref="AgentDispatch.Delivery"/>. Waiting for the recipient to
    /// accept would re-couple the sender's turn to the recipient's lifecycle — exactly what the
    /// asynchronous model exists to avoid — and a recipient that is busy is not a send failure. For the
    /// same reason it takes no cancellation token: the tool call's token dies with this turn, and an
    /// admitted message must still be delivered after the sender has moved on.
    /// </remarks>
    private static ToolHandlerResult SendCollaborationMessage(
        AgentCollaborationSetup collaboration,
        JsonElement root,
        string target
    )
    {
        var content =
            GetOptionalString(root, "content") ?? throw new ArgumentException("The 'content' parameter is required.");

        var rawType =
            GetOptionalString(root, "msg_type") ?? throw new ArgumentException("The 'msg_type' parameter is required.");

        if (!TryParseMessageType(rawType, out var messageType))
        {
            return ToolHandlerResult.FromError(
                $"Unknown msg_type '{rawType}'. Use one of: question, delegate_task, "
                    + "task_update, steer, response.",
                "invalid_msg_type"
            );
        }

        // Some callers cannot omit an optional string parameter and send "" or whitespace where they
        // mean "no correlation" instead of a true JSON null. GetOptionalString already collapses an
        // omitted key or an explicit JSON null to C# null; normalizing here too means every message
        // type downstream sees exactly two states — a real id, or absent — regardless of how the
        // absence was spelled. Without this, a blank string survived as a non-null in_response_to all
        // the way to the ledger, which treated it as an attempted correlation to a message that does
        // not exist and refused it as unknown_correlation instead of sending the message as a fresh one.
        var inResponseTo = NormalizeCorrelationId(GetOptionalString(root, "in_response_to"));

        // Both reply-shaped types are checked here, not just Response. The ledger refuses either one
        // without a correlation, but doing it at the tool boundary is what turns that refusal into a
        // sentence naming the parameter the model left out.
        if (messageType is AgentMessageType.Response or AgentMessageType.TaskUpdate && inResponseTo is null)
        {
            return ToolHandlerResult.FromError(
                messageType == AgentMessageType.Response
                    ? "A 'response' must set 'in_response_to' to the message_id it answers."
                    : "A 'task_update' must set 'in_response_to' to the message_id of the task that "
                        + "was delegated to you.",
                AgentMessageFailureCodes.MissingCorrelation
            );
        }

        var dispatch = new AgentCollaborationMessenger(collaboration).Send(target, content, messageType, inResponseTo);

        if (!dispatch.Result.Succeeded)
        {
            return ToolHandlerResult.FromError(
                DescribeSendFailure(dispatch.Result.FailureCode, target),
                dispatch.Result.FailureCode ?? "send_refused"
            );
        }

        return ToolHandlerResult.FromText(
            JsonSerializer.Serialize(
                new
                {
                    status = "accepted",
                    message_id = dispatch.Result.MessageId,
                    to_agent_id = dispatch.Result.Target?.AgentId,
                    to_name = dispatch.Result.Target?.Name,
                    msg_type = rawType,
                    // Restated rather than inferred by the caller: only these two types leave a correlation
                    // open, and a sender that waits for an answer that is never coming is a deadlock.
                    expects_reply = messageType is AgentMessageType.Question or AgentMessageType.DelegateTask,
                }
            )
        );
    }

    /// <summary>Maps the tool's snake_case wire vocabulary onto the closed message-type set.</summary>
    private static bool TryParseMessageType(string raw, out AgentMessageType messageType)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "question":
                messageType = AgentMessageType.Question;
                return true;
            case "delegate_task":
                messageType = AgentMessageType.DelegateTask;
                return true;
            case "task_update":
                messageType = AgentMessageType.TaskUpdate;
                return true;
            case "steer":
                messageType = AgentMessageType.Steer;
                return true;
            case "response":
                messageType = AgentMessageType.Response;
                return true;
            default:
                messageType = default;
                return false;
        }
    }

    /// <summary>
    /// Turns a refusal code into a sentence that tells the model what to do differently. Never echoes
    /// the message body.
    /// </summary>
    private static string DescribeSendFailure(string? failureCode, string target)
    {
        return failureCode switch
        {
            // Two ways to be unreachable, kept apart because the recovery differs: a name that never
            // resolved is a mistake to correct, while a resolved-but-retired agent is a real agent
            // whose work is already over.
            AgentDirectoryFailureCodes.NotFound =>
                $"No agent matches '{target}'. Call GetAgents for current agent_ids.",
            AgentMessageFailureCodes.UnknownTarget =>
                $"'{target}' has finished and can no longer be reached. Call GetAgents to see who is still live.",
            AgentDirectoryFailureCodes.AmbiguousName =>
                $"More than one agent is named '{target}'. Address it by agent_id instead.",
            AgentMessageFailureCodes.InboxFull =>
                $"'{target}' has too many messages pending. Wait for it to catch up, then retry.",
            AgentMessageFailureCodes.SelfDelivery => "You cannot send a message to yourself.",
            AgentMessageFailureCodes.InvalidSender => "Your agent is no longer active, so it cannot send new messages.",
            AgentMessageFailureCodes.UnknownCorrelation => "The 'in_response_to' message_id is not one you received.",
            AgentMessageFailureCodes.CorrelationClosed => "That message has already been answered.",
            AgentMessageFailureCodes.CorrelationNotAddressedToSender =>
                "That message was not addressed to you, so you cannot answer it.",
            AgentMessageFailureCodes.CorrelationDoesNotExpectReply =>
                "That message did not ask for an answer. Send it as a new message instead.",
            AgentMessageFailureCodes.CorrelationNotADelegation =>
                "Progress updates belong to a delegated task. Answer that message with 'response' instead.",
            AgentMessageFailureCodes.MissingCorrelation =>
                "That message type must name the message it follows. Set 'in_response_to', or send it "
                    + "as a 'question' or 'delegate_task' instead.",
            AgentMessageFailureCodes.TargetNotStarted =>
                $"'{target}' has not started yet. Poll it with CheckAgents and retry once it is running.",
            AgentMessageFailureCodes.TargetNotActive =>
                $"'{target}' is not running, so there is nothing in flight to steer. Send it a "
                    + "'question' or 'delegate_task' instead.",
            _ => $"The message to '{target}' was refused ({failureCode ?? "unknown"}).",
        };
    }

    private Task<ToolHandlerResult> HandleCheckAgentsToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var targets =
            ParseCommaSeparated(GetOptionalString(root, "agent_ids"))
            ?? throw new ArgumentException("The 'agent_ids' parameter is required.");

        var batch = WidenToCollaboration(_manager.CheckAgents(targets));
        return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(SerializeObservationBatch(batch)));
    }

    /// <summary>
    /// Fills in the entries the manager could not resolve from the collaboration directory, so
    /// CheckAgents answers for anything GetAgents can see rather than only for direct children.
    /// </summary>
    /// <remarks>
    /// Status only. Recent turns and the final result stay empty for an agent this one does not own,
    /// because reading another agent's work is the transcript policy's decision and not something a
    /// status check may quietly grant. <see cref="HandleWaitForAgentsToolAsync"/> deliberately does not
    /// go through here: waiting is direct-child only, and widening its probe would let an agent block on
    /// a peer it cannot influence.
    /// </remarks>
    private SubAgentObservationBatch WidenToCollaboration(SubAgentObservationBatch batch)
    {
        if (batch.NotFound == 0 || _manager.Collaboration is not { } collaboration)
        {
            return batch;
        }

        return new SubAgentObservationBatch
        {
            Entries =
            [
                .. batch.Entries.Select(entry =>
                    entry.IsFound || collaboration.Directory.Resolve(entry.Target).Entry is not { } found
                        ? entry
                        : entry with
                        {
                            AgentId = found.AgentId,
                            Name = found.Name,
                            Status = found.Status,
                            TemplateName = found.AgentType,
                        }
                ),
            ],
        };
    }

    private static string SerializeObservationBatch(SubAgentObservationBatch batch)
    {
        return JsonSerializer.Serialize(BuildObservationPayload(batch));
    }

    private static object BuildObservationPayload(SubAgentObservationBatch batch)
    {
        return new
        {
            requested = batch.Requested,
            running = batch.Running,
            terminal = batch.Terminal,
            not_found = batch.NotFound,
            agents = batch.Entries.Select(e => new
            {
                target = e.Target,
                agent_id = e.AgentId,
                name = e.Name,
                status = e.Status,
                template = e.TemplateName,
                task = e.Task,
                recent_turns = e.RecentTurns.Select(t => new
                {
                    type = t.MessageType,
                    tool = t.ToolName,
                    tool_args = t.ToolArgsPreview,
                    text = t.TextPreview,
                    time = t.Timestamp.ToString("o"),
                }),
                last_result = e.LastResult,
                send_to_parent_failed = e.SendToParentFailed,
                send_to_parent_error = e.SendToParentError,
            }),
        };
    }

    private Task<ToolHandlerResult> HandleGetAgentsToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        if (_manager.Collaboration is not { } collaboration)
        {
            // Unreachable through the advertised surface — the descriptor only exists under a
            // collaboration — but a stale tool call from an earlier turn must not throw.
            return Task.FromResult<ToolHandlerResult>(
                ToolHandlerResult.FromError("Agent collaboration is not enabled.", "collaboration_disabled")
            );
        }

        // Serialized rather than formatted: role and description are model-authored, so rendering them
        // into a hand-built listing is how one agent's description forges another agent's row.
        var payload = JsonSerializer.Serialize(
            new
            {
                collaboration_id = collaboration.Bundle.CollaborationId,
                your_agent_id = collaboration.AgentId,
                agents = collaboration
                    .Directory.Snapshot()
                    .Select(e => new
                    {
                        agent_id = e.AgentId,
                        name = e.Name,
                        role = e.Role,
                        description = e.Description,
                        kind = e.Kind.ToString(),
                        agent_type = e.AgentType,
                        parent_agent_id = e.ParentAgentId,
                        depth = e.StructuralDepth,
                        // Both depths, because they answer different questions and diverge: structural depth is
                        // where an agent sits, delegation depth is how much spawning budget reaching it spent,
                        // and a workflow controller hop advances one without the other.
                        structural_depth = e.StructuralDepth,
                        delegation_depth = e.DelegationDepth,
                        status = e.Status,
                        is_live = e.IsLive,
                        is_you = string.Equals(e.AgentId, collaboration.AgentId, StringComparison.Ordinal),
                        // Stated up front so the reader does not have to discover by refusal which transcripts
                        // it may read; the policy is evaluated here rather than assumed from the hierarchy.
                        transcript_readable = collaboration
                            .Bundle.EvaluateTranscriptAccess(collaboration.AgentId, e.AgentId)
                            .IsAllowed,
                    }),
            }
        );

        return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(payload));
    }

    private async Task<ToolHandlerResult> HandleWaitForAgentsToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var targets =
            ParseCommaSeparated(GetOptionalString(root, "agent_ids"))
            ?? throw new ArgumentException("The 'agent_ids' parameter is required.");

        var mode = (GetOptionalString(root, "mode") ?? "all").Trim().ToLowerInvariant();
        if (mode is not ("all" or "any"))
        {
            return ToolHandlerResult.FromError($"Unknown mode '{mode}'. Use 'all' or 'any'.", "invalid_mode");
        }

        // Resolve every target before waiting on any of them: a typo in one id would otherwise leave
        // the caller blocked on the rest with no indication that part of its request was nonsense.
        var probe = _manager.CheckAgents(targets);
        if (probe.NotFound > 0)
        {
            var unknown = probe.Entries.Where(e => !e.IsFound).Select(e => e.Target);
            return ToolHandlerResult.FromError(
                $"You have no sub-agent matching: {string.Join(", ", unknown)}. "
                    + "WaitForAgents only covers agents you spawned yourself.",
                "unknown_agent"
            );
        }

        if (!TryReadTimeoutSeconds(root, out var timeoutSeconds, out var timeoutError))
        {
            return ToolHandlerResult.FromError(timeoutError!, "invalid_args");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Every racer must complete rather than fault when the race is torn down: none of them is
        // awaited on the losing paths, so a cancellation fault here would surface as an unobserved
        // task exception long after this tool call is gone.
        var waits = targets
            .Select(t => AwaitQuietlyAsync(_manager.ObserveTargetCompletionAsync(t, linked.Token)))
            .ToArray();

        var completion = mode == "any" ? Task.WhenAny(waits) : Task.WhenAll(waits);
        var question = WatchForQuestionAsync(linked.Token);
        var timeout = timeoutSeconds is { } cap ? DelayQuietlyAsync(TimeSpan.FromSeconds(cap), linked.Token) : null;

        Task[] races = timeout is null ? [completion, question] : [completion, question, timeout];

        var winner = await Task.WhenAny(races);

        // Stop the losing races before reporting. The waits are non-destructive, so cancelling them
        // abandons the observation only — every agent listed keeps running either way.
        await linked.CancelAsync();

        // Always observed, and always BEFORE this method can leave by any route: a question that
        // claimed its one interrupt but is not being reported has to give the claim back, or no later
        // wait would ever be woken by it. Being cancelled counts as not reporting it — the caller is
        // about to receive an exception rather than the question, so the claim is just as lost as when
        // another racer won. (The await cannot hang: `linked` is already cancelled, which settles it.)
        var reported = winner == question && !cancellationToken.IsCancellationRequested;
        var asked = await question;
        if (asked is not null && !reported)
        {
            _manager.Collaboration?.Bundle.Ledger.ReleaseWaitInterrupt(asked.MessageId);
            asked = null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var status =
            asked is not null ? "question_received"
            : winner == completion ? "completed"
            : "timeout";

        return ToolHandlerResult.FromText(
            JsonSerializer.Serialize(
                new
                {
                    status,
                    mode,
                    question = asked,
                    agents = BuildObservationPayload(_manager.CheckAgents(targets)),
                }
            )
        );
    }

    /// <summary>
    /// Reduces one completion wait to "the wait is over, somehow". The outcome is read back from the
    /// manager afterwards, never from this task.
    /// </summary>
    /// <remarks>
    /// The fault is deliberately NOT propagated and deliberately NOT treated as a verdict. It is not a
    /// reliable one: a run that FAILED faults this task and is a perfectly good terminal state (see
    /// <c>WaitAgent_ReportsATerminalFailureInsteadOfBlockingForever</c>), while a queued spawn whose
    /// start throws faults it identically and leaves nothing behind at all. What separates the two is
    /// whether the agent is still tracked when the wait ends, which is what the callers check.
    /// Propagating the fault here would only produce an unobserved task exception on the losing racer.
    /// </remarks>
    private static async Task AwaitQuietlyAsync(Task<string> wait)
    {
        try
        {
            _ = await wait;
        }
        catch (Exception)
        {
            // Intentionally swallowed: the outcome is reported from a fresh observation.
        }
    }

    /// <summary>A delay that ends quietly when the race that owns it is torn down.</summary>
    private static async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Another racer won; expiry is no longer interesting.
        }
    }

    /// <summary>
    /// Completes when someone asks THIS agent a question, so a waiting agent cannot become the reason
    /// another one is blocked. Only Question interrupts: updates and steers carry no obligation, and
    /// waking for them would make every wait unpredictable. Yields null when the race is torn down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fires on admission rather than delivery, so a wait can end for a question whose delivery later
    /// fails. That asymmetry is deliberate: ending a wait early costs one turn, whereas missing the
    /// question that the waiter alone can answer is a deadlock.
    /// </para>
    /// <para>
    /// A question is claimed before it is reported, and a claim is granted once. A question stays open
    /// until it is answered, so without the claim the opening sweep would rediscover the same
    /// still-unanswered question on every subsequent wait and return instantly — an agent that chose
    /// not to answer could then never wait again. The claim bounds each question to one interruption
    /// while leaving it open, and therefore still answerable whenever the agent gets to it.
    /// </para>
    /// </remarks>
    private async Task<QuestionInterrupt?> WatchForQuestionAsync(CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource<QuestionInterrupt?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(() => signal.TrySetResult(null));

        if (_manager.Collaboration is not { } collaboration)
        {
            // Unreachable through the advertised surface; kept so this never becomes a live race that
            // completes instantly and reports a question nobody asked.
            return await signal.Task;
        }

        var ledger = collaboration.Bundle.Ledger;

        QuestionInterrupt Describe(string messageId, string fromAgentId) =>
            new(messageId, fromAgentId, collaboration.Directory.FindById(fromAgentId)?.Name);

        // Claim first, report second, and give the claim back if the report lost a race — a claim that
        // was taken but never surfaced would silently spend the one interrupt the question gets.
        bool TryReport(string messageId, string fromAgentId)
        {
            if (!ledger.TryClaimWaitInterrupt(messageId))
            {
                return false;
            }

            if (signal.TrySetResult(Describe(messageId, fromAgentId)))
            {
                return true;
            }

            ledger.ReleaseWaitInterrupt(messageId);
            return false;
        }

        void OnAdmitted(AgentMessageAdmittedNotice notice)
        {
            if (
                notice.MessageType == AgentMessageType.Question
                && string.Equals(notice.ToAgentId, collaboration.AgentId, StringComparison.Ordinal)
            )
            {
                _ = TryReport(notice.MessageId, notice.FromAgentId);
            }
        }

        ledger.MessageAdmitted += OnAdmitted;
        try
        {
            // Subscribe-then-sweep, in that order: a question admitted before the handler attached
            // would otherwise stay unnoticed until an unrelated second question arrived.
            foreach (var open in ledger.GetOpenInbound(collaboration.AgentId))
            {
                if (open.MessageType == AgentMessageType.Question && TryReport(open.MessageId, open.FromAgentId))
                {
                    break;
                }
            }

            return await signal.Task;
        }
        finally
        {
            ledger.MessageAdmitted -= OnAdmitted;
        }
    }

    /// <summary>The question that ended a wait, as the waiting agent is told about it.</summary>
    /// <remarks>
    /// A declared shape rather than an anonymous one because the wait handler has to read
    /// <see cref="MessageId"/> back to release a claim it did not end up reporting.
    /// </remarks>
    private sealed record QuestionInterrupt(
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("from_agent_id")] string FromAgentId,
        [property: JsonPropertyName("from_name")] string? FromName
    );

    private Task<ToolHandlerResult> HandleCheckAgentToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var agentId =
            GetOptionalString(root, "agent_id") ?? throw new ArgumentException("The 'agent_id' parameter is required.");

        // An unknown/stale/mistyped agent id is a MODEL mistake (e.g. polling with the wrong id), not a host
        // fault — return a helpful tool result listing the valid ids rather than throwing, which would surface
        // as an "Error executing tool call" and derail the loop.
        if (!_manager.TryPeek(agentId, out var status))
        {
            return Task.FromResult<ToolHandlerResult>(
                ToolHandlerResult.FromError(DescribeUnknownAgent(agentId, "CheckAgent"), "unknown_agent")
            );
        }

        return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(status));
    }

    /// <summary>
    /// Blocks on ONE sub-agent's terminal state, then reports exactly what CheckAgent would have.
    /// </summary>
    /// <remarks>
    /// Deliberately built on the same completion latch as <c>WaitForAgents</c>
    /// (<see cref="SubAgentManager.ObserveTargetCompletionAsync"/>) rather than a second polling loop:
    /// the latch is set for a failed or abandoned run too, which is what stops the outcome the caller
    /// most needs to hear about from being the one that hangs it. The wait is non-destructive —
    /// timing out or being cancelled abandons the observation only, never the agent.
    /// </remarks>
    private async Task<ToolHandlerResult> HandleWaitAgentToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var agentId =
            GetOptionalString(root, "agent_id") ?? throw new ArgumentException("The 'agent_id' parameter is required.");

        // Resolve BEFORE waiting: a mistyped id would otherwise block until the timeout and then report
        // a "still running" agent that never existed.
        if (!_manager.TryPeek(agentId, out _))
        {
            return ToolHandlerResult.FromError(DescribeUnknownAgent(agentId, WaitAgentToolName), "unknown_agent");
        }

        if (!TryReadTimeoutSeconds(root, out var timeoutSeconds, out var timeoutError))
        {
            return ToolHandlerResult.FromError(timeoutError!, "invalid_args");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Quiet racer: the loser is never awaited, so a cancellation fault would surface as an
        // unobserved task exception long after this tool call is gone.
        var completion = AwaitQuietlyAsync(_manager.ObserveTargetCompletionAsync(agentId, linked.Token));
        var timeout = timeoutSeconds is { } cap ? DelayQuietlyAsync(TimeSpan.FromSeconds(cap), linked.Token) : null;

        var winner = timeout is null ? await Task.WhenAny(completion) : await Task.WhenAny(completion, timeout);

        await linked.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();

        // Re-read rather than reuse the pre-wait snapshot: the whole point of the wait is the status and
        // result the agent reached while it was blocked. A MISS here is not a mistyped id — that was
        // rejected before the wait began — it means the agent stopped being tracked WHILE we waited: a
        // queued spawn whose start threw, or a disposed manager cancelling it with the MANAGER's token.
        // Both end the wait without ending the agent, and TryPeek's out value on a miss is the EMPTY
        // STRING, which is not parseable JSON — deserializing it unconditionally threw out of the tool
        // call, and calling it "completed" told the model its child had succeeded.
        var agent = _manager.TryPeek(agentId, out var observed)
            ? JsonSerializer.Deserialize<JsonElement>(observed)
            : (JsonElement?)null;

        return ToolHandlerResult.FromText(
            JsonSerializer.Serialize(
                new
                {
                    status = agent is null ? "unavailable"
                    : winner == completion ? "completed"
                    : "timeout",
                    detail = agent is not null
                        ? null
                        : $"The wait on '{agentId}' ended without the agent reaching a terminal state: it stopped "
                            + "being tracked before it could produce a result — its start failed, or the sub-agent "
                            + "system shut down. There is nothing to collect. Spawn it again if you still need the work.",
                    agent,
                }
            )
        );
    }

    /// <summary>
    /// Explains an unknown agent id to the model, naming the ids that would have worked.
    /// </summary>
    /// <remarks>
    /// Shared by CheckAgent and WaitAgent so both mistakes are corrected the same way. The listing is
    /// sorted (ordinal) so repeated failures read identically instead of shuffling, and capped at
    /// <see cref="MaxListedAgentIds"/> — a cap that ANNOUNCES itself, because a silently truncated list
    /// is worse than no list: it invites the model to conclude the id it wanted does not exist.
    /// </remarks>
    private string DescribeUnknownAgent(string agentId, string toolName)
    {
        var known = _manager.KnownAgentIds();
        if (known.Count == 0)
        {
            return $"No sub-agent with id '{agentId}'. No sub-agents are currently tracked — a synchronous Agent "
                + $"call returns its result inline ({toolName} is unnecessary), and any background agents have completed.";
        }

        var sorted = known.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var listed = sorted.Take(MaxListedAgentIds);
        var suffix =
            sorted.Length > MaxListedAgentIds ? $" (showing {MaxListedAgentIds} of {sorted.Length})" : string.Empty;

        return $"No sub-agent with id '{agentId}'. Use one of the ids the Agent tool returned: "
            + $"{string.Join(", ", listed)}{suffix}.";
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    /// <summary>
    /// Collapses a blank or whitespace-only correlation id to null. Some callers cannot omit an
    /// optional string parameter and send "" or whitespace where they mean "absent" instead of a true
    /// JSON null — without this, that value would survive as a non-null <c>in_response_to</c> and be
    /// treated as an attempted (but unknown) correlation rather than as no correlation at all. A real,
    /// non-blank id — even one that turns out not to exist — is returned unchanged.
    /// </summary>
    private static string? NormalizeCorrelationId(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool? GetOptionalBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            // Some models emit booleans as strings ("true"/"false").
            JsonValueKind.String when bool.TryParse(prop.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Reads the optional <c>timeout_seconds</c> cap shared by <c>WaitAgent</c> and
    /// <c>WaitForAgents</c>, separating ABSENT from PRESENT-BUT-UNUSABLE.
    /// <para>
    /// Both tools tell the model to pass this "so a wedged agent cannot stall you indefinitely".
    /// Parsing it with <see cref="GetOptionalInt"/> collapsed every unusable value — a malformed
    /// string, 0, a negative — onto the same <c>null</c> as an omitted parameter, and the
    /// <c>is &gt; 0</c> gate then turned all of them into the unbounded wait the model passed the
    /// argument to avoid, with nothing said about it. A cap the caller asked for and did not get is
    /// an error, not a default: reject it so the model can correct the call.
    /// </para>
    /// </summary>
    /// <param name="root">The tool call's parsed arguments.</param>
    /// <param name="seconds">The requested cap, or <c>null</c> for "no cap" (absent or explicit null).</param>
    /// <param name="error">The rejection message when the value is present but unusable.</param>
    /// <returns><c>true</c> when the argument is usable (including when it is absent).</returns>
    private static bool TryReadTimeoutSeconds(JsonElement root, out int? seconds, out string? error)
    {
        seconds = null;
        error = null;

        // Omitted keeps the established contract: an optional cap that was not requested means the
        // wait is bounded only by the caller's own cancellation. An explicit null is the same
        // statement — models routinely emit one for a parameter they chose not to set.
        if (!root.TryGetProperty("timeout_seconds", out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        int? requested = prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var number) => number,
            // Some models emit integers as strings ("30").
            JsonValueKind.String when int.TryParse(prop.GetString(), out var fromText) => fromText,
            _ => null,
        };

        // One rejection for every unusable shape: unparseable, zero, and negative alike. Zero and
        // negative are rejected rather than read as "expire immediately", because a wait that
        // returns "timeout" before observing anything is indistinguishable from a wedged agent.
        if (requested is not > 0)
        {
            error =
                "The 'timeout_seconds' parameter must be a positive whole number of seconds. "
                + "Omit it entirely to wait without a cap.";
            return false;
        }

        seconds = requested;
        return true;
    }

    private static int? GetOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var number) => number,
            // Some models emit integers as strings ("2").
            JsonValueKind.String when int.TryParse(prop.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string[]? ParseCommaSeparated(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
