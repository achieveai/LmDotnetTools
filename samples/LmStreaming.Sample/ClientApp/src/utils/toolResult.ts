/**
 * Pure helpers for interpreting raw tool-result strings (#199).
 *
 * Tool results arrive as strings that may be plain stdout, single-encoded JSON, or
 * DOUBLE-encoded JSON (a JSON string whose content is itself JSON). These helpers see through the
 * encoding WITHOUT mangling plain stdout, large integers, or quoted strings — the `text` field
 * always preserves the last string form so callers can display it verbatim.
 *
 * Zero Vue / component imports — safe for fast unit tests and any renderer to depend on.
 */
import type { UnwrapResult } from '@/utils/toolTypes';

/** MCP infrastructure errors are persisted with is_error=false but must still read as failures. */
const MCP_ERROR_PREFIX = 'Error executing MCP tool';

/** Trailing-anchored `[Exit code: N]` marker (with any leading newlines) appended to shell/task streams. */
const EXIT_CODE_RE = /\n*\[Exit code:\s*(\d+)\]\s*$/;

/**
 * Progressively `JSON.parse` a raw result to see through single/double encoding.
 *
 * Caps depth at 3 and stops at the first non-string value. `text` is the LAST string seen before
 * the terminal value, so JSON-ish stdout, large integers, and quoted strings display verbatim and
 * are never lossily re-serialized. Never throws.
 */
export function unwrapResult(raw: string | null | undefined): UnwrapResult {
  const s = raw == null ? '' : String(raw);
  let cur = s;
  for (let depth = 0; depth < 3; depth++) {
    let parsed: unknown;
    try {
      parsed = JSON.parse(cur);
    } catch {
      // Not JSON at this level — the current string is terminal plain text.
      return { value: cur, text: cur };
    }
    if (typeof parsed === 'string') {
      // It was an encoded string; unwrap one more layer.
      cur = parsed;
      continue;
    }
    // Terminal non-string (object/array/number/boolean/null) — keep `cur` as the display text.
    return { value: parsed, text: cur };
  }
  // Exhausted the depth cap while still a string.
  return { value: cur, text: cur };
}

/**
 * Match the trailing `[Exit code: N]` marker. The marker is end-anchored, so only a bounded suffix
 * is scanned — cheap even for large TaskOutput streams. Returns the parsed code and the full match
 * length so callers can strip the marker from the body. Single source of truth for the marker regex.
 */
export function matchExitMarker(text: string): { exitCode: number; markerLength: number } | null {
  const suffix = text.length > 128 ? text.slice(-128) : text;
  const match = EXIT_CODE_RE.exec(suffix);
  return match ? { exitCode: Number(match[1]), markerLength: match[0].length } : null;
}

/**
 * Extract a trailing `[Exit code: N]` value, or null when the text has no trailing exit marker.
 */
export function parseExitCode(text: string | null | undefined): number | null {
  if (text == null) return null;
  return matchExitMarker(String(text))?.exitCode ?? null;
}

/**
 * Decide whether a tool result represents a failure. True when ANY of:
 *  1. the explicit `is_error` flag is true;
 *  2. the unwrapped value is an object carrying a truthy own `error` key;
 *  3. the text starts with the MCP infra-error prefix (persisted with is_error=false);
 *  4. a trailing `[Exit code: N]` is present and non-zero (shell failure with is_error=false).
 */
export function isErrorResult(
  raw: string | null | undefined,
  isErrorFlag?: boolean | null
): boolean {
  if (isErrorFlag === true) return true;

  const { value, text } = unwrapResult(raw);

  if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
    const record = value as Record<string, unknown>;
    if (Object.prototype.hasOwnProperty.call(record, 'error') && Boolean(record.error)) {
      return true;
    }
  }

  if (text.startsWith(MCP_ERROR_PREFIX) || (raw != null && String(raw).startsWith(MCP_ERROR_PREFIX))) {
    return true;
  }

  const exit = parseExitCode(text);
  if (exit !== null && exit !== 0) return true;

  return false;
}
