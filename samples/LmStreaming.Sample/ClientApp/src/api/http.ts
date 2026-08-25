/**
 * The single place an `Authorization` header is attached to an API call.
 *
 * Every `/api/*` request in the client goes through {@link apiFetch}. The alternative — each API
 * module reaching for the token itself — means the next module someone adds is unauthenticated
 * until a reviewer happens to notice, which is exactly the class of hole P1 exists to close.
 *
 * The token is held here as a plain module variable rather than pulled from MSAL directly, so this
 * module has no dependency on `@azure/msal-browser`. That matters for more than tidiness: the API
 * modules are imported by ~70 existing test files, and making them transitively pull in a browser
 * auth library would make every one of those tests carry it too.
 */

import { asRefusalCode, IDENTITY_REFUSAL_HEADER, type IdentityRefusalCode } from '@/types/identity';

/**
 * The access token to present, or null when the client is not signed in — which is the normal
 * state whenever `Identity:Enforce` is false or no Entra app registration is configured.
 */
let accessToken: string | null = null;

/** Notified whenever a response refuses the whole organisation rather than this one request. */
let refusalHandler: ((code: IdentityRefusalCode) => void) | null = null;

/**
 * Produces a fresh access token, or null when it cannot without interaction.
 *
 * Injected rather than imported for the same reason the token itself is: the implementation is
 * MSAL, and this module must stay free of `@azure/msal-browser`. Null means "no broker" — the
 * signed-out deployment every developer machine and every existing test runs in — and every
 * renewal path below is inert in that state.
 */
let tokenProvider: (() => Promise<string | null>) | null = null;

/** Notified when the session is over and only an interactive sign-in can bring it back. */
let sessionExpiredHandler: (() => void) | null = null;

/**
 * The renewal currently under way, shared by every caller that wants one.
 *
 * A chat client issues several requests per interaction. Without this, a token that lapses between
 * them sends one broker round-trip per in-flight request — and, because the broker falls back to a
 * full-page redirect when silent renewal fails, several competing navigations.
 */
let renewal: Promise<string | null> | null = null;

/**
 * How close to its own expiry a token has to be before we stop presenting it.
 *
 * Sized for clock skew between this browser and the resource server, not for the round-trip: a
 * token the server considers dead while this clock says it has ten seconds left produces a 401
 * that the retry below has to clean up. `acquireTokenSilent` serves from MSAL's cache, so paying
 * it a minute early costs almost nothing.
 */
const EXPIRY_SKEW_SECONDS = 60;

/** Replaces the token every subsequent call presents. Null clears it (sign-out, or expiry). */
export function setAccessToken(token: string | null): void {
  accessToken = token;
}

/** The token currently being presented, for diagnostics and for the transports that cannot use a header. */
export function getAccessToken(): string | null {
  return accessToken;
}

/**
 * Adds the bearer header to an existing init without mutating the caller's object.
 *
 * Uses `Headers` rather than object spread because `init.headers` may legitimately be a `Headers`
 * instance, a plain object, or an array of pairs; spreading a `Headers` yields an empty object and
 * would silently drop the caller's `Content-Type`.
 */
function withBearer(init: RequestInit | undefined, token: string): RequestInit {
  const headers = new Headers(init?.headers);
  headers.set('Authorization', `Bearer ${token}`);
  return { ...init, headers };
}

/**
 * One trip to `fetch`, carrying the signed-in user's access token when there is one.
 *
 * Factored out of {@link apiFetch} so that the retry re-reads {@link accessToken} rather than
 * closing over the value the first attempt used — a retry that presents the dead token again is
 * not a retry.
 *
 * When there is no token this forwards to `fetch` with the caller's arguments UNCHANGED, including
 * their count. That is deliberate and load-bearing: a signed-out call must be indistinguishable
 * from the direct `fetch` it replaced, so that the existing suite — which asserts on exact `fetch`
 * arguments — keeps passing without a single test being edited. Calling `fetch(input, undefined)`
 * instead of `fetch(input)` would break those assertions on arity alone.
 */
function issue(input: RequestInfo | URL, init: RequestInit | undefined): Promise<Response> {
  const token = accessToken;
  return token === null
    ? init === undefined
      ? fetch(input)
      : fetch(input, init)
    : fetch(input, withBearer(init, token));
}

/**
 * Every `/api/*` call, with the access token kept alive underneath it.
 *
 * An Entra access token lives about an hour; a chat client is exactly the kind of app people leave
 * open all day. Two mechanisms, because neither alone is enough. Refreshing ahead of `exp` misses
 * a token revoked early or a clock that disagrees with the server's; recovering from a 401 only
 * acts after a request has already failed, and cannot act at all on one that is not replayable.
 */
export async function apiFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  // Guarded rather than awaited unconditionally, and the guard is deliberately synchronous. An
  // `await` taken on every call would push the `fetch` one microtask later than the caller expects,
  // which is enough to reorder an abort against the request it is meant to cancel — several
  // existing tests turn exactly that ordering into an assertion.
  if (tokenProvider !== null && accessToken !== null && isNearExpiry(accessToken)) {
    await renewToken();
  }

  const first = await issue(input, init);

  // The fast path, and the only one a deployment without a broker ever takes. Everything below is
  // unreachable while `tokenProvider` is null, which is what keeps a signed-out call identical to
  // the direct `fetch` it replaced.
  if (first?.status !== 401 || tokenProvider === null) {
    notifyIfRefused(first);
    return first;
  }

  // The first attempt already drained a streaming body. Re-issuing would send an empty one, so the
  // "retry" would not be the caller's request at all — it would be a different request that
  // happens to share a URL. Better a visible expiry than a silently truncated write.
  if (!isReplayable(input, init)) {
    expireSession();
    return first;
  }

  const renewed = await renewToken();
  if (renewed === null) {
    // Null means the broker has already redirected for interaction, or there is no account left to
    // renew against. Replaying with the same dead token would only earn the same 401.
    expireSession();
    return first;
  }

  // Exactly one retry, and no loop: a second 401 is the server saying the problem was never the
  // token's age. Retrying past that is how one expired session becomes a sustained stream of
  // doomed requests.
  const second = await issue(input, init);
  if (second?.status === 401) {
    expireSession();
  }

  notifyIfRefused(second);
  return second;
}

/**
 * Asks the provider for a token, joining any renewal already in flight rather than starting a
 * second one, and publishes whatever comes back.
 *
 * A provider that rejects is treated as "no token": a renewal failure must not escape into the
 * caller's `apiFetch`, where it would surface as a network error on whatever request happened to
 * trigger it rather than as the expired session it actually is.
 */
function renewToken(): Promise<string | null> {
  if (tokenProvider === null) {
    return Promise.resolve(null);
  }

  if (renewal !== null) {
    return renewal;
  }

  const provider = tokenProvider;
  const attempt = Promise.resolve()
    .then(() => provider())
    .catch(() => null)
    .then((token) => {
      setAccessToken(token);
      return token;
    })
    .finally(() => {
      renewal = null;
    });

  renewal = attempt;
  return attempt;
}

/** Drops the dead token and tells the shell the session is over. */
function expireSession(): void {
  // Cleared here rather than in the handler because the handler is optional: a client that never
  // registered one must still stop presenting a token the server has already rejected twice.
  setAccessToken(null);
  sessionExpiredHandler?.();
}

/**
 * Whether this request can be sent a second time byte-for-byte.
 *
 * A `ReadableStream` body is consumed by the first attempt and a `Request` object carries its body
 * the same way. `duplex` is checked as well because it is only ever set for a streaming body, and
 * it is the one signal present even where `ReadableStream` is not a global.
 */
function isReplayable(input: RequestInfo | URL, init: RequestInit | undefined): boolean {
  if (typeof Request !== 'undefined' && input instanceof Request && input.body !== null) {
    return false;
  }

  if (init === undefined || init.body === undefined || init.body === null) {
    return true;
  }

  if ((init as { duplex?: unknown }).duplex !== undefined) {
    return false;
  }

  return !(typeof ReadableStream !== 'undefined' && init.body instanceof ReadableStream);
}

/**
 * Whether the token says it is within {@link EXPIRY_SKEW_SECONDS} of expiring.
 *
 * Total by construction. A token this client cannot parse — an opaque one, a malformed one, a JWT
 * with no `exp` — has UNKNOWN expiry, and unknown means "use it and let the server decide". The
 * 401 path is what catches those. Throwing here would break every deployment whose broker hands
 * out something that is not a JWT, over a check that is only ever an optimisation.
 */
function isNearExpiry(token: string): boolean {
  const segments = token.split('.');
  if (segments.length !== 3) {
    return false;
  }

  try {
    const payload: unknown = JSON.parse(decodeBase64Url(segments[1]));
    const exp = (payload as { exp?: unknown } | null)?.exp;
    if (typeof exp !== 'number' || !Number.isFinite(exp)) {
      return false;
    }

    return exp * 1000 - Date.now() <= EXPIRY_SKEW_SECONDS * 1000;
  } catch {
    return false;
  }
}

/**
 * Decodes one base64url segment to a string. Hand-rolled rather than pulled from a JWT library:
 * reading a number out of a payload the server has already validated does not justify a dependency
 * in the module every API call goes through.
 */
function decodeBase64Url(segment: string): string {
  const base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  const binary = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='));
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

/**
 * Reports a whole-organisation refusal to whoever registered for it.
 *
 * Reads the header, never the body. The body belongs to the caller that made this request, and
 * consuming it here would hand that caller an already-read stream — a bug that would only show up
 * on the one code path nobody exercises until a real tenant is suspended.
 *
 * Every access is optional-chained because a `Response` in a unit test is routinely a plain object
 * literal with only the fields that test cares about. Throwing on such a mock would turn this into
 * a change that breaks tests unrelated to identity.
 */
function notifyIfRefused(response: Response): void {
  if (response?.status !== 403 || refusalHandler === null) {
    return;
  }

  const code = asRefusalCode(response.headers?.get?.(IDENTITY_REFUSAL_HEADER));
  if (code !== null) {
    refusalHandler(code);
  }
}

/**
 * Registers the callback that reacts to a whole-organisation refusal, replacing any previous one.
 *
 * This lives at the fetch layer rather than being probed once at start-up because a tenant can be
 * suspended while a session is already open. A one-time probe would leave that user staring at an
 * app whose every request silently fails.
 */
export function onApiRefusal(handler: ((code: IdentityRefusalCode) => void) | null): void {
  refusalHandler = handler;
}

/**
 * Registers what this module calls to obtain a fresh token, replacing any previous provider.
 *
 * The provider is supplied by the identity composable, which already owns MSAL. Inverting it this
 * way is what keeps `@azure/msal-browser` out of the module that every API call — and therefore
 * every API test — imports.
 *
 * Null unregisters, which is also how the test suite stops one file's provider from being called
 * with the next file's mocks.
 */
export function setTokenProvider(provider: (() => Promise<string | null>) | null): void {
  tokenProvider = provider;

  // A renewal started by the outgoing provider must not be handed to callers of the new one; the
  // token it resolves with came from a broker this client is no longer using.
  renewal = null;
}

/**
 * Registers the callback that reacts to a session that can no longer be renewed silently.
 *
 * Separate from {@link onApiRefusal} because the two mean opposite things to the user. A refusal
 * says the organisation may not be here and signing in again changes nothing; an expiry says the
 * only thing wrong is the clock, and signing in again is exactly the fix.
 */
export function onSessionExpired(handler: (() => void) | null): void {
  sessionExpiredHandler = handler;
}
