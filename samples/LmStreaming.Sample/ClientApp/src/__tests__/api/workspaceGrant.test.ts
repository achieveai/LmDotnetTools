import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import {
  clearAllWorkspaceGrants,
  clearWorkspaceGrant,
  requestWorkspaceGrant,
  workspaceFileUrl,
  FileBrowserError,
  NoSessionError,
} from '@/api/fileBrowserApi';
import { jsonResponse } from '../fixtures/fileBrowser';

/**
 * The path-addressed raw workspace access added for Bug#15. Two things are load-bearing here and
 * neither is obvious from the call sites: the grant is CACHED (an open iframe fetches a document
 * plus every subresource, and a grant per request would issue a handful of live tokens per page),
 * and the URL is built by encoding each path SEGMENT rather than the whole path (so `/` stays a
 * separator and relative links inside the served document resolve to sibling workspace files).
 */
function grantResponse(grant: string, expiresAt: Date, transport = 'url'): Response {
  return jsonResponse({ grant, expiresAt: expiresAt.toISOString(), transport });
}

function anHourFromNow(): Date {
  return new Date(Date.now() + 60 * 60 * 1000);
}

/**
 * The mint's request, as this client now sends it. The transport is the ONE thing the client
 * decides: the server sits behind an https front that does not forward its scheme, so only the page
 * knows whether a `Secure` cookie would be accepted.
 */
function mintInit(transport: 'cookie' | 'url'): RequestInit {
  return {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ transport }),
    signal: undefined,
  };
}

/**
 * `isSecureContext` is a read-only accessor on the jsdom window, so it is redefined rather than
 * assigned — the same seam `CopyMessageButton.test.ts` uses for the clipboard's secure-context gate.
 */
function setSecureContext(value: boolean): void {
  Object.defineProperty(globalThis, 'isSecureContext', { value, configurable: true });
}

describe('requestWorkspaceGrant', () => {
  beforeEach(() => clearAllWorkspaceGrants());
  afterEach(() => {
    vi.restoreAllMocks();
    clearAllWorkspaceGrants();
  });

  it('mints through the bearer-authenticated POST grant route', async () => {
    setSecureContext(false);
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(grantResponse('g-1', anHourFromNow()));

    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-1');

    expect(fetchSpy).toHaveBeenCalledWith(
      '/api/conversations/thread-1/files/grant',
      mintInit('url')
    );
  });

  /**
   * The point of the cookie transport: on a secure context the credential is asked for as an
   * `HttpOnly` cookie, so the URL the sandboxed document can read carries only a public marker. A
   * `Secure` cookie is accepted exactly where `isSecureContext` is true — https and localhost — so
   * that flag, and not a server-side guess, is what chooses.
   */
  it('asks for the cookie transport on a secure context', async () => {
    setSecureContext(true);
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(grantResponse('cookie', anHourFromNow(), 'cookie'));

    await requestWorkspaceGrant('thread-1');

    expect(fetchSpy).toHaveBeenCalledWith(
      '/api/conversations/thread-1/files/grant',
      mintInit('cookie')
    );
  });

  /**
   * The companion that keeps the case above from passing for the wrong reason. A plain-http
   * deployment on anything but localhost would have its `Secure` cookie dropped by the browser and
   * then address nothing, so it must keep the URL token it always had.
   */
  it('falls back to the URL transport when the context is not secure', async () => {
    setSecureContext(false);
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(grantResponse('g-1', anHourFromNow()));

    await requestWorkspaceGrant('thread-1');

    expect(fetchSpy).toHaveBeenCalledWith(
      '/api/conversations/thread-1/files/grant',
      mintInit('url')
    );
  });

  /**
   * What is cached and put in the URL is the segment the SERVER returned, never a value this module
   * inferred from the transport it asked for. A server that ignored the request still produces
   * working URLs, and a marker is carried exactly as a token is.
   */
  it('caches the segment the server returned, not the transport it asked for', async () => {
    setSecureContext(true);
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      grantResponse('cookie', anHourFromNow(), 'cookie')
    );

    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('cookie');
    expect(workspaceFileUrl('thread-1', await requestWorkspaceGrant('thread-1'), 'report/a.html')).toBe(
      '/api/conversations/thread-1/workspace/cookie/report/a.html'
    );
  });

  /**
   * And the reverse: asking for the cookie does not make this client assume it got one. A server
   * that answered with a URL token is believed, and that token is what goes in the URL.
   */
  it('uses a URL token the server returned even when the cookie transport was asked for', async () => {
    setSecureContext(true);
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(grantResponse('g-real', anHourFromNow(), 'url'));

    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-real');
  });

  it('reuses a cached grant instead of minting a second one', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(grantResponse('g-1', anHourFromNow()));

    await requestWorkspaceGrant('thread-1');
    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-1');

    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('caches per thread, so a second conversation mints its own grant', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(grantResponse('g-1', anHourFromNow()))
      .mockResolvedValueOnce(grantResponse('g-2', anHourFromNow()));

    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-1');
    await expect(requestWorkspaceGrant('thread-2')).resolves.toBe('g-2');

    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  it('mints again once the grant is inside the refresh margin', async () => {
    // Four minutes of life left: inside the five-minute margin, so it is replaced rather than used.
    const nearlyExpired = new Date(Date.now() + 4 * 60 * 1000);
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(grantResponse('g-old', nearlyExpired))
      .mockResolvedValueOnce(grantResponse('g-new', anHourFromNow()));

    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-old');
    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-new');

    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  it('joins a mint already in flight rather than starting a second one', async () => {
    let release: (r: Response) => void = () => {};
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockReturnValue(new Promise<Response>((resolve) => (release = resolve)));

    const first = requestWorkspaceGrant('thread-1');
    const second = requestWorkspaceGrant('thread-1');
    release(grantResponse('g-1', anHourFromNow()));

    await expect(first).resolves.toBe('g-1');
    await expect(second).resolves.toBe('g-1');
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('does not cache a failure, so the next call retries', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ code: 'gateway_error' }, 502))
      .mockResolvedValueOnce(grantResponse('g-1', anHourFromNow()));

    await expect(requestWorkspaceGrant('thread-1')).rejects.toBeInstanceOf(FileBrowserError);
    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-1');

    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  it('surfaces a session-level 409 as the typed NoSessionError', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ code: 'no_session_yet' }, 409));

    await expect(requestWorkspaceGrant('thread-1')).rejects.toBeInstanceOf(NoSessionError);
  });

  it('clearWorkspaceGrant forces the next call to mint a fresh grant', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(grantResponse('g-1', anHourFromNow()))
      .mockResolvedValueOnce(grantResponse('g-2', anHourFromNow()));

    await requestWorkspaceGrant('thread-1');
    clearWorkspaceGrant('thread-1');

    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-2');
    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  it('uses an unparseable expiry once rather than caching it forever', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(jsonResponse({ grant: 'g-1', expiresAt: 'not-a-date' }));

    await expect(requestWorkspaceGrant('thread-1')).resolves.toBe('g-1');
    // Cached with a refresh point in the FUTURE (now + margin), so it is reused rather than
    // re-minted on every single subresource — but it expires from this client's view within the
    // margin instead of never.
    await requestWorkspaceGrant('thread-1');
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });
});

describe('workspaceFileUrl', () => {
  it('addresses the file by PATH so relative links inside it resolve to siblings', () => {
    expect(workspaceFileUrl('thread-1', 'g-1', 'report/index.html')).toBe(
      '/api/conversations/thread-1/workspace/g-1/report/index.html'
    );
  });

  it('encodes each segment individually, keeping "/" a separator', () => {
    expect(workspaceFileUrl('thread-1', 'g-1', 'my docs/a b.html')).toBe(
      '/api/conversations/thread-1/workspace/g-1/my%20docs/a%20b.html'
    );
  });

  it('encodes a "?" or "#" in a file name so it cannot truncate the URL', () => {
    expect(workspaceFileUrl('t', 'g', 'a#b?c.html')).toBe(
      '/api/conversations/t/workspace/g/a%23b%3Fc.html'
    );
  });

  it('encodes the thread id and the grant', () => {
    expect(workspaceFileUrl('a/b', 'g+/=', 'x.txt')).toBe(
      '/api/conversations/a%2Fb/workspace/g%2B%2F%3D/x.txt'
    );
  });

  it('drops empty segments from a path with leading or doubled slashes', () => {
    expect(workspaceFileUrl('t', 'g', '/a//b.html')).toBe('/api/conversations/t/workspace/g/a/b.html');
  });

  it('adds ?download=1 as a QUERY, which a relative subresource link cannot inherit', () => {
    expect(workspaceFileUrl('t', 'g', 'a.bin', { download: true })).toBe(
      '/api/conversations/t/workspace/g/a.bin?download=1'
    );
  });

  it('omits the query when download is not asked for', () => {
    expect(workspaceFileUrl('t', 'g', 'a.bin', {})).toBe('/api/conversations/t/workspace/g/a.bin');
  });
});
