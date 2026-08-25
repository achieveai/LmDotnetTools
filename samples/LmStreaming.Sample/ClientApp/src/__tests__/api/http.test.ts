import { describe, it, expect, vi, afterEach } from 'vitest';
import {
  apiFetch,
  setAccessToken,
  getAccessToken,
  onApiRefusal,
  onSessionExpired,
  setTokenProvider,
} from '@/api/http';
import { IDENTITY_REFUSAL_HEADER } from '@/types/identity';

function okResponse(): Response {
  return { ok: true, status: 200, statusText: 'OK', json: async () => ({}) } as Response;
}

function unauthorizedResponse(): Response {
  return { ok: false, status: 401, statusText: 'Unauthorized', json: async () => ({}) } as Response;
}

/**
 * A token that looks enough like a JWT for the expiry probe: three dot-separated segments whose
 * middle one is base64url-encoded JSON. Only `exp` matters, so nothing signs anything.
 */
function jwtExpiringIn(seconds: number): string {
  const payload = { exp: Math.floor(Date.now() / 1000) + seconds, sub: 'user-1' };
  const encoded = btoa(JSON.stringify(payload))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
  return `eyJhbGciOiJSUzI1NiJ9.${encoded}.signature`;
}

/** The Authorization header the mock saw on call `index`, or null when it saw none. */
function bearerOnCall(fetchSpy: { mock: { calls: unknown[][] } }, index: number): string | null {
  const init = fetchSpy.mock.calls[index][1] as RequestInit | undefined;
  return new Headers(init?.headers).get('Authorization');
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
  setTokenProvider(null);
  onSessionExpired(null);
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

describe('apiFetch refreshing ahead of expiry', () => {
  it('re-acquires before issuing when the held token is inside the skew window', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(okResponse());
    const provider = vi.fn().mockResolvedValue('token-fresh');
    setAccessToken(jwtExpiringIn(30));
    setTokenProvider(provider);

    await apiFetch('/api/thing');

    // The request must carry the NEW token, not the one that was about to die. Re-acquiring and
    // then sending the stale value would be indistinguishable from not refreshing at all.
    expect(provider).toHaveBeenCalledOnce();
    expect(fetchSpy).toHaveBeenCalledOnce();
    expect(bearerOnCall(fetchSpy, 0)).toBe('Bearer token-fresh');
    expect(getAccessToken()).toBe('token-fresh');
  });

  it('re-acquires when the held token has already expired', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(okResponse());
    const provider = vi.fn().mockResolvedValue('token-fresh');
    setAccessToken(jwtExpiringIn(-600));
    setTokenProvider(provider);

    await apiFetch('/api/thing');

    expect(provider).toHaveBeenCalledOnce();
    expect(bearerOnCall(fetchSpy, 0)).toBe('Bearer token-fresh');
  });

  it('leaves a token with plenty of life alone', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(okResponse());
    const provider = vi.fn().mockResolvedValue('token-fresh');
    const live = jwtExpiringIn(3600);
    setAccessToken(live);
    setTokenProvider(provider);

    await apiFetch('/api/thing');

    // acquireTokenSilent is cached and cheap, but "cheap" is not "free" and it is not synchronous.
    // Calling it on every request would put an awaited round-trip in front of every API call.
    expect(provider).not.toHaveBeenCalled();
    expect(bearerOnCall(fetchSpy, 0)).toBe('Bearer ' + live);
  });

  it('sends an opaque token as-is rather than throwing on it', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(okResponse());
    const provider = vi.fn().mockResolvedValue('token-fresh');
    setAccessToken('not-a-jwt-at-all');
    setTokenProvider(provider);

    await apiFetch('/api/thing');

    // The server, not the client, is the authority on whether a token is good. A token this
    // client cannot parse has UNKNOWN expiry, and the 401 path below is what catches it. Throwing
    // here would break every deployment whose broker hands out an opaque token.
    expect(provider).not.toHaveBeenCalled();
    expect(bearerOnCall(fetchSpy, 0)).toBe('Bearer not-a-jwt-at-all');
  });

  it('sends a JWT-shaped token whose payload carries no exp as-is', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(okResponse());
    const provider = vi.fn().mockResolvedValue('token-fresh');
    setAccessToken('header.eyJzdWIiOiJ1c2VyLTEifQ.signature');
    setTokenProvider(provider);

    await apiFetch('/api/thing');

    expect(provider).not.toHaveBeenCalled();
    expect(fetchSpy).toHaveBeenCalledOnce();
  });
});

describe('apiFetch recovering from a 401', () => {
  it('re-acquires and retries exactly once, carrying the new token', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(unauthorizedResponse())
      .mockResolvedValueOnce(okResponse());
    const provider = vi.fn().mockResolvedValue('token-new');
    const expired = vi.fn();
    setAccessToken(jwtExpiringIn(3600));
    setTokenProvider(provider);
    onSessionExpired(expired);

    const response = await apiFetch('/api/thing');

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    expect(bearerOnCall(fetchSpy, 1)).toBe('Bearer token-new');
    expect(response.status).toBe(200);
    expect(expired).not.toHaveBeenCalled();
  });

  it('does not retry, and reports expiry, when re-acquisition yields nothing', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(unauthorizedResponse());
    const provider = vi.fn().mockResolvedValue(null);
    const expired = vi.fn();
    setAccessToken(jwtExpiringIn(3600));
    setTokenProvider(provider);
    onSessionExpired(expired);

    const response = await apiFetch('/api/thing');

    // A null from the provider means MSAL has already redirected, or there is no account left.
    // Replaying the request with the same dead token would only produce the same 401.
    expect(fetchSpy).toHaveBeenCalledOnce();
    expect(expired).toHaveBeenCalledOnce();
    expect(response.status).toBe(401);
  });

  it('stops after a second 401 rather than retrying again', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(unauthorizedResponse());
    const provider = vi.fn().mockResolvedValue('token-new');
    const expired = vi.fn();
    setAccessToken(jwtExpiringIn(3600));
    setTokenProvider(provider);
    onSessionExpired(expired);

    const response = await apiFetch('/api/thing');

    // Exactly two calls. A retry loop keyed on 401 is how a client turns one expired session into
    // a sustained stream of doomed requests at an endpoint that will keep saying no.
    expect(fetchSpy).toHaveBeenCalledTimes(2);
    expect(provider).toHaveBeenCalledOnce();
    expect(expired).toHaveBeenCalledOnce();
    expect(response.status).toBe(401);
  });

  it('shares one re-acquisition across requests that all 401 together', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(unauthorizedResponse())
      .mockResolvedValueOnce(unauthorizedResponse())
      .mockResolvedValueOnce(unauthorizedResponse())
      .mockResolvedValue(okResponse());
    let release: ((token: string) => void) | null = null;
    const provider = vi.fn(
      () =>
        new Promise<string>((resolve) => {
          release = resolve;
        }),
    );
    setAccessToken(jwtExpiringIn(3600));
    setTokenProvider(provider);
    onSessionExpired(vi.fn());

    const all = Promise.all([apiFetch('/api/one'), apiFetch('/api/two'), apiFetch('/api/three')]);
    await vi.waitFor(() => expect(release).not.toBeNull());
    release!('token-new');
    await all;

    // A chat client fires several requests per interaction. Without de-duplication, a token that
    // expires between them produces one broker round-trip per in-flight request - and, with an
    // interactive fallback behind it, several competing redirects.
    expect(provider).toHaveBeenCalledOnce();
    expect(fetchSpy).toHaveBeenCalledTimes(6);
    expect(bearerOnCall(fetchSpy, 3)).toBe('Bearer token-new');
    expect(bearerOnCall(fetchSpy, 4)).toBe('Bearer token-new');
    expect(bearerOnCall(fetchSpy, 5)).toBe('Bearer token-new');
  });

  it('does not retry a request whose body cannot be sent twice', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(unauthorizedResponse());
    const provider = vi.fn().mockResolvedValue('token-new');
    const expired = vi.fn();
    setAccessToken(jwtExpiringIn(3600));
    setTokenProvider(provider);
    onSessionExpired(expired);

    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode('{}'));
        controller.close();
      },
    });
    const response = await apiFetch('/api/thing', {
      method: 'POST',
      body,
      duplex: 'half',
    } as RequestInit);

    // The first attempt consumed the stream. Re-issuing would send an empty body, so the retry
    // would not be a retry of the caller's request at all - it would be a different request that
    // happens to share a URL.
    expect(fetchSpy).toHaveBeenCalledOnce();
    expect(provider).not.toHaveBeenCalled();
    expect(expired).toHaveBeenCalledOnce();
    expect(response.status).toBe(401);
  });

  it('leaves a 401 alone when nothing can re-acquire a token', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(unauthorizedResponse());
    setAccessToken('token-abc');

    const response = await apiFetch('/api/thing');

    // No provider is the signed-out deployment - the state every developer machine runs in. A 401
    // there is an ordinary answer to forward, not a session to expire.
    expect(fetchSpy).toHaveBeenCalledOnce();
    expect(response.status).toBe(401);
  });

  it('routes a tenant refusal to the refusal handler and not down the 401 path', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(refusalResponse('tenant_suspended'));
    const provider = vi.fn().mockResolvedValue('token-new');
    const expired = vi.fn();
    const seen: string[] = [];
    setAccessToken(jwtExpiringIn(3600));
    setTokenProvider(provider);
    onSessionExpired(expired);
    onApiRefusal((code) => seen.push(code));

    await apiFetch('/api/thing');

    // A 403 and a 401 have opposite remedies. Re-acquiring a token for a suspended tenant gets a
    // brand new token that is refused in exactly the same way.
    expect(seen).toEqual(['tenant_suspended']);
    expect(provider).not.toHaveBeenCalled();
    expect(expired).not.toHaveBeenCalled();
    expect(fetchSpy).toHaveBeenCalledOnce();
  });

  it('still forwards a signed-out call with the caller arguments unchanged', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(okResponse());
    setTokenProvider(vi.fn().mockResolvedValue('token-new'));

    await apiFetch('/api/thing');

    // Registering a provider must not change the shape of a call made without a token. The ~70
    // existing API tests assert on exact `fetch` arity, and they run with no token at all.
    expect(fetchSpy).toHaveBeenCalledWith('/api/thing');
    expect(fetchSpy.mock.calls[0]).toHaveLength(1);
  });
});
