import { inject } from 'vue';
import type { ToolCall, ToolCallResultMessage } from '@/types';

/** The provider key ChatLayout uses to expose result-matching to descendant pills. */
export const GET_RESULT_FOR_TOOL_CALL = 'getResultForToolCall';

type ResultLookup = (toolCallId: string | null | undefined) => ToolCallResultMessage | null;

/**
 * Result-matching for a tool call by its `tool_call_id`. Consolidates the block that was
 * copy-pasted across the (now dead) MessageItem/MessageGroup and the live MetadataPill.
 * ToolPill is the sole consumer — rich components receive parsed props and never inject.
 */
export function useToolResult() {
  const getResultForToolCall = inject<ResultLookup>(GET_RESULT_FOR_TOOL_CALL, () => null);

  function getResult(toolCall: ToolCall): ToolCallResultMessage | null {
    return getResultForToolCall(toolCall.tool_call_id);
  }

  return { getResultForToolCall, getResult };
}
