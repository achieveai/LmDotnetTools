import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { effectScope } from 'vue';
import {
  useConversations,
  CONVERSATIONS_PAGE_SIZE,
  SORT_MODE_STORAGE_KEY,
} from '@/composables/useConversations';
import type { ConversationSummary } from '@/types/conversations';

/**
 * Recorded request URLs, in order. Every assertion about paging is ultimately an assertion about
 * this list: the endpoint returns a bare array with no `hasMore`, so "did we stop?" and "did we ask
 * for the right offset?" are only observable here.
 */
let calls: string[] = [];

/** Pages handed out, in order, when the fetch stub is auto-responding. */
let queue: ConversationSummary[][] = [];

/** When true the stub parks each request until the test resolves it, so races are deterministic. */
let manual = false;

/** Resolvers for the parked requests, in arrival order. */
let parked: Array<(rows: ConversationSummary[]) => void> = [];

let originalFetch: typeof globalThis.fetch;

function jsonResponse(rows: ConversationSummary[]): Response {
  return new Response(JSON.stringify(rows), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function installFetch(): void {
  globalThis.fetch = vi.fn((input: RequestInfo | URL) => {
    calls.push(String(input));
    if (manual) {
      return new Promise<Response>((resolve) => {
        parked.push((rows) => resolve(jsonResponse(rows)));
      });
    }
    return Promise.resolve(jsonResponse(queue.shift() ?? []));
  }) as unknown as typeof globalThis.fetch;
}

/** Resolves the oldest parked request and lets its continuation run. */
async function releaseParked(rows: ConversationSummary[]): Promise<void> {
  const resolve = parked.shift();
  expect(resolve, 'expected a parked request to release').toBeDefined();
  resolve!(rows);
  // Two turns: one for the Response, one for `.json()`.
  await Promise.resolve();
  await Promise.resolve();
}

function summary(id: string, lastUpdated = 0): ConversationSummary {
  return { threadId: id, title: `Conversation ${id}`, lastUpdated };
}

/** A page of `count` rows whose ids are `c{start}`…, matching the backend's own ordering. */
function page(count: number, start = 0): ConversationSummary[] {
  return Array.from({ length: count }, (_, i) => summary(`c${start + i}`, 1000 - (start + i)));
}

function ids(list: ConversationSummary[]): string[] {
  return list.map((c) => c.threadId);
}

/** Runs the composable inside an effect scope so its reactive state is created (and disposable). */
function harness() {
  const scope = effectScope();
  const api = scope.run(() => useConversations())!;
  return { api, scope };
}

beforeEach(() => {
  calls = [];
  queue = [];
  parked = [];
  manual = false;
  originalFetch = globalThis.fetch;
  installFetch();
  localStorage.clear();
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  vi.restoreAllMocks();
  localStorage.clear();
});

describe('useConversations — incremental paging', () => {
  it('requests the first page with the default sort at offset 0', async () => {
    queue = [page(CONVERSATIONS_PAGE_SIZE)];
    const { api, scope } = harness();

    await api.loadConversations();

    expect(calls).toEqual(['/api/conversations?limit=30&offset=0&sort=lastUsed']);
    expect(api.conversations.value).toHaveLength(CONVERSATIONS_PAGE_SIZE);
    expect(api.hasMoreConversations.value).toBe(true);
    scope.stop();
  });

  it('loads the next page at offset 30 and APPENDS it', async () => {
    queue = [page(CONVERSATIONS_PAGE_SIZE), page(5, CONVERSATIONS_PAGE_SIZE)];
    const { api, scope } = harness();

    await api.loadConversations();
    await api.loadMoreConversations();

    expect(calls[1]).toBe('/api/conversations?limit=30&offset=30&sort=lastUsed');
    expect(api.conversations.value).toHaveLength(35);
    // Appended, not prepended and not re-sorted.
    expect(ids(api.conversations.value).slice(0, 3)).toEqual(['c0', 'c1', 'c2']);
    expect(ids(api.conversations.value).slice(-2)).toEqual(['c33', 'c34']);
    scope.stop();
  });

  it('treats a short page as the last page and stops requesting', async () => {
    queue = [page(CONVERSATIONS_PAGE_SIZE), page(7, CONVERSATIONS_PAGE_SIZE)];
    const { api, scope } = harness();

    await api.loadConversations();
    await api.loadMoreConversations();
    expect(api.hasMoreConversations.value).toBe(false);

    await api.loadMoreConversations();
    await api.loadMoreConversations();

    expect(calls).toHaveLength(2);
    expect(api.conversations.value).toHaveLength(37);
    scope.stop();
  });

  it('marks the list exhausted when even the FIRST page is short', async () => {
    queue = [page(10)];
    const { api, scope } = harness();

    await api.loadConversations();
    expect(api.hasMoreConversations.value).toBe(false);

    await api.loadMoreConversations();

    expect(calls).toHaveLength(1);
    scope.stop();
  });

  it('never has two page requests in flight at once', async () => {
    queue = [page(CONVERSATIONS_PAGE_SIZE)];
    const { api, scope } = harness();
    await api.loadConversations();

    // Both scroll triggers fire before the first page resolves.
    manual = true;
    const first = api.loadMoreConversations();
    const second = api.loadMoreConversations();

    expect(calls).toHaveLength(2);
    expect(api.isLoadingMore.value).toBe(true);

    await releaseParked(page(5, CONVERSATIONS_PAGE_SIZE));
    await Promise.all([first, second]);

    expect(calls).toHaveLength(2);
    expect(api.conversations.value).toHaveLength(35);
    expect(api.isLoadingMore.value).toBe(false);
    scope.stop();
  });

  it('does not start a page load while the FIRST page is still in flight', async () => {
    manual = true;
    const { api, scope } = harness();
    const initial = api.loadConversations();

    await api.loadMoreConversations();
    expect(calls).toHaveLength(1);

    await releaseParked(page(CONVERSATIONS_PAGE_SIZE));
    await initial;
    scope.stop();
  });

  it('appends no duplicate threadIds when a page overlaps what is already held', async () => {
    const firstPage = page(CONVERSATIONS_PAGE_SIZE);
    // The backend's second page repeats the last row of the first (a row shifted while paging).
    const secondPage = [summary('c29', 971), ...page(4, CONVERSATIONS_PAGE_SIZE)];
    queue = [firstPage, secondPage];
    const { api, scope } = harness();

    await api.loadConversations();
    await api.loadMoreConversations();

    const seen = ids(api.conversations.value);
    expect(new Set(seen).size).toBe(seen.length);
    expect(seen.filter((id) => id === 'c29')).toHaveLength(1);
    scope.stop();
  });

  it('keeps a locally-added, not-yet-persisted conversation that the fetch does not return', async () => {
    manual = true;
    const { api, scope } = harness();
    const pending = api.loadConversations();

    // The user's first send in a brand-new thread lands while the mount fetch is still in flight.
    api.addOrUpdateConversation(summary('local-thread', 9999));

    await releaseParked(page(3));
    await pending;

    expect(ids(api.conversations.value)).toEqual(['local-thread', 'c0', 'c1', 'c2']);
    scope.stop();
  });

  it('does not count a local-only entry into the next page offset', async () => {
    manual = true;
    const { api, scope } = harness();
    const pending = api.loadConversations();
    api.addOrUpdateConversation(summary('local-thread', 9999));
    await releaseParked(page(CONVERSATIONS_PAGE_SIZE));
    await pending;
    expect(api.conversations.value).toHaveLength(CONVERSATIONS_PAGE_SIZE + 1);

    manual = false;
    queue = [page(2, CONVERSATIONS_PAGE_SIZE)];
    await api.loadMoreConversations();

    // 30 fetched rows so far — the local-only entry must not push the offset to 31.
    expect(calls[1]).toBe('/api/conversations?limit=30&offset=30&sort=lastUsed');
    scope.stop();
  });
});

describe('useConversations — sort modes', () => {
  it('switching sort clears the list and refetches from offset 0 with the new sort', async () => {
    queue = [page(CONVERSATIONS_PAGE_SIZE), page(CONVERSATIONS_PAGE_SIZE, CONVERSATIONS_PAGE_SIZE)];
    const { api, scope } = harness();
    await api.loadConversations();
    await api.loadMoreConversations();
    expect(api.conversations.value).toHaveLength(60);

    queue = [[summary('created-a', 1), summary('created-b', 2)]];
    await api.setSortMode('created');

    expect(calls[2]).toBe('/api/conversations?limit=30&offset=0&sort=created');
    // Cleared: none of the lastUsed-ordered rows survive into the created-ordered list.
    expect(ids(api.conversations.value)).toEqual(['created-a', 'created-b']);
    expect(api.sortMode.value).toBe('created');
    scope.stop();
  });

  it('pages the new sort from offset 0 onward, never resuming the old offset', async () => {
    queue = [page(CONVERSATIONS_PAGE_SIZE), page(CONVERSATIONS_PAGE_SIZE, CONVERSATIONS_PAGE_SIZE)];
    const { api, scope } = harness();
    await api.loadConversations();
    await api.loadMoreConversations();

    queue = [page(CONVERSATIONS_PAGE_SIZE, 100), page(1, 200)];
    await api.setSortMode('created');
    await api.loadMoreConversations();

    expect(calls[3]).toBe('/api/conversations?limit=30&offset=30&sort=created');
    scope.stop();
  });

  it('lastUsed: touching an existing conversation moves it to the top', async () => {
    queue = [page(5)];
    const { api, scope } = harness();
    await api.loadConversations();
    expect(api.sortMode.value).toBe('lastUsed');

    api.addOrUpdateConversation({ ...summary('c3', 5000), title: 'Touched' });

    expect(ids(api.conversations.value)).toEqual(['c3', 'c0', 'c1', 'c2', 'c4']);
    expect(api.conversations.value[0].title).toBe('Touched');
    scope.stop();
  });

  it('created: touching an existing conversation updates it in place without reordering', async () => {
    localStorage.setItem(SORT_MODE_STORAGE_KEY, 'created');
    queue = [page(5)];
    const { api, scope } = harness();
    await api.loadConversations();
    expect(api.sortMode.value).toBe('created');

    api.addOrUpdateConversation({ ...summary('c3', 5000), title: 'Touched' });

    expect(ids(api.conversations.value)).toEqual(['c0', 'c1', 'c2', 'c3', 'c4']);
    expect(api.conversations.value[3].title).toBe('Touched');
    scope.stop();
  });

  it('adds a brand-new conversation at the top in either sort mode', async () => {
    localStorage.setItem(SORT_MODE_STORAGE_KEY, 'created');
    queue = [page(3)];
    const { api, scope } = harness();
    await api.loadConversations();

    api.addOrUpdateConversation(summary('brand-new', 9999));

    expect(ids(api.conversations.value)[0]).toBe('brand-new');
    scope.stop();
  });

  it('persists the chosen sort mode and restores it on the next composable', async () => {
    queue = [page(1)];
    const { api, scope } = harness();
    await api.setSortMode('created');
    expect(localStorage.getItem(SORT_MODE_STORAGE_KEY)).toBe('created');
    scope.stop();

    const restored = harness();
    expect(restored.api.sortMode.value).toBe('created');
    restored.scope.stop();
  });

  it('falls back to lastUsed for an unrecognized stored value', () => {
    localStorage.setItem(SORT_MODE_STORAGE_KEY, 'not-a-sort-mode');
    const { api, scope } = harness();
    expect(api.sortMode.value).toBe('lastUsed');
    scope.stop();
  });

  it('survives a localStorage that throws on read and on write', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage disabled');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage disabled');
    });

    queue = [page(1), page(1, 50)];
    const { api, scope } = harness();
    expect(api.sortMode.value).toBe('lastUsed');

    await api.loadConversations();
    await api.setSortMode('created');

    expect(api.sortMode.value).toBe('created');
    expect(calls[1]).toBe('/api/conversations?limit=30&offset=0&sort=created');
    scope.stop();
  });

  it('is a no-op when the sort mode is unchanged', async () => {
    queue = [page(2)];
    const { api, scope } = harness();
    await api.loadConversations();

    await api.setSortMode('lastUsed');

    expect(calls).toHaveLength(1);
    scope.stop();
  });
});

describe('useConversations — reset during an in-flight page', () => {
  /**
   * A sort switch while `loadMoreConversations` is parked. The stale page must be rejected AND the
   * spinner must come down.
   *
   * `isLoadingMore` is released in the load-more `finally` only when the request is still the
   * current generation. A reset supersedes that generation, so the stale request declines to lower
   * the flag — correctly, it no longer owns it — and unless the reset lowers it instead, nothing
   * ever does. The failure is silent and permanent: every request has completed, no error is shown,
   * and the sidebar reads "Loading more…" until the page is reloaded.
   */
  it('leaves isLoadingMore false when a sort switch supersedes a parked load-more', async () => {
    queue = [page(CONVERSATIONS_PAGE_SIZE)];
    const { api, scope } = harness();
    await api.loadConversations();
    expect(api.hasMoreConversations.value).toBe(true);

    manual = true;
    const stalePage = api.loadMoreConversations();
    expect(api.isLoadingMore.value).toBe(true);

    // The switch bumps the generation while the load-more sits parked.
    const switched = api.setSortMode('created');
    await releaseParked(page(CONVERSATIONS_PAGE_SIZE, CONVERSATIONS_PAGE_SIZE)); // the stale page
    await releaseParked([summary('created-1', 5)]); // the reload the switch kicked off
    await Promise.all([stalePage, switched]);

    expect(api.isLoadingMore.value).toBe(false);
    // The stale page's rows must not have been appended to the newly-sorted list.
    expect(ids(api.conversations.value)).toEqual(['created-1']);
    scope.stop();
  });
});

describe('useConversations — failures', () => {
  it('surfaces a failed page load without wedging paging', async () => {
    globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
      calls.push(String(input));
      return new Response('nope', { status: 500, statusText: 'Server Error' });
    }) as unknown as typeof globalThis.fetch;
    const { api, scope } = harness();

    await api.loadConversations();

    expect(api.error.value).toContain('Failed to fetch conversations');
    expect(api.isLoading.value).toBe(false);
    // Still retryable — a transient failure must not permanently exhaust the list.
    expect(api.hasMoreConversations.value).toBe(true);
    scope.stop();
  });
});

/**
 * A provisioning response body. Separate from `jsonResponse` above because that one builds the
 * bare ROW ARRAY the listing endpoint returns, while provisioning answers a single object.
 */
function provisionResponse(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const provisionBinding = { workspaceId: 'ws-1', providerId: 'anthropic', modeId: 'default' };

/**
 * #435. Under `Identity:Enforce=true` the WebSocket gate refuses a thread id with no metadata row,
 * byte-identically to one owned by somebody else, and deliberately does NOT mint a row for it - so
 * a client that invents its own id can never open a socket. The id must come from the server.
 */
describe('useConversations provisioning (#435)', () => {
  it('takes the thread id from POST /api/conversations rather than minting one locally', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockResolvedValueOnce(provisionResponse({ threadId: 'thread-server-minted' }));
    const { createNewConversation, currentThreadId } = useConversations();

    const threadId = await createNewConversation(provisionBinding);

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(provisionBinding),
    });
    // The SERVER's id, verbatim - not a locally generated one that happens to look similar.
    expect(threadId).toBe('thread-server-minted');
    expect(currentThreadId.value).toBe('thread-server-minted');
  });

  it('surfaces a provisioning failure instead of falling back to a locally minted id', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockResolvedValueOnce(
      provisionResponse(
        { error: 'provider_unavailable', code: 'provider_unavailable', providerId: 'anthropic' },
        503
      )
    );
    const { createNewConversation, currentThreadId } = useConversations();

    // A fallback id would open a socket the gate then refuses - a conversation that looks started
    // and cannot stream is worse than one that visibly failed to start.
    await expect(createNewConversation(provisionBinding)).rejects.toThrow(/provider_unavailable/);
    expect(currentThreadId.value).toBeNull();
  });
});
