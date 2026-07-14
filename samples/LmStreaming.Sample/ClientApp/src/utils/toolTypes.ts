/**
 * Shared types for the per-tool rendering system (#199).
 *
 * This module has ZERO imports so every renderer, parser, and state module can depend on it
 * without cycles. Pure data/types only — no Vue, no component references.
 */

/**
 * Normalized tool family key. Drives icon + collapsed summary + which rich component (if any)
 * ToolPill mounts. `generic` is the fallback for unknown / labeled-kv tools.
 */
export type ToolFamily =
  | 'read'
  | 'write'
  | 'edit'
  | 'shell'
  | 'task'
  | 'grep'
  | 'glob'
  | 'skill'
  | 'agent'
  | 'flow'
  | 'wait'
  | 'math'
  | 'web'
  | 'weather'
  | 'generic';

/**
 * Result of progressively unwrapping a raw tool-result string.
 * `value` is the deepest parsed value; `text` is the last STRING seen before the terminal
 * non-string value (so JSON-ish stdout / large integers / quoted strings display verbatim,
 * never mangled by over-parsing).
 */
export interface UnwrapResult {
  value: unknown;
  text: string;
}

/** Lifecycle state of a single tool call (families differ only in content, not the machine). */
export type ToolCallState = 'streaming-args' | 'awaiting-result' | 'success' | 'error';

/** Input to {@link deriveToolPillState}. All fields tolerate null/undefined/partial. */
export interface ToolPillInput {
  /** Raw `function_args` JSON string (may be a growing prefix during streaming). */
  functionArgs?: string | null;
  /** Raw `result` string from the correlated ToolCallResultMessage. */
  result?: string | null;
  /** Whether a result message exists at all (distinct from an empty result string). */
  hasResult: boolean;
  /** The `is_error` flag from the ToolCallResultMessage, if any. */
  isErrorFlag?: boolean | null;
}

/**
 * Pure, mount-free view model derived from a tool call + its result. ToolPill renders from this.
 * {@link deriveToolPillState} NEVER throws.
 */
export interface ToolPillView {
  state: ToolCallState;
  isError: boolean;
  /** Non-zero shell/task exit code when detectable in the payload, else null. */
  exitCode: number | null;
  /** Static background/async indicator (run_in_background arg, Agent {status:"spawned"}). */
  isBackground: boolean;
  /** `function_args` parsed once — object, or null when absent/invalid/partial. */
  parsedArgs: Record<string, unknown> | null;
  /** `unwrapResult(result).text` — display text (raw string, never mangled). '' when no result. */
  resultText: string;
  /** Whether a result message exists. */
  hasResult: boolean;
  /** Error message to surface when `isError` (from `{error}` key / MCP prefix / raw), else null. */
  errorText: string | null;
}

/**
 * Registry entry — DATA ONLY (no Vue Component ref, so registry stays a pure, fast-tested `.ts`).
 * ToolPill owns the `family → RichComponent` mapping.
 */
export interface ToolRenderer {
  family: ToolFamily;
  /** Emoji icon shown collapsed. */
  icon: string;
  /** Text alternative for the emoji icon (a11y). */
  iconAlt: string;
  /**
   * Collapsed one-line summary. Receives PARSED args, the unwrapped display result text, and the
   * derived view. Pure; must NEVER throw (args/result may be null/partial).
   */
  summarize: (
    parsedArgs: Record<string, unknown> | null,
    resultText: string,
    view: ToolPillView
  ) => string;
}

// ---------------------------------------------------------------------------
// Parser models (utils/toolParsers.ts). Rich components consume these as props.
// ---------------------------------------------------------------------------

export interface DiffRow {
  kind: 'del' | 'add';
  text: string;
}
export interface DiffModel {
  rows: DiffRow[];
  /** Number of added (new_string) lines. */
  added: number;
  /** Number of removed (old_string) lines. */
  removed: number;
  replaceAll: boolean;
}

export interface MatchLine {
  /** Line number when the grep output carries one, else null. */
  lineNo: number | null;
  text: string;
  /** True for a matched line (`:` separator), false for a context line (`-` separator). */
  isMatch: boolean;
}
export interface MatchGroup {
  /** File path heading, or null when the output is not grouped by file. */
  file: string | null;
  lines: MatchLine[];
}
export interface MatchesModel {
  groups: MatchGroup[];
  /** Trailing "N matches found in M files" summary line when present, else ''. */
  summary: string;
  totalMatches: number;
}

export interface TerminalModel {
  stdout: string;
  stderr: string;
  /** Exit code when present (trailing `[Exit code: N]` or structured `exit_code`), else null. */
  exitCode: number | null;
  /** True when the command failed (non-zero exit) — even if `is_error` was false. */
  failed: boolean;
}

export interface CodeBlockLine {
  /** Line number for numbered `read` output, else null. */
  lineNo: number | null;
  text: string;
}
export interface CodeBlockModel {
  mode: 'numbered' | 'plain';
  lines: CodeBlockLine[];
  /** The full raw content (used when mode is plain / write). */
  content: string;
}
