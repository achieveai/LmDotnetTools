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

/**
 * `loadProviders()` resolving is not the same as "the selection is settled" — the catalog is
 * fetched on mount while the composer is already interactive, so whoever needs a provider id has to
 * be able to wait for the load that will actually apply its response.
 */
describe('useProviders.settleCatalog', () => {
  afterEach(() => vi.restoreAllMocks());

  it('does not resolve until an in-flight load has applied its selection', async () => {
    let land!: (body: unknown) => void;
    vi.spyOn(globalThis, 'fetch').mockReturnValueOnce(
      new Promise<Response>((resolve) => {
        land = (body) =>
          resolve({ ok: true, status: 200, statusText: 'OK', json: async () => body } as Response);
      })
    );

    const p = useProviders();
    void p.loadProviders();

    let settled = false;
    const waiting = p.settleCatalog().then((ok) => {
      settled = true;
      return ok;
    });

    await Promise.resolve();
    expect(settled).toBe(false);
    expect(p.selectedProviderId.value).toBeNull();

    land({ providers: [{ id: 'test', displayName: 'Test', available: true }], default: 'test' });

    await expect(waiting).resolves.toBe(true);
    expect(p.selectedProviderId.value).toBe('test');
  });

  it('resolves immediately when no load has ever started', async () => {
    const p = useProviders();
    await expect(p.settleCatalog()).resolves.toBe(true);
  });

  it('ignores a superseded load so the newest response wins the selection', async () => {
    let landFirst!: (body: unknown) => void;
    const first = new Promise<Response>((resolve) => {
      landFirst = (body) =>
        resolve({ ok: true, status: 200, statusText: 'OK', json: async () => body } as Response);
    });
    vi.spyOn(globalThis, 'fetch')
      .mockReturnValueOnce(first)
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: async () => ({
          providers: [{ id: 'anthropic', displayName: 'Anthropic', available: true }],
          default: 'anthropic',
        }),
      } as Response);

    const p = useProviders();
    void p.loadProviders();
    void p.loadProviders();

    // The stale response lands LAST; without the generation guard it would overwrite the newer one.
    await new Promise((resolve) => setTimeout(resolve, 0));
    landFirst({ providers: [{ id: 'test', displayName: 'Test', available: true }], default: 'test' });

    await expect(p.settleCatalog()).resolves.toBe(true);
    expect(p.selectedProviderId.value).toBe('anthropic');
  });
});
