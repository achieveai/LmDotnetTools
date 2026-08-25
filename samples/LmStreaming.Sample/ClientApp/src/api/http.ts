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
 * `fetch` for `/api/*` calls, carrying the signed-in user's access token when there is one.
 *
 * When there is no token this forwards to `fetch` with the caller's arguments UNCHANGED, including
 * their count. That is deliberate and load-bearing: a signed-out call must be indistinguishable
 * from the direct `fetch` it replaced, so that the existing suite — which asserts on exact `fetch`
 * arguments — keeps passing without a single test being edited. Calling `fetch(input, undefined)`
 * instead of `fetch(input)` would break those assertions on arity alone.
 */
export async function apiFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const token = accessToken;
  const response =
    token === null
      ? await (init === undefined ? fetch(input) : fetch(input, init))
      : await fetch(input, withBearer(init, token));

  notifyIfRefused(response);
  return response;
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
