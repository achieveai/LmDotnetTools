import { unwrapResult, isErrorResult, parseExitCode } from './toolResult';
import type { ToolCallState, ToolPillInput, ToolPillView } from './toolTypes';

/** Narrow to a plain (non-array) object. */
function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
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
    isError = isErrorResult(result, isErrorFlag);

    // Exit code: structured `exit_code` (TaskOutput) wins, else trailing `[Exit code: N]` (Bash).
    if (isRecord(resultValue) && typeof resultValue.exit_code === 'number') {
      exitCode = resultValue.exit_code;
    } else {
      exitCode = parseExitCode(unwrapped.text);
    }

    if (isError) {
      if (isRecord(resultValue) && resultValue.error != null && resultValue.error !== false) {
        errorText = String(resultValue.error);
      } else if (typeof result === 'string' && result.startsWith('Error executing MCP tool')) {
        errorText = result;
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
