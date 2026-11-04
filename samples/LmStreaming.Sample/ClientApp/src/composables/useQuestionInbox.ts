import { computed, onMounted, onScopeDispose, readonly, ref } from 'vue';
import {
  connectQuestionEvents,
  type PendingQuestionEvent,
  type QuestionEventStream,
} from '@/api/eventsWsClient';
import type { ConversationSummary } from '@/types/conversations';
import type { ToolCall, ToolCallResultMessage } from '@/types';

export interface QuestionInboxEntry {
  key: string;
  rootThreadId: string;
  conversationTitle: string;
  /** Enough of a summary to open a conversation the sidebar has not listed yet, bindings included. */
  conversation: ConversationSummary;
  agentId: string | null;
  agentName: string | null;
  childThreadId: string | null;
  toolCallId: string;
  toolCall: ToolCall;
  result: ToolCallResultMessage;
  prompt: string;
}

export interface QuestionInboxOptions {
  /** Opens the app-wide question stream. Injectable for tests; defaults to the real `/ws/events` client. */
  events?: typeof connectQuestionEvents;
  /**
   * Called once per question that becomes newly pending — from a push, not from a snapshot. Lets a
   * caller announce an arrival (a toast) without diffing `entries` itself. Not called for a question
   * already in the inbox, so a reconnect re-announces nothing.
   */
  onQuestionRaised?: (question: PendingQuestionEvent) => void;
}

/**
 * Every question waiting for the human, in any conversation, from the server's `/ws/events` stream.
 *
 * WHY NO READS: this used to find questions by re-reading transcripts — every conversation's
 * messages and sub-agent roster, then every child's messages — on mount, every 30 seconds, every 5
 * minutes in full, and each time the inbox opened. With thousands of conversations a full pass took
 * longer than its own interval, so it never stopped, and half the roster reads came back 503. The
 * server now keeps the list itself, across restarts, so its snapshot is the whole answer and the
 * pushes keep it current.
 */
export function useQuestionInbox(options: QuestionInboxOptions = {}) {
  const entryMap = ref(new Map<string, QuestionInboxEntry>());
  const entries = computed(() => [...entryMap.value.values()]);
  const isRefreshing = ref(true);
  const error = ref<string | null>(null);
  const openEvents = options.events ?? connectQuestionEvents;
  let stream: QuestionEventStream | null = null;

  const keyOf = (rootThreadId: string, agentId: string | null, toolCallId: string) =>
    `root:${rootThreadId}/agent:${agentId ?? 'root'}/tool:${toolCallId}`;

  /**
   * The entry for one pushed question. `toolCall`/`result` are placeholders: the event carries the
   * rendered prompt, not the raw arguments, and nothing on the open path renders them —
   * `openInboxQuestion` re-reads the live result out of the loaded transcript before it shows a form.
   */
  function entryFor(question: PendingQuestionEvent): QuestionInboxEntry {
    const conversation: ConversationSummary = {
      threadId: question.rootThreadId,
      title: question.conversationTitle ?? 'Conversation',
      lastUpdated: 0,
      provider: question.provider,
      mode: question.mode,
      workspace: question.workspace,
    };
    const deferredAt = Date.parse(question.raisedAtUtc);
    return {
      key: keyOf(question.rootThreadId, question.agentId, question.toolCallId),
      rootThreadId: question.rootThreadId,
      conversationTitle: conversation.title,
      conversation,
      agentId: question.agentId,
      agentName: question.agentId === null ? 'Main agent' : (question.agentName ?? question.agentId),
      childThreadId: question.childThreadId,
      toolCallId: question.toolCallId,
      toolCall: {
        tool_call_id: question.toolCallId,
        function_name: 'AskUserQuestion',
        function_args: null,
      },
      result: {
        $type: 'tool_call_result',
        role: 'tool',
        tool_call_id: question.toolCallId,
        result: '',
        is_deferred: true,
        deferred_at: Number.isNaN(deferredAt) ? null : deferredAt,
      },
      prompt: question.prompt,
    };
  }

  /** Replaces the list: a snapshot is the server's complete record, not an addition to ours. */
  function applySnapshot(questions: PendingQuestionEvent[]) {
    entryMap.value = new Map(questions.map((question) => {
      const entry = entryFor(question);
      return [entry.key, entry];
    }));
    isRefreshing.value = false;
  }

  /** Adds the question; returns true when it was not already listed. */
  function applyPending(question: PendingQuestionEvent): boolean {
    const entry = entryFor(question);
    const fresh = !entryMap.value.has(entry.key);
    entryMap.value = new Map(entryMap.value).set(entry.key, entry);
    return fresh;
  }

  function applySettled(rootThreadId: string, toolCallId: string) {
    const next = new Map(entryMap.value);
    let removed = false;
    for (const [key, entry] of next) {
      if (entry.rootThreadId === rootThreadId && entry.toolCallId === toolCallId) {
        next.delete(key);
        removed = true;
      }
    }
    if (removed) entryMap.value = next;
  }

  function connect() {
    stream?.close();
    isRefreshing.value = true;
    stream = openEvents({
      onSnapshot: applySnapshot,
      onPending: (question) => {
        if (applyPending(question)) options.onQuestionRaised?.(question);
      },
      onSettled: applySettled,
      onConnectionChange: (connected) => {
        error.value = connected ? null : 'Live question updates are reconnecting.';
      },
    });
  }

  /** Reconnects for a fresh snapshot. A socket and one frame — never a transcript read. */
  function refresh(): Promise<void> {
    connect();
    return Promise.resolve();
  }

  onMounted(connect);
  onScopeDispose(() => {
    stream?.close();
    stream = null;
  });

  return { entries: readonly(entries), refresh, isRefreshing: readonly(isRefreshing), error: readonly(error) };
}
