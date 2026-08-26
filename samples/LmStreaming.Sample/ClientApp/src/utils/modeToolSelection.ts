import {
  BUILT_IN_TOOL_GROUP,
  QUALIFIED_TOOL_GROUPS,
  WILDCARD_TOOL,
  type ChatMode,
  type ToolDefinition,
} from '@/types/chatMode';

/**
 * Splitting and joining the Modes editor's flat tool selection against the three fields a mode
 * actually persists.
 *
 * A mode stores its tool choice in three places with three different null rules, and the editor
 * shows one list. These helpers are the only place that translation happens, so the rules are
 * stated once and can be tested without mounting a component:
 *
 * - `enabledTools` - unqualified, non-built-in tools. `undefined` means "all", including tools
 *   added to the catalog later.
 * - `enabledBuiltInTools` - server-side built-ins. `undefined` falls back to `enabledTools`, which
 *   is why the editor always writes it explicitly once the user has saved.
 * - `enabledCapabilityTools` - qualified `group:tool` ids. `undefined` means "the legacy defaults"
 *   (sub-agents on, no sandbox, no workflow tools), NOT "none".
 */

/** The id a mode stores for this catalog row. */
export function toolId(tool: ToolDefinition): string {
  return tool.id ?? tool.name;
}

/** The group a catalog row belongs to, defaulting to the sample group for older payloads. */
export function toolGroup(tool: ToolDefinition): string {
  return tool.group ?? 'sample';
}

/** Whether `group` addresses its tools by a `group:tool` id. */
export function isQualifiedGroup(group: string): boolean {
  return (QUALIFIED_TOOL_GROUPS as readonly string[]).includes(group);
}

/** The `group:*` id that selects everything in `group`. */
export function wildcardId(group: string): string {
  return `${group}:${WILDCARD_TOOL}`;
}

/** One section of the grouped checkbox list. */
export interface ToolGroupView {
  key: string;
  label: string;
  tools: ToolDefinition[];
  /** True for `sandbox` / `subagents` / `workflow`. */
  qualified: boolean;
  /** The `group:*` row, when this group has one. */
  wildcard?: ToolDefinition;
  /** True when any tool here makes each conversation open a sandbox session. */
  requiresSandbox: boolean;
  /** Set when this group's listing may be incomplete. */
  catalogWarning?: string;
}

/**
 * Buckets a flat catalog into display sections, preserving the server's ordering so the editor
 * never has to hard-code a group order that could drift from the catalog.
 */
export function groupTools(tools: ToolDefinition[]): ToolGroupView[] {
  const views: ToolGroupView[] = [];
  const byKey = new Map<string, ToolGroupView>();

  for (const tool of tools) {
    const key = toolGroup(tool);
    let view = byKey.get(key);
    if (!view) {
      view = {
        key,
        label: tool.groupLabel ?? key,
        tools: [],
        qualified: isQualifiedGroup(key),
        requiresSandbox: false,
      };
      byKey.set(key, view);
      views.push(view);
    }

    if (tool.isWildcard) {
      view.wildcard = tool;
    } else {
      view.tools.push(tool);
    }

    if (tool.requiresSandbox) view.requiresSandbox = true;
    if (tool.catalogWarning) view.catalogWarning = tool.catalogWarning;
  }

  return views;
}

/**
 * The ids to tick when the editor opens `mode`.
 *
 * `mode` is null when creating a new mode, in which case the selection is what a brand-new
 * conversation gets today: every unqualified tool, plus the legacy capability defaults.
 */
export function selectionFromMode(
  mode: ChatMode | null | undefined,
  tools: ToolDefinition[]
): string[] {
  const selected: string[] = [];

  // Built-ins: an absent enabledBuiltInTools falls back to enabledTools on the server, so the
  // editor must apply the same fallback or a mode would appear to have built-ins it does not get.
  const builtInAllow = mode?.enabledBuiltInTools ?? mode?.enabledTools;
  const toolAllow = mode?.enabledTools;
  const capabilityAllow = mode?.enabledCapabilityTools;

  for (const tool of tools) {
    const id = toolId(tool);
    const group = toolGroup(tool);

    if (isQualifiedGroup(group)) {
      if (capabilityAllow === undefined || capabilityAllow === null) {
        // No capability selection recorded: show exactly what the mode already gets, so saving
        // without touching anything does not narrow it.
        if (tool.isLegacyDefault) selected.push(id);
      } else if (capabilityAllow.includes(id)) {
        selected.push(id);
      }
      continue;
    }

    const allow = group === BUILT_IN_TOOL_GROUP ? builtInAllow : toolAllow;
    if (allow === undefined || allow === null || allow.includes(id)) {
      selected.push(id);
    }
  }

  return selected;
}

/** The three persisted fields implied by a flat selection. */
export interface ModeToolFields {
  enabledTools?: string[];
  enabledBuiltInTools?: string[];
  enabledCapabilityTools?: string[];
}

/**
 * Projects the editor's flat selection back onto the three persisted fields.
 *
 * `enabledTools` is left undefined when every unqualified non-built-in tool is ticked, preserving
 * the "all tools, including ones added later" meaning that a full explicit list would quietly lose.
 * The other two are written explicitly: `enabledBuiltInTools` because leaving it undefined would
 * re-enable the enabledTools fallback, and `enabledCapabilityTools` because undefined there means
 * "legacy defaults" rather than "what the user just chose".
 *
 * `current` is the mode being edited, and it is what makes a group the catalog could not show
 * survive a save. A deployment whose provider contributes no server-side built-ins serves a catalog
 * with no `builtin` rows at all; without this the editor would render nothing for that group, see
 * nothing ticked, and write `[]` — silently stripping the mode's `web_search`. A group with no rows
 * to choose from is a group the user was never shown, so its stored value is carried through
 * untouched rather than being treated as a deselection.
 */
export function selectionToModeFields(
  selected: string[],
  tools: ToolDefinition[],
  current?: ChatMode | null
): ModeToolFields {
  const chosen = new Set(selected);
  const enabledTools: string[] = [];
  const enabledBuiltInTools: string[] = [];
  const enabledCapabilityTools: string[] = [];
  let unqualifiedCount = 0;
  let builtInCount = 0;
  let qualifiedCount = 0;

  for (const tool of tools) {
    const id = toolId(tool);
    const group = toolGroup(tool);

    if (isQualifiedGroup(group)) {
      qualifiedCount++;
      // A group:* row makes the individual rows redundant; storing only the wildcard is what lets
      // a tool installed later still be covered.
      if (chosen.has(id) && !isRedundantUnderWildcard(id, group, chosen)) {
        enabledCapabilityTools.push(id);
      }
      continue;
    }

    if (group === BUILT_IN_TOOL_GROUP) {
      builtInCount++;
      if (chosen.has(id)) enabledBuiltInTools.push(id);
      continue;
    }

    unqualifiedCount++;
    if (chosen.has(id)) enabledTools.push(id);
  }

  return {
    enabledTools:
      unqualifiedCount === 0
        ? current?.enabledTools
        : enabledTools.length === unqualifiedCount
          ? undefined
          : enabledTools,
    enabledBuiltInTools: builtInCount === 0 ? current?.enabledBuiltInTools : enabledBuiltInTools,
    enabledCapabilityTools:
      qualifiedCount === 0 ? current?.enabledCapabilityTools : enabledCapabilityTools,
  };
}

function isRedundantUnderWildcard(id: string, group: string, chosen: Set<string>): boolean {
  const wildcard = wildcardId(group);
  return id !== wildcard && chosen.has(wildcard);
}
