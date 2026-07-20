import type {
  Message,
  TextMessage,
  ReasoningMessage,
  ToolsCallMessage,
  ToolCallMessage,
  NotifyMessage,
  NotificationDisplayData,
  DisplayItem,
  MessageStatus,
} from '@/types';
import {
  MessageType,
  isNotifyMessage,
  isTextMessage,
  isReasoningMessage,
  isToolsCallMessage,
  isToolCallMessage,
  normalizeReasoningVisibility,
} from '@/types';

/**
 * Minimal shape `buildDisplayItems` needs from an indexed chat message. `useChat`'s
 * `InternalChatMessage` and the sub-agent panel's per-agent index both satisfy this structurally, so
 * the pill-grouping/display logic lives in ONE place instead of being duplicated per consumer.
 */
export interface DisplayableMessage {
  id: string;
  role: 'user' | 'assistant';
  status: MessageStatus;
  content: Message;
  runId?: string | null;
  parentRunId?: string | null;
  messageOrderIdx?: number | null;
  timestamp: number;
}

/**
 * Normalize a NotifyMessage into the shape the notification pill renders. Producing the pill data
 * here (rather than passing the raw message) lets the legacy `context_discovery` TextMessage path
 * feed the SAME pill via a hand-built {@link NotificationDisplayData}.
 */
export function notifyToDisplayData(msg: NotifyMessage): NotificationDisplayData {
  return {
    notifyKind: msg.notify_kind,
    label: msg.label,
    sourceToolName: msg.source_tool_name,
    sourceToolCallId: msg.source_tool_call_id,
    detail: msg.detail,
    text: msg.text,
  };
}

/**
 * Transform an ordered list of indexed messages into display items with pill grouping. Extracted
 * verbatim from useChat's `displayItems` computed body so both the parent chat and the sub-agent
 * panel render identically without duplicating the grouping/dedup logic. Pure: no reactivity, no
 * side effects — callers pass the already-sorted, non-pending messages.
 */
export function buildDisplayItems(sortedMessages: DisplayableMessage[]): DisplayItem[] {
  const items: DisplayItem[] = [];

  let pillBuffer: Array<ReasoningMessage | ToolsCallMessage> = [];
  let pillRunId: string | null = null;
  let pillParentRunId: string | null = null;
  let pillMessageOrderIdx: number | null = null;

  function flushPill() {
    if (pillBuffer.length > 0) {
      items.push({
        type: 'pill',
        id: `pill-${items.length}`,
        items: [...pillBuffer],
        runId: pillRunId,
        parentRunId: pillParentRunId,
        messageOrderIdx: pillMessageOrderIdx,
      });
      pillBuffer = [];
      pillRunId = null;
      pillParentRunId = null;
      pillMessageOrderIdx = null;
    }
  }

  for (const msg of sortedMessages) {
    const content = msg.content;
    // Out-of-band notifications render as a distinct pill, NEVER a user bubble — so this branch
    // must precede the `role === 'user'` catch-all (a NotifyMessage maps to Role.User, and a
    // reload-parsed one is tagged role 'user'). The legacy pre-migration path — a context_discovery
    // marker flattened onto a Role.User TextMessage — is folded into the SAME branch so already
    // persisted rows render one unified context pill (no duplicate, no user bubble) too.
    if (isNotifyMessage(content)) {
      flushPill();
      items.push({
        type: 'notification',
        id: msg.id,
        notification: notifyToDisplayData(content),
        runId: msg.runId,
      });
    } else if (isTextMessage(content) && content.context_discovery != null) {
      flushPill();
      items.push({
        type: 'notification',
        id: msg.id,
        notification: {
          notifyKind: 'context-discovery',
          contextPath: content.context_discovery.path,
          contextTruncated: content.context_discovery.truncated,
          text: content.text,
        },
        runId: msg.runId,
      });
    } else if (msg.role === 'user') {
      flushPill();
      items.push({
        type: 'user-message',
        id: msg.id,
        content: msg.content as TextMessage,
        status: msg.status,
        timestamp: msg.timestamp,
      });
    } else if (isReasoningMessage(msg.content)) {
      const reasoning = msg.content as ReasoningMessage;
      const visibility = normalizeReasoningVisibility(reasoning.visibility);

      // Skip encrypted reasoning (just shows "[Encrypted reasoning hidden]" noise)
      if (visibility === 'Encrypted') {
        continue;
      }

      // Skip duplicate plain reasoning with same content already in pill buffer
      // (backend stores both streamed accumulation and final complete message)
      const isDuplicate = pillBuffer.some(
        (item) =>
          isReasoningMessage(item) &&
          (item as ReasoningMessage).generationId === reasoning.generationId &&
          (item as ReasoningMessage).reasoning === reasoning.reasoning
      );
      if (isDuplicate) {
        continue;
      }

      // Add to pill buffer
      pillBuffer.push(reasoning);
      pillRunId = msg.runId ?? null;
      pillParentRunId = msg.parentRunId ?? null;
      pillMessageOrderIdx = msg.messageOrderIdx ?? null;
    } else if (isToolsCallMessage(msg.content)) {
      // Split multi-tool-call messages into individual pills (one per tool call)
      const toolsCall = msg.content as ToolsCallMessage;
      for (const tc of toolsCall.tool_calls) {
        const singleToolMsg: ToolsCallMessage = {
          $type: MessageType.ToolsCall,
          tool_calls: [tc],
          role: toolsCall.role,
          generationId: toolsCall.generationId,
          runId: toolsCall.runId,
          parentRunId: toolsCall.parentRunId,
          threadId: toolsCall.threadId,
          messageOrderIdx: toolsCall.messageOrderIdx,
        };
        pillBuffer.push(singleToolMsg);
      }
      pillRunId = msg.runId ?? null;
      pillParentRunId = msg.parentRunId ?? null;
      pillMessageOrderIdx = msg.messageOrderIdx ?? null;
    } else if (isToolCallMessage(msg.content)) {
      // Wrap individual tool call as its own pill (no merging)
      const toolCall: ToolCallMessage = msg.content as ToolCallMessage;
      const toolsCallMsg: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        tool_calls: [{
          tool_call_id: toolCall.tool_call_id,
          function_name: toolCall.function_name,
          function_args: toolCall.function_args,
        }],
        role: toolCall.role,
        generationId: toolCall.generationId,
        runId: toolCall.runId,
        parentRunId: toolCall.parentRunId,
        threadId: toolCall.threadId,
        messageOrderIdx: toolCall.messageOrderIdx,
      };
      pillBuffer.push(toolsCallMsg);
      pillRunId = msg.runId ?? null;
      pillParentRunId = msg.parentRunId ?? null;
      pillMessageOrderIdx = msg.messageOrderIdx ?? null;
    } else if (isTextMessage(msg.content)) {
      // Text message - flush pill and add text
      flushPill();
      items.push({
        type: 'assistant-message',
        id: msg.id,
        content: msg.content as TextMessage,
        runId: msg.runId,
        parentRunId: msg.parentRunId,
        messageOrderIdx: msg.messageOrderIdx,
      });
    }
  }

  // Flush any remaining pill items
  flushPill();

  return items;
}
