import { describe, expect, it } from 'vitest';
import type { ChatMode, ToolDefinition } from '@/types/chatMode';
import {
  groupTools,
  selectionFromMode,
  selectionToModeFields,
} from '@/utils/modeToolSelection';

/**
 * A stand-in for what `/api/tools` returns: one row per group, with the qualified groups carrying
 * `group:tool` ids and a wildcard row.
 */
const catalog: ToolDefinition[] = [
  { name: 'web_search', id: 'web_search', group: 'builtin', groupLabel: 'Built-in (server-side)' },
  { name: 'calculate', id: 'calculate', group: 'sample', groupLabel: 'Sample tools' },
  { name: 'add-task', id: 'add-task', group: 'tasks', groupLabel: 'Tasks' },
  {
    name: 'All sub-agent tools',
    id: 'subagents:*',
    group: 'subagents',
    groupLabel: 'Sub-agents',
    isWildcard: true,
  },
  {
    name: 'Agent',
    id: 'subagents:Agent',
    group: 'subagents',
    groupLabel: 'Sub-agents',
    isLegacyDefault: true,
  },
  {
    name: 'CheckAgents',
    id: 'subagents:CheckAgents',
    group: 'subagents',
    groupLabel: 'Sub-agents',
    isLegacyDefault: false,
  },
  {
    name: 'All workspace tools',
    id: 'sandbox:*',
    group: 'sandbox',
    groupLabel: 'Workspace (sandbox)',
    isWildcard: true,
    requiresSandbox: true,
  },
  {
    name: 'Bash',
    id: 'sandbox:Bash',
    group: 'sandbox',
    groupLabel: 'Workspace (sandbox)',
    requiresSandbox: true,
  },
  {
    name: 'Read',
    id: 'sandbox:Read',
    group: 'sandbox',
    groupLabel: 'Workspace (sandbox)',
    requiresSandbox: true,
  },
];

function mode(overrides: Partial<ChatMode>): ChatMode {
  return {
    id: 'm1',
    name: 'Mode',
    systemPrompt: 'p',
    isSystemDefined: false,
    createdAt: 0,
    updatedAt: 0,
    ...overrides,
  };
}

describe('groupTools', () => {
  it('buckets rows by group and pulls out the wildcard row', () => {
    const groups = groupTools(catalog);

    expect(groups.map((g) => g.key)).toEqual([
      'builtin',
      'sample',
      'tasks',
      'subagents',
      'sandbox',
    ]);

    const sandbox = groups.find((g) => g.key === 'sandbox')!;
    expect(sandbox.wildcard?.id).toBe('sandbox:*');
    // The wildcard is not also a normal row, or it would be counted as a tool.
    expect(sandbox.tools.map((t) => t.id)).toEqual(['sandbox:Bash', 'sandbox:Read']);
    expect(sandbox.qualified).toBe(true);
    expect(sandbox.requiresSandbox).toBe(true);
  });

  it('marks unqualified groups as unqualified and gives them no wildcard', () => {
    const groups = groupTools(catalog);
    const sample = groups.find((g) => g.key === 'sample')!;

    expect(sample.qualified).toBe(false);
    expect(sample.wildcard).toBeUndefined();
  });

  it('surfaces a catalog warning on the group that carries it', () => {
    const groups = groupTools([
      { ...catalog[7], catalogWarning: 'gateway unreachable' },
    ]);

    expect(groups[0].catalogWarning).toBe('gateway unreachable');
  });
});

describe('selectionFromMode', () => {
  it('ticks everything unqualified when the mode enables all tools', () => {
    const selected = selectionFromMode(mode({}), catalog);

    expect(selected).toContain('web_search');
    expect(selected).toContain('calculate');
    expect(selected).toContain('add-task');
  });

  it('pre-ticks the legacy capability defaults when no selection is recorded', () => {
    // A mode that predates capability selection still gets the legacy sub-agent surface, so the
    // editor has to show it or the first save would silently strip it.
    const selected = selectionFromMode(mode({}), catalog);

    expect(selected).toContain('subagents:Agent');
    expect(selected).not.toContain('subagents:CheckAgents');
    expect(selected).not.toContain('sandbox:Bash');
    expect(selected).not.toContain('sandbox:*');
  });

  it('treats an empty capability list as an explicit none, not as legacy', () => {
    const selected = selectionFromMode(mode({ enabledCapabilityTools: [] }), catalog);

    expect(selected).not.toContain('subagents:Agent');
  });

  it('reads an explicit capability selection verbatim', () => {
    const selected = selectionFromMode(
      mode({ enabledCapabilityTools: ['sandbox:*', 'subagents:CheckAgents'] }),
      catalog
    );

    expect(selected).toContain('sandbox:*');
    expect(selected).toContain('subagents:CheckAgents');
    expect(selected).not.toContain('subagents:Agent');
  });

  it('applies the server-side fallback from enabledBuiltInTools to enabledTools', () => {
    // The server reads enabledTools when enabledBuiltInTools is absent; showing built-ins as
    // enabled here when the server would disable them would be a lie about what the mode does.
    const selected = selectionFromMode(
      mode({ enabledTools: ['calculate'] }),
      catalog
    );

    expect(selected).not.toContain('web_search');
    expect(selected).toContain('calculate');
    expect(selected).not.toContain('add-task');
  });

  it('prefers an explicit enabledBuiltInTools over the fallback', () => {
    const selected = selectionFromMode(
      mode({ enabledTools: [], enabledBuiltInTools: ['web_search'] }),
      catalog
    );

    expect(selected).toContain('web_search');
    expect(selected).not.toContain('calculate');
  });

  it('ticks the legacy defaults for a brand-new mode', () => {
    const selected = selectionFromMode(null, catalog);

    expect(selected).toContain('calculate');
    expect(selected).toContain('subagents:Agent');
    expect(selected).not.toContain('sandbox:Bash');
  });
});

describe('selectionToModeFields', () => {
  it('writes an explicit null for enabledTools when every unqualified tool is ticked', () => {
    // Explicit null means "all, including tools added later" under the server's presence-aware
    // update contract; an exhaustive list would freeze the mode to today's catalog, and an
    // omitted key would instead mean "leave the stored allowlist alone".
    const fields = selectionToModeFields(
      ['web_search', 'calculate', 'add-task'],
      catalog
    );

    expect(fields.enabledTools).toBeNull();
  });

  it('serializes the all-ticked enabledTools as a present, explicit null — not an omitted key', () => {
    // The server's ChatModeCreateUpdate is presence-aware: JSON.stringify drops undefined-valued
    // keys but keeps an explicit null, and that distinction is exactly what "all tools including
    // ones added later" vs. "leave the stored allowlist alone" hinges on.
    const fields = selectionToModeFields(['web_search', 'calculate', 'add-task'], catalog);

    const wire = JSON.parse(JSON.stringify(fields));
    expect('enabledTools' in wire).toBe(true);
    expect(wire.enabledTools).toBeNull();
  });

  it('writes an explicit enabledTools once anything is unticked', () => {
    const fields = selectionToModeFields(['web_search', 'calculate'], catalog);

    expect(fields.enabledTools).toEqual(['calculate']);
  });

  it('always writes enabledBuiltInTools explicitly', () => {
    // Leaving it undefined would re-enable the server's enabledTools fallback and quietly grant
    // built-ins the user just unticked.
    const fields = selectionToModeFields(['calculate', 'add-task'], catalog);

    expect(fields.enabledBuiltInTools).toEqual([]);
  });

  it('always writes enabledCapabilityTools explicitly, even when empty', () => {
    const fields = selectionToModeFields(['calculate'], catalog);

    expect(fields.enabledCapabilityTools).toEqual([]);
  });

  it('drops rows made redundant by their group wildcard', () => {
    // Storing sandbox:* alone is what keeps covering a tool a marketplace plugin adds later.
    const fields = selectionToModeFields(['sandbox:*', 'sandbox:Bash'], catalog);

    expect(fields.enabledCapabilityTools).toEqual(['sandbox:*']);
  });

  it('keeps named rows when their group has no wildcard selected', () => {
    const fields = selectionToModeFields(['sandbox:Read'], catalog);

    expect(fields.enabledCapabilityTools).toEqual(['sandbox:Read']);
  });

  it('round-trips an explicit selection unchanged', () => {
    const original = mode({
      enabledTools: ['calculate'],
      enabledBuiltInTools: ['web_search'],
      enabledCapabilityTools: ['sandbox:*', 'subagents:Agent'],
    });

    const fields = selectionToModeFields(selectionFromMode(original, catalog), catalog);

    expect(fields.enabledTools).toEqual(['calculate']);
    expect(fields.enabledBuiltInTools).toEqual(['web_search']);
    // Catalog order, not the order the mode happened to store them in: normalizing here is what
    // stops a mode's stored list from churning on every re-save.
    expect(fields.enabledCapabilityTools).toEqual(['subagents:Agent', 'sandbox:*']);
  });

  it('preserves a group the catalog could not show', () => {
    // Found by the manual Playwright run: a deployment whose provider contributes no server-side
    // built-ins serves a catalog with NO builtin rows, so the editor showed none, saw none ticked,
    // and wrote [] — stripping the mode's web_search on its first save.
    const withoutBuiltIns = catalog.filter((t) => t.group !== 'builtin');
    const current = mode({ enabledBuiltInTools: ['web_search'] });

    const fields = selectionToModeFields(['calculate'], withoutBuiltIns, current);

    expect(fields.enabledBuiltInTools).toEqual(['web_search']);
  });

  it('still writes an empty built-in list when the group WAS shown', () => {
    // The preservation above must not swallow a real deselection.
    const current = mode({ enabledBuiltInTools: ['web_search'] });

    const fields = selectionToModeFields(['calculate'], catalog, current);

    expect(fields.enabledBuiltInTools).toEqual([]);
  });

  it('preserves the capability selection when no qualified rows are offered', () => {
    const unqualifiedOnly = catalog.filter((t) => !t.id?.includes(':'));
    const current = mode({ enabledCapabilityTools: ['sandbox:*'] });

    const fields = selectionToModeFields(['calculate'], unqualifiedOnly, current);

    expect(fields.enabledCapabilityTools).toEqual(['sandbox:*']);
  });

  it('round-trips a stored capability id the catalog could not display', () => {
    // Found in review: the sandbox listing is probed LIVE, so a failed probe still serves the
    // baseline plus a wildcard row while omitting plugin-provided tools. The group is therefore
    // non-empty and the group-level preservation never fires, so a stored sandbox:MathEval was read
    // as unticked and written away - narrowing a hand-curated mode because discovery was degraded.
    const degraded = catalog.filter((t) => t.id !== 'sandbox:Bash');
    const current = mode({ enabledCapabilityTools: ['sandbox:Read', 'sandbox:MathEval'] });

    const fields = selectionToModeFields(['sandbox:Read'], degraded, current);

    expect(fields.enabledCapabilityTools).toContain('sandbox:MathEval');
    expect(fields.enabledCapabilityTools).toContain('sandbox:Read');
  });

  it('still drops a displayed row the user actually unticked', () => {
    // Non-vacuity for the preservation above: it must not resurrect a real deselection.
    const current = mode({ enabledCapabilityTools: ['sandbox:Read', 'sandbox:Bash'] });

    const fields = selectionToModeFields(['sandbox:Read'], catalog, current);

    expect(fields.enabledCapabilityTools).toEqual(['sandbox:Read']);
  });

  it('does not preserve an unrenderable id once its group wildcard is selected', () => {
    // sandbox:* already covers it, and keeping both would re-introduce the redundancy the
    // wildcard exists to avoid.
    const degraded = catalog.filter((t) => t.id !== 'sandbox:Bash');
    const current = mode({ enabledCapabilityTools: ['sandbox:MathEval'] });

    const fields = selectionToModeFields(['sandbox:*'], degraded, current);

    expect(fields.enabledCapabilityTools).toEqual(['sandbox:*']);
  });

  it('ignores a stored id that is not a qualified capability id', () => {
    // Defensive: a bare name in enabledCapabilityTools belongs to enabledTools and must not be
    // carried into the capability list by the preservation path.
    const current = mode({ enabledCapabilityTools: ['calculate', 'sandbox:MathEval'] });

    const fields = selectionToModeFields(['sandbox:Read'], catalog, current);

    expect(fields.enabledCapabilityTools).not.toContain('calculate');
    expect(fields.enabledCapabilityTools).toContain('sandbox:MathEval');
  });

  it('round-trips a legacy mode into the surface it already had', () => {
    // The regression that matters for existing modes: opening one and saving it unchanged must not
    // change what it does.
    const fields = selectionToModeFields(selectionFromMode(mode({}), catalog), catalog);

    expect(fields.enabledTools).toBeNull();
    expect(fields.enabledCapabilityTools).toEqual(['subagents:Agent']);
  });
});
