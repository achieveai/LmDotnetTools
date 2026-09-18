import { computed, onMounted, onScopeDispose, readonly, ref, toValue, type MaybeRefOrGetter } from 'vue';
import {
  listConversations,
  loadConversationMessages,
  type PersistedMessage,
} from '@/api/conversationsApi';
import { listSubAgents, type SubAgentSummary } from '@/api/subAgentsApi';
import type { ConversationSummary } from '@/types/conversations';
import type { ToolCall, ToolCallResultMessage } from '@/types';
import { resolveRenderer } from '@/utils/toolName';

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
  for (const message of messages) {
    if (!message || typeof message !== 'object') continue;
    const value = message as {
      tool_calls?: ToolCall[];
      tool_call_results?: ToolCallResultMessage[];
      tool_call_id?: string | null;
      result?: string;
    };
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
    return result?.is_deferred ? [{ toolCallId, toolCall, result }] : [];
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
  const entries = computed(() => [...entryMap.value.values()]);
  const isRefreshing = ref(false);
  const error = ref<string | null>(null);
  const fingerprints = new Map<string, number>();
  let lastFullSweep = 0;
  let inFlight: Promise<void> | null = null;
  let inFlightIsFull = false;
  let fullRefreshQueued = false;
  let timer: ReturnType<typeof setInterval> | undefined;

  const scopePrefix = (scope: Scope) =>
    `root:${scope.rootThreadId}/agent:${scope.agentId ?? 'root'}/child:${scope.childThreadId ?? 'root'}/tool:`;

  function replaceScope(scope: Scope, rows: PersistedMessage[]) {
    const prefix = scopePrefix(scope);
    const next = new Map(entryMap.value);
    for (const key of next.keys()) if (key.startsWith(prefix)) next.delete(key);
    for (const pending of findPersistedQuestions(rows)) {
      const key = `${prefix}${pending.toolCallId}`;
      next.set(key, { key, ...scope, ...pending, prompt: promptFor(pending.toolCall) });
    }
    entryMap.value = next;
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
      const pendingRoots = new Set([...entryMap.value.values()].map((entry) => entry.rootThreadId));
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
  });
  onScopeDispose(() => {
    if (timer) clearInterval(timer);
    document.removeEventListener('visibilitychange', onVisibilityChange);
  });

  return { entries: readonly(entries), refresh, isRefreshing: readonly(isRefreshing), error: readonly(error) };
}
