import { describe, it, expect, vi, afterEach } from 'vitest';
import { useProviders } from '@/composables/useProviders';

function mockFetchOnce(ok: boolean, body: unknown, status = ok ? 200 : 409) {
  return vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce({
    ok,
    status,
    statusText: ok ? 'OK' : 'Conflict',
    json: async () => body,
  } as Response);
}

describe('useProviders.switchProvider', () => {
  afterEach(() => vi.restoreAllMocks());

  it('POSTs the provider endpoint and reflects the new provider on success', async () => {
    const fetchSpy = mockFetchOnce(true, { providerId: 'openai' });
    const p = useProviders();

    await p.switchProvider('thread-1', 'openai');

    expect(fetchSpy).toHaveBeenCalledWith(
      '/api/conversations/thread-1/provider',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ providerId: 'openai' }),
      })
    );
    expect(p.selectedProviderId.value).toBe('openai');
    expect(p.error.value).toBeNull();
  });

  it('re-throws and sets error without changing the selection on failure (409 while streaming)', async () => {
    mockFetchOnce(false, { error: 'Cannot switch provider while response is streaming.' }, 409);
    const p = useProviders();
    p.selectedProviderId.value = 'test';

    await expect(p.switchProvider('thread-1', 'openai')).rejects.toThrow(/streaming/);

    expect(p.selectedProviderId.value).toBe('test'); // selection unchanged on failure
    expect(p.error.value).toMatch(/streaming/);
  });
});
