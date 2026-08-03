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
import { parseWeatherData, getWeatherEmoji, formatTemperature, getRainForecast } from '@/utils/weatherParser';

/** Returns the first non-empty string value among `keys` in `args`, or '' when absent/non-string. */
function firstString(args: Record<string, unknown> | null, keys: readonly string[]): string {
  if (!args) return '';
  for (const key of keys) {
    const value = args[key];
    if (typeof value === 'string' && value.length > 0) return value;
  }
  return '';
}

/** Trim `s` to `n` chars with an ellipsis. */
function trunc(s: string, n = 60): string {
  return s.length > n ? s.slice(0, n - 1) + '…' : s;
}

/**
 * Comma-joins the first non-empty string ARRAY among `keys` (e.g. CheckAgents' `agent_ids`), or ''.
 * Separate from {@link firstString} because those args are lists, not scalars, and a list arg would
 * otherwise summarize as nothing at all.
 */
function firstStringList(args: Record<string, unknown> | null, keys: readonly string[]): string {
  if (!args) return '';
  for (const key of keys) {
    const value = args[key];
    if (Array.isArray(value)) {
      const names = value.filter((v): v is string => typeof v === 'string' && v.length > 0);
      if (names.length > 0) return trunc(names.join(', '), 40);
    }
  }
  return '';
}

/** Cheap line count for a collapsed summary — no row allocation (empty/nullish → 0). */
function lineCount(value: unknown): number {
  return typeof value === 'string' && value.length > 0 ? value.split('\n').length : 0;
}

/**
 * Cheap Grep match count for the COLLAPSED summary: scan only the trailing "N matches found" line
 * (bounded to the tail) instead of parsing every result row — the heavy per-row parse (parseMatches)
 * is deferred to the expanded MatchesRich renderer.
 */
function grepMatchCount(resultText: string): number | null {
  const match = resultText.slice(-300).match(/(\d+) matches found/);
  return match ? parseInt(match[1], 10) : null;
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

// One renderer per family. `summarize` must be null-safe and NEVER throw (args/result may be
// null/partial). They enrich the one-line only from data actually present.
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
  summarize: (args) => {
    const path = firstString(args, ['file_path']);
    if (!args) return path;
    const stat = `+${lineCount(args.new_string)} −${lineCount(args.old_string)}`;
    return path ? `${path} · ${stat}` : stat;
  },
};

const shellRenderer: ToolRenderer = {
  family: 'shell',
  icon: '>_',
  iconAlt: 'shell command',
  summarize: (args) => trunc(firstString(args, ['command'])),
};

const taskRenderer: ToolRenderer = {
  family: 'task',
  icon: '⏱',
  iconAlt: 'task',
  summarize: (args) => firstString(args, ['shell_id', 'status']),
};

const grepRenderer: ToolRenderer = {
  family: 'grep',
  icon: '🔍',
  iconAlt: 'search',
  summarize: (args, resultText) => {
    const pattern = firstString(args, ['pattern', 'query']);
    const count = grepMatchCount(resultText);
    return count != null ? `${pattern} · ${count} matches` : pattern;
  },
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
  summarize: (args) => {
    // WHO the call concerns: a spawn names its template, an addressed call names one agent
    // (`target` for collaboration SendMessage, `agent_id` for CheckAgent/GetAgentTranscript), and the
    // plural checks carry a LIST (`agent_ids`). GetAgents takes no args at all and correctly
    // summarizes to the bare icon.
    const type =
      firstString(args, ['subagent_type', 'agent_id', 'target', 'shell_id']) ||
      firstStringList(args, ['agent_ids']);
    // WHAT was said: `prompt` is the legacy SendMessage/Agent wording, `content` the collaboration
    // one. Both are supported permanently — old persisted calls must keep rendering.
    const prompt = firstString(args, ['prompt', 'message', 'content']);
    return prompt ? `${type ? type + ' · ' : ''}${trunc(prompt, 40)}` : type;
  },
};

const flowRenderer: ToolRenderer = {
  family: 'flow',
  icon: '⛓',
  iconAlt: 'workflow',
  summarize: (args) => firstString(args, ['node', 'currentNode', 'status']),
};

const waitRenderer: ToolRenderer = {
  family: 'wait',
  icon: '🔧',
  iconAlt: 'wait',
  summarize: (args) => genericSummary(args),
};

const mathRenderer: ToolRenderer = {
  family: 'math',
  icon: '🖩',
  iconAlt: 'math',
  summarize: (args, resultText) => {
    try {
      const parsed = JSON.parse(resultText) as Record<string, unknown>;
      if (parsed && typeof parsed === 'object' && 'result' in parsed) {
        return `${parsed.expression ?? ''} = ${parsed.result}`.trim();
      }
    } catch {
      /* no result yet / not JSON */
    }
    return firstString(args, ['expression']) || genericSummary(args);
  },
};

const webRenderer: ToolRenderer = {
  family: 'web',
  icon: '🌐',
  iconAlt: 'web',
  summarize: (args) => trunc(firstString(args, ['query', 'url'])),
};

const weatherRenderer: ToolRenderer = {
  family: 'weather',
  icon: '🌤️',
  iconAlt: 'weather',
  summarize: (args, resultText) => {
    const location = firstString(args, ['location']);
    const data = parseWeatherData(resultText);
    if (data) {
      const chip = `${getWeatherEmoji(data.condition)} ${formatTemperature(
        data.temperature,
        data.temperatureUnit
      )} · ${getRainForecast(data.condition, data.humidity)}`;
      return location ? `${location} · ${chip}` : chip;
    }
    return location ? `${location} · Loading…` : '';
  },
};

/** Fallback renderer for unknown / labeled-kv tools. Exported so lookups can return it directly. */
export const genericRenderer: ToolRenderer = {
  family: 'generic',
  icon: '🔧',
  iconAlt: 'tool',
  summarize: (args) => genericSummary(args),
};

// #246: AskUserQuestion (browser-hosted client tool). function_args:
// { context: string, questions: [{ id?, prompt, description?, allowMultiple?, allowOther?,
//   options: [{ label, value?, description?, preview? }] }] } (1-4 questions). The collapsed
// summary shows the question count and the first prompt (or the shared context) — QuestionRich
// renders the interactive form.
const questionRenderer: ToolRenderer = {
  family: 'question',
  icon: '❓',
  iconAlt: 'question for you',
  summarize: (args) => {
    const questions = Array.isArray(args?.questions) ? (args!.questions as Array<Record<string, unknown>>) : [];
    if (questions.length === 0) return typeof args?.context === 'string' ? trunc(args.context) : '';
    const firstPrompt = typeof questions[0]?.prompt === 'string' ? (questions[0].prompt as string) : '';
    const countLabel = questions.length > 1 ? `${questions.length} questions · ` : '';
    return trunc(`${countLabel}${firstPrompt}`);
  },
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
register(
  [
    'agent',
    'sendmessage',
    'checkagent',
    // Collaboration tools (#244): the plural checks and the directory/transcript reads.
    'checkagents',
    'waitforagents',
    'getagents',
    'getagenttranscript',
  ],
  agentRenderer
);
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
register(['askuserquestion'], questionRenderer);

/** The flat, shared-reference name → renderer map. */
export function getRegistry(): Map<string, ToolRenderer> {
  return registry;
}
