/**
 * Pure parsers that turn raw tool payloads (function args + result strings) into the structured
 * view models declared in {@link ./toolTypes}. Rich tool components consume these models as props.
 *
 * ZERO Vue imports — these are pure functions and must NEVER throw (inputs may be null / partial /
 * a growing streaming prefix).
 */
import type {
  DiffModel,
  DiffRow,
  MatchesModel,
  MatchGroup,
  TerminalModel,
  CodeBlockModel,
  CodeBlockLine,
} from '@/utils/toolTypes';
import { matchExitMarker } from '@/utils/toolResult';

/**
 * Split a string into logical lines. A nullish input and a genuinely-empty `''` input both yield
 * ZERO lines (splitting `''` on `\n` gives `['']`, which we collapse to `[]` so we never emit a
 * phantom empty-string row).
 */
function splitLines(value: string | null | undefined): string[] {
  if (value == null) {
    return [];
  }
  const parts = value.split('\n');
  if (parts.length === 1 && parts[0] === '') {
    return [];
  }
  return parts;
}

/**
 * Simple diff (no LCS, no invented `@@` hunks): every old line rendered as removed, followed by
 * every new line rendered as added.
 */
export function parseDiff(
  oldString: string | null | undefined,
  newString: string | null | undefined,
  replaceAll?: boolean
): DiffModel {
  const oldLines = splitLines(oldString);
  const newLines = splitLines(newString);

  const rows: DiffRow[] = [];
  for (const text of oldLines) {
    rows.push({ kind: 'del', text });
  }
  for (const text of newLines) {
    rows.push({ kind: 'add', text });
  }

  return {
    rows,
    added: newLines.length,
    removed: oldLines.length,
    replaceAll: !!replaceAll,
  };
}

/**
 * Parse ripgrep-style Grep `content` output into file-grouped match/context lines plus the trailing
 * "N matches found" summary.
 */
export function parseMatches(text: string | null | undefined): MatchesModel {
  const model: MatchesModel = { groups: [], summary: '', totalMatches: 0 };
  if (text == null || text === '') {
    return model;
  }

  let currentGroup: MatchGroup | null = null;
  const ensureGroup = (): MatchGroup => {
    if (currentGroup == null) {
      currentGroup = { file: null, lines: [] };
      model.groups.push(currentGroup);
    }
    return currentGroup;
  };

  for (const line of text.split('\n')) {
    // Ignore blank lines and `--` context separators.
    if (line.trim() === '' || line === '--') {
      continue;
    }

    // Trailing summary line, e.g. "20 matches found in 4 files (searched 48 files)".
    const summaryMatch = line.match(/(\d+) matches found/);
    if (summaryMatch) {
      model.summary = line;
      model.totalMatches = parseInt(summaryMatch[1], 10);
      continue;
    }

    // Match line: `<lineNo>:<text>`.
    const matchLine = line.match(/^(\d+):(.*)$/);
    if (matchLine) {
      ensureGroup().lines.push({
        lineNo: parseInt(matchLine[1], 10),
        text: matchLine[2],
        isMatch: true,
      });
      continue;
    }

    // Context line: `<lineNo>-<text>`.
    const contextLine = line.match(/^(\d+)-(.*)$/);
    if (contextLine) {
      ensureGroup().lines.push({
        lineNo: parseInt(contextLine[1], 10),
        text: contextLine[2],
        isMatch: false,
      });
      continue;
    }

    // File heading: ends with `:` and does NOT start with a digit.
    if (line.endsWith(':') && !/^\d/.test(line)) {
      currentGroup = { file: line.slice(0, -1), lines: [] };
      model.groups.push(currentGroup);
      continue;
    }

    // Unknown / noise lines are ignored (like blank lines and `--` separators).
  }

  return model;
}

/**
 * Normalize a terminal-style result. Two shapes are supported:
 *  - structured (`TaskOutput` object with `stdout`/`stderr`/`exit_code`/`status`);
 *  - a plain combined string (Bash) whose exit code, when present, is a trailing `[Exit code: N]`.
 */
export function parseTerminal(
  text: string | null | undefined,
  opts?: { structured?: Record<string, unknown> | null; exitCode?: number | null }
): TerminalModel {
  const structured = opts?.structured;
  if (structured != null && typeof structured === 'object') {
    const stdout = typeof structured.stdout === 'string' ? structured.stdout : '';
    const stderr = typeof structured.stderr === 'string' ? structured.stderr : '';
    const envelopeExit = typeof structured.exit_code === 'number' ? structured.exit_code : null;
    // An explicit authoritative exitCode (resolved by deriveToolPillState, which also accounts for
    // a failing command captured in stdout) takes precedence over the envelope's own exit_code.
    const exitCode = opts?.exitCode ?? envelopeExit;
    const failed = (exitCode != null && exitCode !== 0) || structured.status === 'failed';
    return { stdout, stderr, exitCode, failed };
  }

  const fallbackExit = opts?.exitCode ?? null;

  if (text == null) {
    return {
      stdout: '',
      stderr: '',
      exitCode: fallbackExit,
      failed: fallbackExit != null && fallbackExit !== 0,
    };
  }

  const marker = matchExitMarker(text);
  if (marker) {
    const stdout = text.slice(0, text.length - marker.markerLength).replace(/\n+$/, '');
    return { stdout, stderr: '', exitCode: marker.exitCode, failed: marker.exitCode !== 0 };
  }

  return {
    stdout: text,
    stderr: '',
    exitCode: fallbackExit,
    failed: fallbackExit != null && fallbackExit !== 0,
  };
}

/**
 * Turn a `read`/`write` payload into a code block. Numbered `%6d\t` Read output becomes
 * `mode:'numbered'` with per-line numbers; a Write `content` arg (or raw text fallback) becomes
 * `mode:'plain'`.
 */
export function parseCodeBlock(
  args: Record<string, unknown> | null,
  text: string | null | undefined
): CodeBlockModel {
  if (text != null && /^\s*\d+\t/m.test(text)) {
    const lines: CodeBlockLine[] = [];
    for (const line of text.split('\n')) {
      const numbered = line.match(/^\s*(\d+)\t(.*)$/);
      if (numbered) {
        lines.push({ lineNo: parseInt(numbered[1], 10), text: numbered[2] });
      }
    }
    return { mode: 'numbered', lines, content: text };
  }

  if (args != null && typeof args.content === 'string') {
    return { mode: 'plain', lines: [], content: args.content };
  }

  return { mode: 'plain', lines: [], content: text ?? '' };
}
