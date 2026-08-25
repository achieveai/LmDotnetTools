import { describe, it, expect, vi, afterEach } from 'vitest';
import { apiFetch, setAccessToken, getAccessToken, onApiRefusal } from '@/api/http';
import { IDENTITY_REFUSAL_HEADER } from '@/types/identity';

function okResponse(): Response {
  return { ok: true, status: 200, statusText: 'OK', json: async () => ({}) } as Response;
}

function refusalResponse(code: string | null, status = 403): Response {
  return {
    ok: false,
    status,
    statusText: 'Forbidden',
    headers: new Headers(code === null ? {} : { [IDENTITY_REFUSAL_HEADER]: code }),
    json: async () => ({}),
  } as Response;
}

afterEach(() => {
  vi.restoreAllMocks();
  setAccessToken(null);
  onApiRefusal(null);
});

describe('apiFetch argument forwarding', () => {
  it('calls fetch with the URL alone when signed out and no init was given', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(okResponse());

    await apiFetch('/api/thing');

    // Arity matters, not just the URL. The existing suite asserts `toHaveBeenCalledWith(url)` on
    // ~10 API modules; passing an explicit `undefined` second argument fails those assertions even
    // though the request would be identical. Routing every call through this helper is only free
    // because a signed-out call is INDISTINGUISHABLE from the direct fetch it replaced.
    expect(fetchSpy).toHaveBeenCalledWith('/api/thing');
    expect(fetchSpy.mock.calls[0]).toHaveLength(1);
  });

  it('passes the caller init through untouched when signed out', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(okResponse());
    const init = { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' };

    await apiFetch('/api/thing', init);

    expect(fetchSpy).toHaveBeenCalledWith('/api/thing', init);
    expect(fetchSpy.mock.calls[0][1]).toBe(init);
  });

  it('adds the bearer header once a token is set', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(okResponse());
    setAccessToken('token-abc');

    await apiFetch('/api/thing');

    const sent = new Headers(fetchSpy.mock.calls[0][1]!.headers);
    expect(sent.get('Authorization')).toBe('Bearer token-abc');
  });

  it('keeps the caller headers when adding the bearer header', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(okResponse());
    setAccessToken('token-abc');
    const init = { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' };

    await apiFetch('/api/thing', init);

    const sent = new Headers(fetchSpy.mock.calls[0][1]!.headers);
    expect(sent.get('Authorization')).toBe('Bearer token-abc');
    expect(sent.get('Content-Type')).toBe('application/json');

    // The caller's object is theirs; a helper that mutated it would leak the header into whatever
    // else that object is reused for.
    expect(init.headers).toEqual({ 'Content-Type': 'application/json' });
  });

  it('stops sending a header once the token is cleared', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(okResponse());
    setAccessToken('token-abc');
    setAccessToken(null);

    await apiFetch('/api/thing');

    expect(getAccessToken()).toBeNull();
    expect(fetchSpy.mock.calls[0]).toHaveLength(1);
  });
});

describe('apiFetch refusal reporting', () => {
  it('reports a tenant refusal to the registered handler', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusalResponse('tenant_not_provisioned'));
    const seen: string[] = [];
    onApiRefusal((code) => seen.push(code));

    await apiFetch('/api/thing');

    expect(seen).toEqual(['tenant_not_provisioned']);
  });

  it('reports a suspension', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusalResponse('tenant_suspended'));
    const seen: string[] = [];
    onApiRefusal((code) => seen.push(code));

    await apiFetch('/api/thing');

    expect(seen).toEqual(['tenant_suspended']);
  });

  it('ignores a 403 that is not a whole-organisation refusal', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusalResponse(null));
    const seen: string[] = [];
    onApiRefusal((code) => seen.push(code));

    await apiFetch('/api/thing');

    // A per-resource 403 is an ordinary authorization answer. Treating it as a tenant refusal would
    // black out the whole app because one document was not shared with this user.
    expect(seen).toEqual([]);
  });

  it('ignores an unrecognised refusal code rather than rendering an unknown screen', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusalResponse('something_new'));
    const seen: string[] = [];
    onApiRefusal((code) => seen.push(code));

    await apiFetch('/api/thing');

    expect(seen).toEqual([]);
  });

  it('returns the response body unread, so the caller can still consume it', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusalResponse('tenant_suspended'));
    onApiRefusal(() => {});

    const response = await apiFetch('/api/thing');

    // Classifying the refusal from a header rather than the body is what makes this true. Reading
    // the body here would hand the caller an already-consumed stream.
    await expect(response.json()).resolves.toEqual({});
  });

  it('survives a Response mock that carries no headers at all', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce({ ok: false, status: 403 } as Response);
    onApiRefusal(() => {});

    // Existing tests build Response literals with only the fields they care about. Throwing on one
    // would make this an identity change that breaks tests about something else entirely.
    await expect(apiFetch('/api/thing')).resolves.toBeDefined();
  });
});
