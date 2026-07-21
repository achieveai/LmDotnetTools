import { describe, it, expect, afterEach, vi } from 'vitest';
import { getConversationUsage } from '@/api/conversationsApi';

function mockFetchOnce(status: number, body: unknown) {
  const original = globalThis.fetch;
  const fetchSpy = vi.fn(
    async () =>
      new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' },
      })
  );
  globalThis.fetch = fetchSpy as unknown as typeof fetch;
  return { fetchSpy, restore: () => (globalThis.fetch = original) };
}

// #196: the persisted usage aggregate is fetched on reload to restore the usage banner (including
// sub-agent/workflow usage) instead of resetting to zero.
describe('conversationsApi.getConversationUsage (#196)', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('returns the persisted aggregate on success', async () => {
    const mock = mockFetchOnce(200, {
      rootConversationId: 'thread-1',
      totalTokens: 140,
      completeness: 'Complete',
      perModel: [
        { modelId: 'model-A', inputTokens: 100, outputTokens: 40, cacheReadTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0, totalTokens: 140, attemptCount: 1 },
      ],
      currency: 'USD',
    });
    restore = mock.restore;

    const result = await getConversationUsage('thread-1');

    expect(result?.totalTokens).toBe(140);
    expect(result?.perModel[0].modelId).toBe('model-A');
    expect(mock.fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/usage');
  });

  it('returns null when no usage has been recorded (404)', async () => {
    const mock = mockFetchOnce(404, {});
    restore = mock.restore;

    const result = await getConversationUsage('thread-1');

    expect(result).toBeNull();
  });
});
