/**
 * Pure DATA registry (#199): normalized tool name → {@link ToolRenderer} descriptor.
 *
 * NO Vue imports and a ToolRenderer carries NO component reference, so this module stays a fast,
 * cheaply-unit-tested `.ts`. ToolPill owns the separate `family → rich component` mapping.
 *
 * Renderer objects are SHARED: a family is defined once and registered under every normalized name
 * that maps to it (multiple keys → the same object identity).
 */
import type { ToolRenderer } from '@/utils/toolTypes';

/** Returns the first non-empty string value among `keys` in `args`, or '' when absent/non-string. */
function firstString(args: Record<string, unknown> | null, keys: readonly string[]): string {
  if (!args) return '';
  for (const key of keys) {
    const value = args[key];
    if (typeof value === 'string' && value.length > 0) return value;
  }
  return '';
}

/** Compact `key: value` join of the first 1-2 args, capped near 60 chars. Null-safe; never throws. */
function genericSummary(args: Record<string, unknown> | null): string {
  if (!args) return '';
  const entries = Object.entries(args).slice(0, 2);
  if (entries.length === 0) return '';
  const joined = entries
    .map(([key, value]) => `${key}: ${typeof value === 'string' ? value : JSON.stringify(value)}`)
    .join(', ');
  return joined.length > 60 ? joined.slice(0, 57) + '...' : joined;
}

// One renderer per family. `summarize` is intentionally tiny + null-safe (must NEVER throw); the
// orchestrator refines rich summaries later, so these only pull the single most useful arg.
const readRenderer: ToolRenderer = {
  family: 'read',
  icon: '📄',
  iconAlt: 'file read',
  summarize: (args) => firstString(args, ['file_path']),
};

const writeRenderer: ToolRenderer = {
  family: 'write',
  icon: '📝',
  iconAlt: 'file write',
  summarize: (args) => firstString(args, ['file_path']),
};

const editRenderer: ToolRenderer = {
  family: 'edit',
  icon: '✏️',
  iconAlt: 'file edit',
  summarize: (args) => firstString(args, ['file_path']),
};

const shellRenderer: ToolRenderer = {
  family: 'shell',
  icon: '>_',
  iconAlt: 'shell command',
  summarize: (args) => firstString(args, ['command']),
};

const taskRenderer: ToolRenderer = {
  family: 'task',
  icon: '⏱',
  iconAlt: 'task',
  summarize: () => '',
};

const grepRenderer: ToolRenderer = {
  family: 'grep',
  icon: '🔍',
  iconAlt: 'search',
  summarize: (args) => firstString(args, ['pattern', 'query']),
};

const globRenderer: ToolRenderer = {
  family: 'glob',
  icon: '🔍',
  iconAlt: 'file glob',
  summarize: (args) => firstString(args, ['pattern']),
};

const skillRenderer: ToolRenderer = {
  family: 'skill',
  icon: '🧩',
  iconAlt: 'skill',
  summarize: (args) => firstString(args, ['name', 'skill']),
};

const agentRenderer: ToolRenderer = {
  family: 'agent',
  icon: '🤖',
  iconAlt: 'sub-agent',
  summarize: (args) => firstString(args, ['subagent_type']),
};

const flowRenderer: ToolRenderer = {
  family: 'flow',
  icon: '⛓',
  iconAlt: 'workflow',
  summarize: () => '',
};

const waitRenderer: ToolRenderer = {
  family: 'wait',
  icon: '🔧',
  iconAlt: 'wait',
  summarize: () => '',
};

const mathRenderer: ToolRenderer = {
  family: 'math',
  icon: '🖩',
  iconAlt: 'math',
  summarize: (args) => firstString(args, ['expression']),
};

const webRenderer: ToolRenderer = {
  family: 'web',
  icon: '🌐',
  iconAlt: 'web',
  summarize: (args) => firstString(args, ['query', 'url']),
};

const weatherRenderer: ToolRenderer = {
  family: 'weather',
  icon: '🌤️',
  iconAlt: 'weather',
  summarize: (args) => firstString(args, ['location']),
};

/** Fallback renderer for unknown / labeled-kv tools. Exported so lookups can return it directly. */
export const genericRenderer: ToolRenderer = {
  family: 'generic',
  icon: '🔧',
  iconAlt: 'tool',
  summarize: (args) => genericSummary(args),
};

const registry = new Map<string, ToolRenderer>();

function register(names: readonly string[], renderer: ToolRenderer): void {
  for (const name of names) registry.set(name, renderer);
}

// Names are already NORMALIZED (lowercase, no `sandbox-` prefix); resolveRenderer normalizes input.
register(['read'], readRenderer);
register(['write'], writeRenderer);
register(['edit', 'multiedit'], editRenderer);
register(['bash', 'powershell'], shellRenderer);
register(['taskoutput', 'killshell'], taskRenderer);
register(['grep'], grepRenderer);
register(['glob'], globRenderer);
register(['skill'], skillRenderer);
register(['agent', 'sendmessage', 'checkagent'], agentRenderer);
register(
  [
    'setworkflow',
    'getworkflow',
    'setcurrentnode',
    'setstate',
    'setnotes',
    'startworkflow',
    'checkworkflow',
    'waitworkflow',
  ],
  flowRenderer
);
register(['wait', 'cancelwait', 'listwaits'], waitRenderer);
register(['math_eval', 'calculate', 'calculator', 'math'], mathRenderer);
register(['web_search', 'web_fetch', 'websearch', 'webfetch'], webRenderer);
register(['get_weather', 'weather', 'fetch_weather', 'get_forecast'], weatherRenderer);

/** The flat, shared-reference name → renderer map. */
export function getRegistry(): Map<string, ToolRenderer> {
  return registry;
}
