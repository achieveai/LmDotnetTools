import type { Message } from '@/types';
import {
  isNotifyMessage,
  isTextMessage,
  isTextUpdateMessage,
  isReasoningMessage,
  isReasoningUpdateMessage,
  isToolsCallMessage,
  isToolsCallUpdateMessage,
  isToolCallMessage,
  isToolCallUpdateMessage,
} from '@/types';

/**
 * The logical "kind" a message merges under. Two messages with different kinds never share a merge
 * key even if their identity fields collide.
 *
 * Extracted verbatim from useChat so multiple consumers (the parent chat and the sub-agent panel)
 * key messages identically without duplicating this bug-prone identity logic.
 */
export function getMergeKind(msg: Message): 'text' | 'reasoning' | 'tools' | 'tool' | 'notify' | 'other' {
  if (isNotifyMessage(msg)) return 'notify';
  if (isTextMessage(msg) || isTextUpdateMessage(msg)) return 'text';
  if (isReasoningMessage(msg) || isReasoningUpdateMessage(msg)) return 'reasoning';
  if (isToolsCallMessage(msg) || isToolsCallUpdateMessage(msg)) return 'tools';
  if (isToolCallMessage(msg) || isToolCallUpdateMessage(msg)) return 'tool';
  return 'other';
}

/**
 * Generate the merge/dedup key for a message (`kind-runId-generationId-messageOrderIdx[-disambiguator]`).
 * The key MUST uniquely identify one logical message: it is consumed by BOTH the display/dedup index
 * and (indirectly) the streaming accumulator, so a collision either overwrites an earlier block or
 * concatenates a later turn's deltas onto an earlier one.
 *
 * Extracted verbatim from useChat (pure function, identical behavior) so the sub-agent panel reuses
 * the exact same identity logic instead of re-deriving it.
 */
export function getMergeKey(msg: Message, turnSeq = 0): string {
  const runId = msg.runId || 'default';
  const generationId = msg.generationId || 'default';
  const messageOrderIdx = msg.messageOrderIdx ?? 0;
  const mergeKind = getMergeKind(msg);

  // A notification carries a distinct generationId ('notify:<guid>' stamped by the backend on the
  // ONE message object), so two notifications in one run never collide on the shared
  // runId/messageOrderIdx (both 0), and the live-published copy merges with its reload-parsed twin
  // (same generationId). Keyed explicitly so a null/missing messageOrderIdx can't fold two distinct
  // notifications onto one pill.
  if (isNotifyMessage(msg)) {
    return `${mergeKind}-${runId}-${generationId}-${messageOrderIdx}`;
  }

  // Individual tool calls — streaming OR finalized — are keyed by tool_call_id. Several concurrent
  // tool calls in one turn share runId/generationId/messageOrderIdx and differ only by tool_call_id
  // (e.g. GPT-5.5 via the OpenAI Responses API); without it they collapse into a single pill.
  if (isToolCallUpdateMessage(msg) || isToolCallMessage(msg)) {
    return `${mergeKind}-${runId}-${generationId}-${messageOrderIdx}-${msg.tool_call_id || 'tc'}`;
  }

  // A finalized single-call ToolsCallMessage is the same case wrapped in the aggregate type —
  // disambiguate by its tool_call_id too. Multi-call aggregates already carry every call in one
  // message (rendered as N pills), so they key on messageOrderIdx only.
  if (isToolsCallMessage(msg) && msg.tool_calls?.length === 1 && msg.tool_calls[0]?.tool_call_id) {
    return `${mergeKind}-${runId}-${generationId}-${messageOrderIdx}-${msg.tool_calls[0].tool_call_id}`;
  }

  // Reasoning and text have no per-instance id (unlike tool_call_id). The server now mints a
  // per-turn generationId (MultiTurnAgentLoop.ExecuteRunTurnsAsync), so live streams keep turns
  // distinct on their own. But conversations PERSISTED before that fix still carry the old
  // run-scoped shape on disk — one generationId across every turn with messageOrderIdx reset each
  // turn — so turn N and N+1 content would collide on reload (later turns' thinking/text collapsing
  // onto the first block, text between tool calls pinned to the top instead of interleaving). Fold
  // in a caller-supplied turn epoch (contentTurnEpoch in useChat, bumped when content resumes after
  // intervening non-content) so each turn stays a distinct block regardless. Defense-in-depth that
  // also covers that on-disk legacy data. Mirrors the tool_call_id disambiguation above.
  if (
    isReasoningMessage(msg) || isReasoningUpdateMessage(msg) ||
    isTextMessage(msg) || isTextUpdateMessage(msg)
  ) {
    return `${mergeKind}-${runId}-${generationId}-${messageOrderIdx}-t${turnSeq}`;
  }

  // ToolsCallUpdate accumulates tools into one array → key on messageOrderIdx only.
  return `${mergeKind}-${runId}-${generationId}-${messageOrderIdx}`;
}
