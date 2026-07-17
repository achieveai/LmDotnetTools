import { unwrapResult, isErrorResult, parseExitCode } from './toolResult';
import type { ToolCallState, ToolPillInput, ToolPillView } from './toolTypes';

/** Narrow to a plain (non-array) object. */
function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/**
 * Authoritative exit code for a result, preferring ANY non-zero signal.
 *
 * A structured TaskOutput envelope can report `exit_code: 0` / `status: "completed"` while its
 * captured `stdout` ends with a failing command's trailing `[Exit code: N]` — that captured
 * command failed, so the pill must surface the failure (AC: "non-zero exit renders as failure
 * even when is_error=false"), not a confident green success.
 */
function resolveExitCode(value: unknown, text: string): number | null {
  if (isRecord(value) && typeof value.exit_code === 'number') {
    if (value.exit_code !== 0) return value.exit_code;
    // Envelope says 0 — but a captured command may still have failed; check its stream tails.
    for (const stream of [value.stdout, value.stderr]) {
      if (typeof stream === 'string') {
        const nested = parseExitCode(stream);
        if (nested !== null && nested !== 0) return nested;
      }
    }
    return value.exit_code; // genuinely 0
  }
  // Plain string result (e.g. Bash): trailing `[Exit code: N]` marker.
  return parseExitCode(text);
}

/**
 * Pure, mount-free state derivation for one tool call + its (optional) result.
 *
 * The state machine is `streaming-args → awaiting-result → success | error`:
 * - no result yet + args not yet valid JSON  → `streaming-args` (receiving…)
 * - no result yet + args parse to an object   → `awaiting-result`
 * - result present                            → `success` | `error`
 *
 * Never throws — every field tolerates null/undefined/partial input.
 */
export function deriveToolPillState(input: ToolPillInput): ToolPillView {
  const { functionArgs, result, hasResult, isErrorFlag } = input;

  // Parse args once. A growing/invalid prefix parses to null (→ streaming-args).
  let parsedArgs: Record<string, unknown> | null = null;
  if (functionArgs) {
    try {
      const p: unknown = JSON.parse(functionArgs);
      if (isRecord(p)) parsedArgs = p;
    } catch {
      parsedArgs = null;
    }
  }

  let resultText = '';
  let resultValue: unknown = '';
  let isError = false;
  let exitCode: number | null = null;
  let errorText: string | null = null;

  if (hasResult) {
    const unwrapped = unwrapResult(result);
    resultText = unwrapped.text;
    resultValue = unwrapped.value;

    // Exit code: prefer any non-zero signal (see resolveExitCode — a task can report envelope
    // exit_code 0 while its captured stdout ends with a failing command's `[Exit code: N]`).
    exitCode = resolveExitCode(resultValue, unwrapped.text);

    // A non-zero exit OR a structured `status:"failed"` is a failure regardless of the (unreliable)
    // is_error flag / envelope exit_code — keeps pill state consistent with the terminal renderer.
    const statusFailed = isRecord(resultValue) && resultValue.status === 'failed';
    isError = isErrorResult(result, isErrorFlag) || (exitCode !== null && exitCode !== 0) || statusFailed;

    if (isError) {
      if (isRecord(resultValue) && resultValue.error != null && resultValue.error !== false) {
        errorText = String(resultValue.error);
      } else if (typeof result === 'string' && result.startsWith('Error executing MCP tool')) {
        errorText = result;
      } else if (exitCode !== null && exitCode !== 0) {
        errorText = `Exited with code ${exitCode}`;
      } else if (statusFailed) {
        errorText = 'Task failed';
      } else {
        errorText = resultText || (typeof result === 'string' ? result : '');
      }
    }
  }

  // Static background/async indicator (no live reconcile): run_in_background arg + Agent spawn.
  let isBackground = false;
  if (parsedArgs && parsedArgs.run_in_background === true) isBackground = true;
  if (isRecord(resultValue) && resultValue.status === 'spawned') isBackground = true;

  let state: ToolCallState;
  if (hasResult) state = isError ? 'error' : 'success';
  else if (parsedArgs !== null) state = 'awaiting-result';
  else state = 'streaming-args';

  return { state, isError, exitCode, isBackground, parsedArgs, resultText, hasResult, errorText };
}
