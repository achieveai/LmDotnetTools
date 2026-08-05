/**
 * Finding the questions a run is currently blocked on, so the client can dock them above the
 * chat input instead of burying them in the metadata pill.
 *
 * WHY THIS EXISTS: answering an `AskUserQuestion` is a capability the CLIENT exposes to the
 * server, not a rendering detail of the tool call that requested it. The tool call belongs in the
 * transcript (it is history); the thing the user has to *act on* belongs where they act — next to
 * the text box. Inside the pill it could be invisible for three compounding reasons: the pill body
 * is collapsed by default, the item list is a 150px scroll box, and that box auto-scrolls to the
 * bottom on every new item, pushing an earlier question out of view.
 *
 * Kept pure and mount-free (like `deriveToolPillState`, which it feeds) so both the main chat and
 * the sub-agent transcript run the SAME scan. Scoping a fix like this to one consumer of
 * `MessageList` has already shipped broken here once.
 */
import type { DisplayItem, ToolCall, ToolCallResultMessage } from '@/types';
import { resolveRenderer } from '@/utils/toolName';

/** A tool call whose deferred result is still outstanding — i.e. waiting on this user. */
export interface PendingQuestion {
  /** `tool_call_id`; non-empty by construction (a call without one cannot be answered). */
  id: string;
  toolCall: ToolCall;
  result: ToolCallResultMessage;
}

type ResultLookup = (toolCallId: string | null | undefined) => ToolCallResultMessage | null;

function isToolsCall(item: unknown): item is { tool_calls: ToolCall[] } {
  return !!item && Array.isArray((item as { tool_calls?: unknown }).tool_calls);
}

/**
 * Scan a transcript for question tool calls still awaiting an answer, in transcript order.
 *
 * "Awaiting an answer" is the deferred-result protocol (#246): the server publishes a placeholder
 * `ToolCallResultMessage` with `is_deferred: true`, then republishes the SAME `tool_call_id` with
 * the real result once answered. So `is_deferred` — not "no result yet" — is the pending signal;
 * a call with no result at all is still streaming and has nothing to answer.
 *
 * Tool identity goes through {@link resolveRenderer} rather than a name comparison, so the
 * `sandbox-`-prefixed and differently-cased spellings of the same tool all resolve alike.
 */
export function findPendingQuestions(
  displayItems: DisplayItem[],
  getResult: ResultLookup
): PendingQuestion[] {
  const pending: PendingQuestion[] = [];
  const seen = new Set<string>();

  for (const item of displayItems) {
    if (item.type !== 'pill') continue;
    for (const message of item.items) {
      if (!isToolsCall(message)) continue;
      for (const toolCall of message.tool_calls) {
        const id = toolCall.tool_call_id;
        // A duplicate id is the same logical call re-rendered (resume replays a pill), not a
        // second question — docking it twice would put two live forms on screen.
        if (!id || seen.has(id)) continue;
        if (resolveRenderer(toolCall.function_name).family !== 'question') continue;
        const result = getResult(id);
        if (!result?.is_deferred) continue;
        seen.add(id);
        pending.push({ id, toolCall, result });
      }
    }
  }

  return pending;
}
