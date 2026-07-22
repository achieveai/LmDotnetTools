/**
 * Deterministic, client-side color assignment for sub-agents (#tabbed-subagents).
 *
 * NO Vue imports — a pure `.ts` module (mirrors `components/tools/registry.ts`) so it is cheap to
 * unit-test. Colors are assigned by DISCOVERY ORDER (1st sub-agent → hue 0, 2nd → hue 1, …), never by
 * an LLM, and used to tint a sub-agent's tab and its inline calls in the conversation.
 */

/**
 * The rotating palette: distinct, AA-readable-on-white hues. Deliberately avoids the app's reserved
 * accent `#007bff` and error `#dc3545` so agent colors never read as "link" or "error". The pale tab
 * background is derived from a hue in CSS via `color-mix`, so only the base hue is stored here.
 */
export const AGENT_HUES: readonly string[] = [
  '#2563eb', // blue
  '#0d9488', // teal
  '#7c3aed', // violet
  '#b45309', // amber
  '#be123c', // rose
  '#15803d', // green
  '#0891b2', // cyan
  '#a21caf', // magenta
];

/** Fixed neutral color for the top-level `main` tab — NOT part of the rotating palette. */
export const MAIN_TAB_COLOR = '#334155'; // slate

/** provide/inject key for the agentId → color resolver (mirrors GET_RESULT_FOR_TOOL_CALL). */
export const GET_AGENT_COLOR = 'getAgentColor';

/** Resolver injected into descendant pills: agentId → hue, or null when unknown/unassigned. */
export type AgentColorLookup = (agentId: string | null | undefined) => string | null;

/** The hue for a 0-based discovery index, wrapping around the palette. */
export function hueForIndex(index: number): string {
  return AGENT_HUES[index % AGENT_HUES.length];
}

function firstStringField(obj: Record<string, unknown> | null, keys: readonly string[]): string | null {
  if (!obj) return null;
  for (const key of keys) {
    const value = obj[key];
    if (typeof value === 'string' && value.length > 0) return value;
  }
  return null;
}

/**
 * Extract an EXACT sub-agent id from a sub-agent tool call so its inline pill can be colored to match
 * that agent's tab. Reliable sources only (no template/name guessing, which would mis-color two
 * siblings spawned from the same template):
 *   - `agent_id` in the call args (`sendmessage` / `checkagent`), or
 *   - `agent_id` in the tool result JSON (a background `Agent` spawn returns `{ agent_id, … }`).
 * Returns null when no exact id is present (e.g. a synchronous spawn whose result is the final answer
 * text) — the caller then renders the pill uncolored, unchanged from today.
 */
export function resolveAgentIdFromCall(
  parsedArgs: Record<string, unknown> | null,
  resultText: string | null | undefined
): string | null {
  const fromArgs = firstStringField(parsedArgs, ['agent_id', 'agentId']);
  if (fromArgs) return fromArgs;
  if (resultText) {
    try {
      const parsed = JSON.parse(resultText) as unknown;
      if (parsed && typeof parsed === 'object') {
        return firstStringField(parsed as Record<string, unknown>, ['agent_id', 'agentId']);
      }
    } catch {
      /* result is not JSON (e.g. a synchronous spawn's answer text) — no exact id available */
    }
  }
  return null;
}
