import { describe, it, expect, vi, afterEach } from 'vitest';
import { listEgressKeys, upsertEgressKey, deleteEgressKey } from '@/api/egressAuthApi';
import type { EgressKeyRequest, EgressKeyView } from '@/types/egressAuth';

function mockFetchOnce(ok: boolean, body: unknown, status = ok ? 200 : 400) {
  return vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce({
    ok,
    status,
    statusText: ok ? 'OK' : 'Bad Request',
    json: async () => body,
  } as Response);
}

const sampleView: EgressKeyView = {
  id: 'key-1',
  host: 'api.example.com',
  kind: 'custom-headers',
  headerName: 'Authorization',
  headerNames: ['Authorization'],
  hasClientSecret: false,
  hasRefreshToken: false,
  scopes: [],
};

describe('egressAuthApi.listEgressKeys', () => {
  afterEach(() => vi.restoreAllMocks());

  it('GETs the egress-keys endpoint and returns the catalog', async () => {
    const fetchSpy = mockFetchOnce(true, [sampleView]);

    const keys = await listEgressKeys();

    expect(fetchSpy).toHaveBeenCalledWith('/api/auth/egress-keys');
    expect(keys).toEqual([sampleView]);
  });

  it('throws with the status text on a non-ok response', async () => {
    mockFetchOnce(false, {}, 500);
    await expect(listEgressKeys()).rejects.toThrow(/Failed to fetch egress keys/);
  });
});

describe('egressAuthApi.upsertEgressKey', () => {
  afterEach(() => vi.restoreAllMocks());

  it('POSTs the request body and returns the saved view', async () => {
    const fetchSpy = mockFetchOnce(true, sampleView);
    const req: EgressKeyRequest = {
      id: null,
      host: 'api.example.com',
      kind: 'custom-headers',
      headers: [{ name: 'Authorization', value: 'Bearer x' }],
    };

    const saved = await upsertEgressKey(req);

    expect(fetchSpy).toHaveBeenCalledWith('/api/auth/egress-keys', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    });
    expect(saved).toEqual(sampleView);
  });

  it('surfaces the server {error} body on a 400', async () => {
    mockFetchOnce(false, { error: 'host is required' }, 400);

    await expect(
      upsertEgressKey({ id: null, host: '', kind: 'custom-headers' })
    ).rejects.toThrow(/host is required/);
  });

  it('falls back to the status text when no error body is present', async () => {
    mockFetchOnce(false, {}, 404);

    await expect(
      upsertEgressKey({ id: 'missing', host: 'h', kind: 'custom-headers' })
    ).rejects.toThrow(/Failed to save egress key/);
  });
});

describe('egressAuthApi.deleteEgressKey', () => {
  afterEach(() => vi.restoreAllMocks());

  it('DELETEs the id-scoped endpoint (encoded) and resolves on 204', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce({
      ok: true,
      status: 204,
      statusText: 'No Content',
      json: async () => ({}),
    } as Response);

    await expect(deleteEgressKey('key/1')).resolves.toBeUndefined();

    expect(fetchSpy).toHaveBeenCalledWith('/api/auth/egress-keys/key%2F1', {
      method: 'DELETE',
    });
  });

  it('surfaces the server {error} body on a 404', async () => {
    mockFetchOnce(false, { error: 'not found' }, 404);
    await expect(deleteEgressKey('nope')).rejects.toThrow(/not found/);
  });
});
