import { inject } from 'vue';
import type { ToolCall, ToolCallResultMessage } from '@/types';

/** The provider key ChatLayout uses to expose result-matching to descendant pills. */
export const GET_RESULT_FOR_TOOL_CALL = 'getResultForToolCall';

/**
 * The provider key for "has this question already been answered?" — the second input to
 * `isQuestionAwaitingAnswer` (utils/pendingQuestions). A question the server settled early keeps its
 * placeholder result forever; whether it is still open is decided by the transcript (a
 * `<user-answer …>` message) or by this client's own acked submission, both of which live in the
 * composable that owns the results, not in the pill.
 */
export const IS_QUESTION_ANSWERED = 'isQuestionAnswered';

type ResultLookup = (toolCallId: string | null | undefined) => ToolCallResultMessage | null;
type AnsweredLookup = (toolCallId: string) => boolean;

/**
 * Result-matching for a tool call by its `tool_call_id`. Consolidates the block that was
 * copy-pasted across the (now dead) MessageItem/MessageGroup and the live MetadataPill.
 * ToolPill is the sole consumer — rich components receive parsed props and never inject.
 */
export function useToolResult() {
  const getResultForToolCall = inject<ResultLookup>(GET_RESULT_FOR_TOOL_CALL, () => null);
  const isQuestionAnswered = inject<AnsweredLookup>(IS_QUESTION_ANSWERED, () => false);

  function getResult(toolCall: ToolCall): ToolCallResultMessage | null {
    return getResultForToolCall(toolCall.tool_call_id);
  }

  return { getResultForToolCall, getResult, isQuestionAnswered };
}
