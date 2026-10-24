/**
 * Wire tool-name normalization + renderer lookup (#199).
 *
 * Backends label the same logical tool inconsistently: sandbox-mounted tools arrive `sandbox-`
 * prefixed and casing varies (`Bash`, `bash`, `SANDBOX-bash`). {@link resolveRenderer} folds those
 * to a single canonical key and resolves the matching {@link ToolRenderer}, falling back to the
 * shared {@link genericRenderer} for anything unknown or empty.
 */
import type { ToolCall, ToolCallResultMessage } from '@/types';
import type { ToolRenderer } from '@/utils/toolTypes';
import { getRegistry, genericRenderer } from '@/components/tools/registry';

/** Leading `sandbox-` prefix, matched case-insensitively. */
const SANDBOX_PREFIX = /^sandbox-/i;

/**
 * Canonical registry key for a wire tool name: strip a leading `sandbox-` prefix (case-insensitive)
 * then lowercase. Returns '' for nullish/empty input.
 */
export function normalizeToolName(wireName: string | null | undefined): string {
  if (!wireName) return '';
  return wireName.replace(SANDBOX_PREFIX, '').toLowerCase();
}

/**
 * Resolve the renderer for a wire tool name. Unknown, empty, or nullish names resolve to the
 * shared {@link genericRenderer}. Never throws.
 */
export function resolveRenderer(wireName: string | null | undefined): ToolRenderer {
  const normalized = normalizeToolName(wireName);
  if (!normalized) return genericRenderer;
  return getRegistry().get(normalized) ?? genericRenderer;
}

/** Normalized name of the tiered root's runner: `ExecuteTool({tool, arguments})` runs `tool`. */
const EXECUTE_TOOL = 'executetool';

/** A call as the UI shows it, see {@link unwrapToolCall}. */
export interface DisplayedToolCall {
  /** The call to render: the wrapped target (its name + its own args JSON), or the call itself. */
  call: ToolCall;
  /** The wrapper's wire name (`ExecuteTool`) when the call was unwrapped, else null. */
  via: string | null;
}

/** Narrow to a plain (non-array) object. */
function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/** Parse JSON to a plain object, or null when it is not one (or not JSON at all). */
function parseRecord(json: string): Record<string, unknown> | null {
  try {
    const parsed: unknown = JSON.parse(json);
    return isRecord(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * The call a pill renders. The transcript keeps an `ExecuteTool({tool, arguments})` wrapper, but the
 * server runs `tool` exactly as a direct call, so the UI shows that tool (name, args, renderer) and
 * keeps the wrapper only as `via`. Mirrors the server's unwrap shapes: `arguments` is an object, a
 * JSON string holding one (weak models double-encode), or absent/null (no arguments). Anything else —
 * including args still streaming — returns the call itself, i.e. today's generic ExecuteTool card.
 *
 * Once a `result` exists the server is the authority: a target it ran names itself in `tool_name`,
 * while a refused or failed unwrap keeps `ExecuteTool`. So only a result naming the target keeps the
 * target render; any other result shows the wrapper with its error, never a tool that did not run.
 * Never throws.
 */
export function unwrapToolCall(call: ToolCall, result?: ToolCallResultMessage | null): DisplayedToolCall {
  const unchanged = { call, via: null };
  if (normalizeToolName(call.function_name) !== EXECUTE_TOOL || !call.function_args) return unchanged;

  const wrapper = parseRecord(call.function_args);
  if (!wrapper) return unchanged;
  const { tool, arguments: given } = wrapper;
  if (typeof tool !== 'string' || !tool.trim()) return unchanged;
  // The server refuses a nested ExecuteTool, and its result keeps the wrapper's name — which would
  // match this target below and render "ExecuteTool via ExecuteTool". Never unwrap into itself.
  if (normalizeToolName(tool) === EXECUTE_TOOL) return unchanged;
  if (result &&normalizeToolName(result.tool_name) !== normalizeToolName(tool)) return unchanged;

  const args = given == null ? {} : typeof given === 'string' ? parseRecord(given) : given;
  if (!isRecord(args)) return unchanged;

  return {
    call: { ...call, function_name: tool, function_args: JSON.stringify(args) },
    via: call.function_name ?? null,
  };
}
