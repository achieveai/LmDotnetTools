import { describe, it, expect, afterEach, vi } from 'vitest';
import { ref } from 'vue';
import { useConversationFork } from '@/composables/useConversationFork';
import type { ConversationSummary } from '@/types/conversations';

/** Replaces fetch with a router over `METHOD path` keys; records every request. */
function mockFetch(routes: Record<string, { status: number; body: unknown }>) {
  const original = globalThis.fetch;
  const calls: Array<{ method: string; url: string; body: unknown }> = [];
  globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    const url = String(input);
    calls.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const route = routes[`${method} ${url}`];
    if (!route) return new Response('{}', { status: 404 });
    return new Response(JSON.stringify(route.body), {
      status: route.status,
      headers: { 'Content-Type': 'application/json' },
    });
  }) as unknown as typeof fetch;
  return { calls, restore: () => (globalThis.fetch = original) };
}

const created = (prefillText: string | null) => ({
  threadId: 'fork-1',
  title: 'Plan the migration (fork)',
  forkedFrom: { threadId: 'src', messageId: 'm4', seq: 4 },
  rootThreadId: 'src',
  prefillText,
});

function setup() {
  const conversations = ref<ConversationSummary[]>([
    { threadId: 'src', title: 'Plan the migration', lastUpdated: 0, provider: 'openai', workspace: 'ws', mode: 'm' },
  ]);
  const currentThreadId = ref<string | null>('src');
  const opened: string[] = [];
  const composer: string[] = [];
  const addOrUpdateConversation = vi.fn((summary: ConversationSummary) => {
    conversations.value = [summary, ...conversations.value];
  });
  const forking = useConversationFork({
    currentThreadId,
    conversations,
    addOrUpdateConversation,
    openConversation: async (threadId) => {
      opened.push(threadId);
      currentThreadId.value = threadId;
    },
    setComposerText: (text) => composer.push(text),
  });
  return { forking, conversations, addOrUpdateConversation, opened, composer, currentThreadId };
}

describe('useConversationFork', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('"Fork from here" posts afterRunId, adds the fork to the list and opens it with an empty composer', async () => {
    const mock = mockFetch({ 'POST /api/conversations/src/fork': { status: 201, body: created(null) } });
    restore = mock.restore;
    const { forking, addOrUpdateConversation, opened, composer } = setup();

    await forking.fork({ afterRunId: 'run-2' });

    expect(mock.calls).toEqual([{ method: 'POST', url: '/api/conversations/src/fork', body: { afterRunId: 'run-2' } }]);
    expect(addOrUpdateConversation).toHaveBeenCalledWith(
      expect.objectContaining({
        threadId: 'fork-1',
        title: 'Plan the migration (fork)',
        provider: 'openai',
        workspace: 'ws',
        mode: 'm',
        forkedFrom: { threadId: 'src', messageId: 'm4', seq: 4 },
        rootThreadId: 'src',
      })
    );
    expect(opened).toEqual(['fork-1']);
    expect(composer).toEqual([]);
  });

  it('"Edit in fork" posts beforeMessageId, opens the fork, then pre-fills the composer', async () => {
    const mock = mockFetch({ 'POST /api/conversations/src/fork': { status: 201, body: created('old user text') } });
    restore = mock.restore;
    const { forking, opened, composer } = setup();

    await forking.fork({ beforeMessageId: 'pm-u2' });

    expect(mock.calls[0].body).toEqual({ beforeMessageId: 'pm-u2' });
    expect(opened).toEqual(['fork-1']);
    expect(composer).toEqual(['old user text']);
  });

  it('surfaces a refusal and opens nothing', async () => {
    const mock = mockFetch({
      'POST /api/conversations/src/fork': { status: 409, body: { code: 'turn_in_progress', error: 'busy' } },
    });
    restore = mock.restore;
    const { forking, opened, addOrUpdateConversation } = setup();

    await forking.fork({ afterRunId: 'run-2' });

    expect(forking.forkError.value).toBe('Cannot fork while a reply is still running.');
    expect(opened).toEqual([]);
    expect(addOrUpdateConversation).not.toHaveBeenCalled();
  });

  it('loads branch points for the conversation on screen and drops a stale answer', async () => {
    const points = [{ afterMessageId: 'm4', afterSeq: 4, options: [] }];
    const mock = mockFetch({ 'GET /api/conversations/src/branches': { status: 200, body: { points } } });
    restore = mock.restore;
    const { forking, currentThreadId } = setup();

    await forking.refreshBranches('src');
    expect(forking.branchPoints.value).toEqual(points);

    const pending = forking.refreshBranches('src');
    currentThreadId.value = 'other';
    await pending;
    expect(forking.branchPoints.value).toEqual([]);
  });
});
