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
 * - `enabledTools` - unqualified, non-built-in tools. Omission on update preserves the mode's stored
 *   allowlist; explicit `null` means "all", including tools added to the catalog later.
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

/** The group named by a stored `group:tool` id, or undefined when the id is unqualified. */
export function groupOfId(id: string): string | undefined {
  const separator = id.indexOf(':');
  if (separator <= 0) return undefined;
  const group = id.slice(0, separator);
  return isQualifiedGroup(group) ? group : undefined;
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
  enabledTools?: string[] | null;
  enabledBuiltInTools?: string[];
  enabledCapabilityTools?: string[];
}

/**
 * Projects the editor's flat selection back onto the three persisted fields.
 *
 * `enabledTools` is written as an explicit `null` when every unqualified non-built-in tool is
 * ticked, so the server's presence-aware update contract reads it as "all tools, including ones
 * added later" rather than "leave the stored allowlist alone" — the meaning an omitted key would
 * have on update. The other two are written explicitly: `enabledBuiltInTools` because leaving it
 * undefined would re-enable the enabledTools fallback, and `enabledCapabilityTools` because
 * undefined there means "legacy defaults" rather than "what the user just chose".
 *
 * `current` is the mode being edited, and it is what makes a selection the catalog could not show
 * survive a save. Two shapes of that:
 *
 * - A whole group with no rows. A deployment whose provider contributes no server-side built-ins
 *   serves a catalog with no `builtin` rows at all; without this the editor would render nothing for
 *   that group, see nothing ticked, and write `[]` — silently stripping the mode's `web_search`.
 * - A single capability id with no row. The sandbox listing is probed live, so a failed probe still
 *   returns the baseline plus a wildcard row while omitting the plugin-provided tools. A stored
 *   `sandbox:SomePluginTool` would then be absent from the catalog, read as unticked, and written
 *   away — narrowing a hand-curated mode just because discovery was degraded when it was opened.
 *
 * In both cases the rule is the same: a choice the user was never shown is not a choice the user
 * revoked. Rows that WERE rendered are still honoured, so an actual deselection still writes `[]`.
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

  for (const tool of tools) {
    const id = toolId(tool);
    const group = toolGroup(tool);

    if (isQualifiedGroup(group)) {
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

  // Stored capability ids the catalog never offered a row for. Appended after the rendered ones
  // rather than merged into catalog order, because the catalog by definition has no position for
  // them; their own stored order is the only stable one available.
  const catalogIds = new Set(tools.map(toolId));
  const preservedCapabilityIds = (current?.enabledCapabilityTools ?? []).filter((id) => {
    if (catalogIds.has(id)) return false;
    const group = groupOfId(id);
    return group !== undefined && !isRedundantUnderWildcard(id, group, chosen);
  });

  return {
    enabledTools:
      unqualifiedCount === 0
        ? current?.enabledTools
        : enabledTools.length === unqualifiedCount
          ? null
          : enabledTools,
    enabledBuiltInTools: builtInCount === 0 ? current?.enabledBuiltInTools : enabledBuiltInTools,
    // No `qualifiedCount === 0` special case: when the catalog shows no qualified rows at all,
    // every stored id is unrenderable and preservation already carries the whole list through.
    enabledCapabilityTools: [...enabledCapabilityTools, ...preservedCapabilityIds],
  };
}

function isRedundantUnderWildcard(id: string, group: string, chosen: Set<string>): boolean {
  const wildcard = wildcardId(group);
  return id !== wildcard && chosen.has(wildcard);
}
