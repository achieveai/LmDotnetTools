/**
 * Represents a chat mode that defines a persona, system prompt, and available tools.
 */
export interface ChatMode {
  id: string;
  name: string;
  description?: string;
  systemPrompt: string;
  enabledTools?: string[];
  /**
   * Server-side provider built-ins (e.g. `web_search`). When absent the server falls back to
   * {@link enabledTools} for backward compatibility.
   */
  enabledBuiltInTools?: string[];
  /**
   * Qualified `group:tool` selections for the sandbox / sub-agent / workflow families.
   *
   * Absent (undefined) is NOT the same as empty: absent means the mode predates capability
   * selection and keeps the legacy defaults (sub-agents on, no sandbox, no workflow tools), while
   * an empty array is an explicit "none".
   */
  enabledCapabilityTools?: string[];
  /**
   * Optional prompt fragment folded into the system prompt of every sub-agent spawned under a
   * conversation in this mode. Absent means sub-agent prompts are unchanged.
   */
  subAgentPrompt?: string;
  /**
   * Where the fragment lands relative to each sub-agent's own prompt. Absent defaults to
   * 'append' when a fragment is present.
   */
  subAgentPromptPlacement?: 'prepend' | 'append';
  /**
   * Tool ids guaranteed to every sub-agent spawned in this mode, even when an agent template
   * restricts its own tool list (#623). Uses the mode's tool-id language: bare names, qualified
   * `group:tool` ids, or `group:*` wildcards. Absent or empty means "not enforced" — spawns keep
   * today's behavior exactly.
   */
  subAgentRequiredTools?: string[];
  /** Provider-neutral reasoning effort requested for children in this mode. */
  subAgentReasoningEffort?: string;
  /** Authoritative child tier keyed by canonical registered sub-agent type. */
  subAgentModelIntelligenceByType?: Record<string, number>;
  /** Review-child fallback tier for canonical `code-reviewer:*` types absent from the map. */
  defaultSubAgentModelIntelligence?: number;
  isSystemDefined: boolean;
  createdAt: number;
  updatedAt: number;
}

/**
 * Request body for creating or updating a chat mode.
 */
export interface ChatModeCreateUpdate {
  name: string;
  description?: string;
  systemPrompt: string;
  enabledTools?: string[];
  enabledBuiltInTools?: string[];
  enabledCapabilityTools?: string[];
  /** Sub-agent prompt fragment; see {@link ChatMode.subAgentPrompt}. */
  subAgentPrompt?: string;
  /** 'prepend' | 'append'; the server refuses anything else with 400. */
  subAgentPromptPlacement?: 'prepend' | 'append';
  /** Guaranteed sub-agent tools; see {@link ChatMode.subAgentRequiredTools}. Omit for "not enforced". */
  subAgentRequiredTools?: string[];
  /** Provider-neutral reasoning effort requested for children in this mode. */
  subAgentReasoningEffort?: string;
  /** Authoritative child tier keyed by canonical registered sub-agent type. */
  subAgentModelIntelligenceByType?: Record<string, number>;
  /** Review-child fallback tier for canonical `code-reviewer:*` types absent from the map. */
  defaultSubAgentModelIntelligence?: number;
}

/**
 * Request body for copying a chat mode.
 */
export interface ChatModeCopy {
  newName: string;
}

/**
 * The groups the tool catalog buckets selectable tools into. The three qualified groups
 * (`sandbox`, `subagents`, `workflow`) address their tools by a `group:tool` id; every other group
 * uses the bare tool name it has always used.
 */
export const QUALIFIED_TOOL_GROUPS = ['sandbox', 'subagents', 'workflow'] as const;

/** The group whose tools are selected through `enabledBuiltInTools`. */
export const BUILT_IN_TOOL_GROUP = 'builtin';

/** The qualified group whose tools come from a live sandbox gateway rather than a static roster. */
export const SANDBOX_TOOL_GROUP = 'sandbox';

/** The token that selects every tool in a qualified group, now and in future. */
export const WILDCARD_TOOL = '*';

/**
 * Represents a tool definition served by `/api/tools`.
 */
export interface ToolDefinition {
  /** Display name. For a wildcard row this is a label such as "All workspace tools". */
  name: string;
  /**
   * The id a mode stores for this tool: the bare name for unqualified groups, `group:tool` for the
   * qualified ones, and `group:*` for a wildcard row. Older payloads omit it, in which case the
   * name is the id.
   */
  id?: string;
  description?: string;
  /** Group key, e.g. `sandbox`. Older payloads omit it. */
  group?: string;
  /** Human-readable section heading for the group. */
  groupLabel?: string;
  /** True for the synthetic "everything in this group" row. */
  isWildcard?: boolean;
  /** True when selecting this tool makes every conversation in the mode open a sandbox session. */
  requiresSandbox?: boolean;
  /**
   * True when a mode that records no capability selection still gets this tool. Only the qualified
   * groups set it; it lets the editor pre-tick exactly what a legacy mode already has instead of
   * re-deriving the server's defaults here.
   */
  isLegacyDefault?: boolean;
  /** Set when the catalog for this tool's group may be incomplete (e.g. the gateway was down). */
  catalogWarning?: string;
}

/**
 * Request body for switching conversation mode.
 */
export interface SwitchModeRequest {
  modeId: string;
}

/**
 * Response from switching conversation mode.
 */
export interface SwitchModeResponse {
  modeId: string;
  modeName: string;
}
