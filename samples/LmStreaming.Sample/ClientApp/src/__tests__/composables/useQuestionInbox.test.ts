import { defineComponent, nextTick, ref } from 'vue';
import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useQuestionInbox, type QuestionInboxDependencies } from '@/composables/useQuestionInbox';
import type { PersistedMessage } from '@/api/conversationsApi';
import type { ConversationSummary } from '@/types/conversations';

function conversation(threadId: string, lastUpdated = 1): ConversationSummary {
  return { threadId, title: `Conversation ${threadId}`, lastUpdated, provider: 'p', mode: 'm', workspace: 'w' };
}

function history(id: string, deferred = true): PersistedMessage[] {
  const row = (suffix: string, value: unknown): PersistedMessage => ({
    id: `${id}-${suffix}`,
    threadId: 'unused',
    runId: 'run',
    timestamp: 1,
    messageType: '',
    role: '',
    messageJson: JSON.stringify(value),
  });
  return [
    row('call', {
      $type: 'tools_call',
      tool_calls: [{
        tool_call_id: id,
        function_name: 'AskUserQuestion',
        function_args: '{"questions":[{"prompt":"Choose one","options":["A","B"]}]}',
      }],
    }),
    row('result', { $type: 'tool_call_result', tool_call_id: id, result: '', is_deferred: deferred }),
  ];
}

function render(dependencies: QuestionInboxDependencies, current = ref<string | null>(null), options = {}) {
  let inbox!: ReturnType<typeof useQuestionInbox>;
  const wrapper = mount(
    defineComponent({
      setup() {
        inbox = useQuestionInbox(current, { dependencies, pollIntervalMs: 60_000, ...options });
        return () => null;
      },
    })
  );
  return { wrapper, inbox };
}

afterEach(() => vi.restoreAllMocks());

describe('useQuestionInbox', () => {
  it('pages every conversation, scans the active root first, and keeps root/child identity distinct', async () => {
    const pages = [[conversation('old'), conversation('active')], [conversation('last')], []];
    const order: string[] = [];
    const dependencies: QuestionInboxDependencies = {
      listConversations: vi.fn(async (_limit, offset) => pages[offset / 2] ?? []),
      loadConversationMessages: vi.fn(async (threadId) => {
        order.push(threadId);
        return history('same-id');
      }),
      listSubAgents: vi.fn(async (root) =>
        root === 'active'
          ? [{ agentId: 'agent-1', template: 'helper', task: '', status: 'running' as const, threadId: 'child-1' }]
          : []
      ),
    };
    const { wrapper, inbox } = render(dependencies, ref('active'), { pageSize: 2 });
    await vi.waitFor(() => expect(inbox.isRefreshing.value).toBe(false));
    expect(dependencies.listConversations).toHaveBeenCalledTimes(2);
    expect(order[0]).toBe('active');
    expect(inbox.entries.value).toHaveLength(4);
    expect(new Set(inbox.entries.value.map((entry) => entry.key)).size).toBe(4);
    expect(inbox.entries.value.find((entry) => entry.childThreadId)?.agentName).toBe('helper');
    expect(inbox.entries.value[0].prompt).toBe('Choose one');
    expect(inbox.entries.value[0].conversation.workspace).toBe('w');
    wrapper.unmount();
  });

  it('limits history and roster requests to two concurrent operations', async () => {
    let active = 0;
    let maximum = 0;
    const guarded = async <T>(value: T): Promise<T> => {
      active += 1;
      maximum = Math.max(maximum, active);
      await new Promise((resolve) => setTimeout(resolve, 5));
      active -= 1;
      return value;
    };
    const dependencies: QuestionInboxDependencies = {
      listConversations: vi.fn(async () => [conversation('a'), conversation('b'), conversation('c')]),
      loadConversationMessages: vi.fn(() => guarded([])),
      listSubAgents: vi.fn(() => guarded([])),
    };
    const { wrapper, inbox } = render(dependencies);
    await vi.waitFor(() => expect(inbox.isRefreshing.value).toBe(false));
    expect(maximum).toBe(2);
    wrapper.unmount();
  });

  it('retains an entry on a transient fetch error and removes it after canonical resolution', async () => {
    let state: 'pending' | 'error' | 'resolved' = 'pending';
    const dependencies: QuestionInboxDependencies = {
      listConversations: vi.fn(async () => [conversation('root')]),
      loadConversationMessages: vi.fn(async () => {
        if (state === 'error') throw new Error('offline');
        return history('question', state === 'pending');
      }),
      listSubAgents: vi.fn(async () => []),
    };
    const { wrapper, inbox } = render(dependencies);
    await vi.waitFor(() => expect(inbox.entries.value).toHaveLength(1));
    state = 'error';
    await inbox.refresh();
    expect(inbox.entries.value).toHaveLength(1);
    expect(inbox.error.value).toMatch(/could not be refreshed/);
    state = 'resolved';
    await inbox.refresh();
    expect(inbox.entries.value).toHaveLength(0);
    expect(inbox.error.value).toBeNull();
    wrapper.unmount();
  });

  it('coalesces overlapping manual refreshes', async () => {
    let release!: () => void;
    const blocked = new Promise<void>((resolve) => (release = resolve));
    const dependencies: QuestionInboxDependencies = {
      listConversations: vi.fn(async () => {
        await blocked;
        return [];
      }),
      loadConversationMessages: vi.fn(async () => []),
      listSubAgents: vi.fn(async () => []),
    };
    const { wrapper, inbox } = render(dependencies);
    await nextTick();
    const first = inbox.refresh();
    const second = inbox.refresh();
    release();
    await Promise.all([first, second]);
    expect(dependencies.listConversations).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });
});
