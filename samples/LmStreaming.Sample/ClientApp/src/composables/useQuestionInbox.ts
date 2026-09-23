import { computed, onMounted, onScopeDispose, readonly, ref, toValue, type MaybeRefOrGetter } from 'vue';
import {
  listConversations,
  loadConversationMessages,
  type PersistedMessage,
} from '@/api/conversationsApi';
import { listSubAgents, type SubAgentSummary } from '@/api/subAgentsApi';
import {
  connectQuestionEvents,
  type PendingQuestionEvent,
  type QuestionEventStream,
} from '@/api/eventsWsClient';
import type { ConversationSummary } from '@/types/conversations';
import type { ToolCall, ToolCallResultMessage } from '@/types';
import { resolveRenderer } from '@/utils/toolName';
import { answeredQuestionIdFromText, isQuestionAwaitingAnswer } from '@/utils/pendingQuestions';

export interface QuestionInboxEntry {
  key: string;
  rootThreadId: string;
  conversationTitle: string;
  /** Full summary lets navigation restore provider/mode/workspace for an unloaded older thread. */
  conversation: ConversationSummary;
  agentId: string | null;
  agentName: string | null;
  childThreadId: string | null;
  toolCallId: string;
  toolCall: ToolCall;
  result: ToolCallResultMessage;
  prompt: string;
}

export interface QuestionInboxDependencies {
  listConversations: typeof listConversations;
  loadConversationMessages: typeof loadConversationMessages;
  listSubAgents: typeof listSubAgents;
}

export interface QuestionInboxOptions {
  pollIntervalMs?: number;
  fullSweepIntervalMs?: number;
  pageSize?: number;
  dependencies?: QuestionInboxDependencies;
  /**
   * Opens the app-wide question stream. Injectable for tests; defaults to the real `/ws/events`
   * client. The polling sweep below is NOT removed when this is connected — it stays as the fallback
   * for a socket that cannot be opened at all, and for questions parked by a loop that has since
   * been evicted from the pool (which the server's in-memory snapshot no longer holds).
   */
  events?: typeof connectQuestionEvents;
  /**
   * Called once per question that becomes newly pending — from a push, not from a sweep. Lets a
   * caller announce an arrival (a toast) without diffing `entries` itself. Not called for a
   * question already in the inbox, so a reconnect's snapshot re-announces nothing.
   */
  onQuestionRaised?: (question: PendingQuestionEvent) => void;
}

type Scope = Omit<QuestionInboxEntry, 'key' | 'toolCallId' | 'toolCall' | 'result' | 'prompt'>;

function parseMessages(rows: PersistedMessage[]): unknown[] {
  return rows.flatMap((row) => {
    try {
      return [JSON.parse(row.messageJson)];
    } catch {
      return [];
    }
  });
}

function promptFor(call: ToolCall): string {
  try {
    const args = JSON.parse(call.function_args ?? '{}') as Record<string, unknown>;
    if (typeof args.question === 'string') return args.question;
    if (Array.isArray(args.questions)) {
      const first = args.questions[0] as Record<string, unknown> | undefined;
      if (typeof first?.prompt === 'string') return first.prompt;
      if (typeof first?.question === 'string') return first.question;
    }
  } catch {
    // The raw call remains available to the active dock; the inbox can use a safe label.
  }
  return 'Question waiting for your answer';
}

function findPersistedQuestions(rows: PersistedMessage[]): Array<{
  toolCallId: string;
  toolCall: ToolCall;
  result: ToolCallResultMessage;
}> {
  const messages = parseMessages(rows);
  const results = new Map<string, ToolCallResultMessage>();
  const calls: ToolCall[] = [];
  // Questions settled early keep their placeholder; the redirected answer is a user text row.
  const answered = new Set<string>();
  for (const message of messages) {
    if (!message || typeof message !== 'object') continue;
    const value = message as {
      tool_calls?: ToolCall[];
      tool_call_results?: ToolCallResultMessage[];
      tool_call_id?: string | null;
      result?: string;
      text?: string;
    };
    if (typeof value.text === 'string') {
      const answeredId = answeredQuestionIdFromText(value.text);
      if (answeredId) answered.add(answeredId);
    }
    if (Array.isArray(value.tool_calls)) calls.push(...value.tool_calls);
    const resultRows = Array.isArray(value.tool_call_results)
      ? value.tool_call_results
      : value.tool_call_id && typeof value.result === 'string'
        ? [value as ToolCallResultMessage]
        : [];
    for (const result of resultRows) {
      if (result.tool_call_id) results.set(result.tool_call_id, result);
    }
  }
  const seen = new Set<string>();
  return calls.flatMap((toolCall) => {
    const toolCallId = toolCall.tool_call_id;
    if (!toolCallId || seen.has(toolCallId)) return [];
    seen.add(toolCallId);
    if (resolveRenderer(toolCall.function_name).family !== 'question') return [];
    const result = results.get(toolCallId);
    return result && isQuestionAwaitingAnswer(result, (id) => answered.has(id))
      ? [{ toolCallId, toolCall, result }]
      : [];
  });
}

function createLimiter(limit: number) {
  let active = 0;
  const waiting: Array<() => void> = [];
  return async <T>(work: () => Promise<T>): Promise<T> => {
    if (active >= limit) await new Promise<void>((resolve) => waiting.push(resolve));
    active += 1;
    try {
      return await work();
    } finally {
      active -= 1;
      waiting.shift()?.();
    }
  };
}

export function useQuestionInbox(
  currentThreadId: MaybeRefOrGetter<string | null>,
  options: QuestionInboxOptions = {}
) {
  const deps = options.dependencies ?? { listConversations, loadConversationMessages, listSubAgents };
  const pageSize = options.pageSize ?? 30;
  const pollIntervalMs = options.pollIntervalMs ?? 30_000;
  const fullSweepIntervalMs = options.fullSweepIntervalMs ?? 5 * 60_000;
  const entryMap = ref(new Map<string, QuestionInboxEntry>());
  /**
   * Entries synthesized from a PUSH, held apart from the swept ones so the two cannot erase each
   * other. A pushed question is real before any read confirms it, and the sweep that confirms it
   * needs the sub-agent roster — which lags the spawn — so folding pushes into `entryMap` would let
   * `removeMissingChildren` delete a question the server has just told us is waiting.
   */
  const pushedMap = ref(new Map<string, QuestionInboxEntry>());
  const entries = computed(() => {
    // Swept last: it carries the real tool call, result and conversation summary, so where both
    // sources know a key the read-through entry wins over the synthesized one.
    const merged = new Map(pushedMap.value);
    for (const [key, entry] of entryMap.value) merged.set(key, entry);
    return [...merged.values()];
  });
  const isRefreshing = ref(false);
  const error = ref<string | null>(null);
  const fingerprints = new Map<string, number>();
  let lastFullSweep = 0;
  let inFlight: Promise<void> | null = null;
  let inFlightIsFull = false;
  let fullRefreshQueued = false;
  let timer: ReturnType<typeof setInterval> | undefined;
  const openEvents = options.events ?? connectQuestionEvents;
  let stream: QuestionEventStream | null = null;

  const scopePrefix = (scope: Scope) =>
    `root:${scope.rootThreadId}/agent:${scope.agentId ?? 'root'}/child:${scope.childThreadId ?? 'root'}/tool:`;

  function replaceScope(scope: Scope, rows: PersistedMessage[]) {
    const prefix = scopePrefix(scope);
    const next = new Map(entryMap.value);
    for (const key of next.keys()) if (key.startsWith(prefix)) next.delete(key);
    const found = new Set<string>();
    for (const pending of findPersistedQuestions(rows)) {
      const key = `${prefix}${pending.toolCallId}`;
      found.add(key);
      next.set(key, { key, ...scope, ...pending, prompt: promptFor(pending.toolCall) });
    }
    entryMap.value = next;
    // This read is authoritative for THIS scope's transcript, so a pushed entry it did not find is
    // settled (or was never persisted) and must go. That is the only place a push is retired other
    // than an explicit `question_settled`; a roster that has not caught up cannot retire one,
    // because `removeMissingChildren` never touches this map.
    dropPushed((key) => key.startsWith(prefix) && !found.has(key));
  }

  function dropPushed(matches: (key: string, entry: QuestionInboxEntry) => boolean) {
    let removed = false;
    const next = new Map(pushedMap.value);
    for (const [key, entry] of [...next]) {
      if (matches(key, entry)) {
        next.delete(key);
        removed = true;
      }
    }
    if (removed) pushedMap.value = next;
  }

  async function allConversations(): Promise<ConversationSummary[]> {
    const all: ConversationSummary[] = [];
    for (let offset = 0; ; offset += pageSize) {
      const page = await deps.listConversations(pageSize, offset);
      all.push(...page);
      if (page.length < pageSize) return all;
    }
  }

  async function run(full: boolean) {
    isRefreshing.value = true;
    const failures: unknown[] = [];
    try {
      const listed = await allConversations();
      const active = toValue(currentThreadId);
      const ordered = [...listed].sort((a, b) =>
        a.threadId === active ? -1 : b.threadId === active ? 1 : 0
      );
      const knownRoots = new Set(ordered.map((conversation) => conversation.threadId));
      // Both sources: a root whose only pending question arrived by PUSH still has to be re-read,
      // and its `lastUpdated` has not moved (the run is parked, not finished), so the fingerprint
      // test below would otherwise skip exactly the conversation the push was about.
      const pendingRoots = new Set(entries.value.map((entry) => entry.rootThreadId));
      const candidates = ordered.filter(
        (conversation) =>
          full ||
          conversation.threadId === active ||
          pendingRoots.has(conversation.threadId) ||
          fingerprints.get(conversation.threadId) !== conversation.lastUpdated
      );
      const limit = createLimiter(2);
      await Promise.all(
        candidates.map(async (conversation) => {
          let complete = true;
          const rootScope: Scope = {
            rootThreadId: conversation.threadId,
            conversationTitle: conversation.title,
            conversation,
            agentId: null,
            agentName: 'Main agent',
            childThreadId: null,
          };
          const rootRead = limit(() => deps.loadConversationMessages(conversation.threadId))
            .then((rows) => replaceScope(rootScope, rows))
            .catch((reason) => {
              complete = false;
              failures.push(reason);
            });
          const rosterRead = limit(() => deps.listSubAgents(conversation.threadId))
            .then(async (children) => {
              const readable = children.filter((child) => child.isReadable !== false);
              await Promise.all(
                readable.map((child) =>
                  limit(() => deps.loadConversationMessages(child.threadId))
                    .then((rows) =>
                      replaceScope(
                        {
                          rootThreadId: conversation.threadId,
                          conversationTitle: conversation.title,
                          conversation,
                          agentId: child.agentId,
                          agentName: child.name ?? child.template,
                          childThreadId: child.threadId,
                        },
                        rows
                      )
                    )
                    .catch((reason) => {
                      complete = false;
                      failures.push(reason);
                    })
                )
              );
              removeMissingChildren(conversation.threadId, readable);
            })
            .catch((reason) => {
              complete = false;
              failures.push(reason);
            });
          await Promise.all([rootRead, rosterRead]);
          if (complete) fingerprints.set(conversation.threadId, conversation.lastUpdated);
        })
      );
      if (full) {
        const next = new Map(entryMap.value);
        for (const [key, entry] of next) if (!knownRoots.has(entry.rootThreadId)) next.delete(key);
        entryMap.value = next;
        dropPushed((_key, entry) => !knownRoots.has(entry.rootThreadId));
        lastFullSweep = Date.now();
      }
    } catch (reason) {
      failures.push(reason);
    } finally {
      error.value = failures.length
        ? `${failures.length} question source${failures.length === 1 ? '' : 's'} could not be refreshed.`
        : null;
      isRefreshing.value = false;
    }
  }

  function removeMissingChildren(rootThreadId: string, children: SubAgentSummary[]) {
    const childIds = new Set(children.map((child) => child.threadId));
    const next = new Map(entryMap.value);
    for (const [key, entry] of next) {
      if (entry.rootThreadId === rootThreadId && entry.childThreadId && !childIds.has(entry.childThreadId)) {
        next.delete(key);
      }
    }
    entryMap.value = next;
  }

  /**
   * Builds the entry a pushed question stands in as until a read confirms it.
   *
   * The key is composed with the SAME `scopePrefix` the sweep uses, so when the read-through entry
   * lands it occupies this exact slot instead of doubling the question. `toolCall`/`result` are
   * placeholders: the event carries the rendered prompt, not the raw arguments, and nothing on the
   * open path renders them — `openInboxQuestion` re-reads the live result out of the loaded
   * transcript before it shows a form.
   */
  function pushedEntry(question: PendingQuestionEvent): QuestionInboxEntry {
    const known = [...entryMap.value.values(), ...pushedMap.value.values()].find(
      (entry) => entry.rootThreadId === question.rootThreadId
    );
    const conversation: ConversationSummary = known?.conversation ?? {
      threadId: question.rootThreadId,
      title: question.conversationTitle ?? 'Conversation',
      lastUpdated: 0,
    };
    const scope: Scope = {
      rootThreadId: question.rootThreadId,
      conversationTitle: question.conversationTitle ?? conversation.title,
      conversation,
      agentId: question.agentId,
      agentName:
        question.agentId === null ? 'Main agent' : (question.agentName ?? question.agentId),
      childThreadId: question.childThreadId,
    };
    const key = `${scopePrefix(scope)}${question.toolCallId}`;
    const deferredAt = Date.parse(question.raisedAtUtc);
    return {
      key,
      ...scope,
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

  /** True when neither source already holds this question. */
  function isNew(key: string) {
    return !entryMap.value.has(key) && !pushedMap.value.has(key);
  }

  /** Adds the question if it is not already known; returns true when it was genuinely new. */
  function applyPending(question: PendingQuestionEvent): boolean {
    const entry = pushedEntry(question);
    const fresh = isNew(entry.key);
    if (!entryMap.value.has(entry.key)) {
      pushedMap.value = new Map(pushedMap.value).set(entry.key, entry);
    }
    return fresh;
  }

  function applySettled(rootThreadId: string, toolCallId: string) {
    const suffix = `/tool:${toolCallId}`;
    const stale = (key: string) => key.startsWith(`root:${rootThreadId}/`) && key.endsWith(suffix);
    dropPushed(stale);
    const next = new Map(entryMap.value);
    let removed = false;
    for (const key of [...next.keys()]) {
      if (stale(key)) {
        next.delete(key);
        removed = true;
      }
    }
    if (removed) entryMap.value = next;
  }

  function requestRefresh(full: boolean): Promise<void> {
    if (inFlight) {
      if (full && !inFlightIsFull) fullRefreshQueued = true;
      return inFlight.then(() => (fullRefreshQueued ? requestRefresh(true) : undefined));
    }
    inFlightIsFull = full;
    if (full) fullRefreshQueued = false;
    inFlight = run(full).finally(() => {
      inFlight = null;
      inFlightIsFull = false;
    });
    return inFlight;
  }

  const refresh = () => requestRefresh(true);
  const automaticRefresh = () =>
    requestRefresh(Date.now() - lastFullSweep >= fullSweepIntervalMs);
  const onVisibilityChange = () => {
    if (document.visibilityState === 'visible') void automaticRefresh();
  };

  onMounted(() => {
    void refresh();
    timer = setInterval(() => {
      if (document.visibilityState === 'visible') void automaticRefresh();
    }, pollIntervalMs);
    document.addEventListener('visibilitychange', onVisibilityChange);
    // Push is what makes a question raised elsewhere visible in a second; the timer above stays as
    // the fallback for a socket that never opens and for questions the server's in-memory state no
    // longer holds.
    stream = openEvents({
      onSnapshot: (questions) => {
        const added = questions.map(applyPending).filter(Boolean).length;
        // A reconnect's snapshot must not re-announce what the user has already been shown, so the
        // toast hook is deliberately not called here — only genuinely new arrivals are announced.
        if (added > 0) void requestRefresh(false);
      },
      onPending: (question) => {
        if (applyPending(question)) options.onQuestionRaised?.(question);
        // Targeted, not full: only this root can have changed, and the read replaces the placeholder
        // with the real call. Cheap, because the incremental sweep re-reads any root that has a
        // pending entry — which this one now does.
        void requestRefresh(false);
      },
      onSettled: applySettled,
    });
  });
  onScopeDispose(() => {
    if (timer) clearInterval(timer);
    document.removeEventListener('visibilitychange', onVisibilityChange);
    stream?.close();
    stream = null;
  });

  return { entries: readonly(entries), refresh, isRefreshing: readonly(isRefreshing), error: readonly(error) };
}
