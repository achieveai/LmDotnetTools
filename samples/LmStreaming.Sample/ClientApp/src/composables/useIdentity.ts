import { ref, readonly, computed, type Ref } from 'vue';
import { getIdentityConfig } from '@/api/identityApi';
import { onApiRefusal, onSessionExpired, setAccessToken, setTokenProvider } from '@/api/http';
import * as entraAuth from '@/auth/entraAuth';
import type { IdentityConfig, IdentityRefusalCode, IdentityStatus } from '@/types/identity';

/**
 * Drives the app shell's decision: show the app, ask the user to sign in, or explain a refusal.
 *
 * The whole state machine exists to keep three outcomes apart that a naive client collapses into
 * "not authorised":
 *
 *  - not signed in — signing in fixes it;
 *  - the organisation was never provisioned, or is suspended — signing in CANNOT fix it, and
 *    sending the user back to Entra produces an infinite loop;
 *  - the deployment does not enforce identity at all — nothing to do, show the app.
 */

const status: Ref<IdentityStatus> = ref('loading');
const refusalCode: Ref<IdentityRefusalCode | null> = ref(null);
const errorMessage: Ref<string | null> = ref(null);
const config: Ref<IdentityConfig | null> = ref(null);

/**
 * Records a whole-organisation refusal.
 *
 * Deliberately terminal: once refused, the client stops trying. There is nothing the browser can
 * do about a tenant that was never provisioned, and retrying is how a client ends up hammering an
 * endpoint that will keep saying no.
 */
function refuse(code: IdentityRefusalCode): void {
  refusalCode.value = code;
  status.value = 'rejected';
  setAccessToken(null);

  // The provider goes too. Left registered, the next call would meet a 401 (there is no token any
  // more), renew successfully, and be refused again on the retry — turning a terminal state into a
  // steady trickle of paired requests against an endpoint that will keep saying no.
  setTokenProvider(null);
}

/**
 * Records a session that ran out and could not be renewed without the user present.
 *
 * Deliberately NOT a refusal and deliberately not an automatic redirect. A redirect fired from
 * whichever background request happened to notice would throw away a half-typed message; the user
 * gets a screen, and decides when to leave the page.
 */
function expire(): void {
  status.value = 'expired';
}

/**
 * Reads the deployment's identity config and, if it is enforcing, establishes a token.
 *
 * Safe to call exactly once, at app start. It never throws: a failure becomes the `error` status
 * so the shell can render something, because an unhandled rejection here would leave the user
 * looking at a blank page with the reason only in the console.
 */
export async function initializeIdentity(): Promise<void> {
  onApiRefusal(refuse);
  onSessionExpired(expire);

  try {
    const loaded = await getIdentityConfig();
    config.value = loaded;

    // No app registration means there is no directory to sign in to. This is the state every
    // developer machine and every existing test runs in, and it must show the app, not a gate.
    if (!loaded.enforce || loaded.clientId === null || loaded.authority === null) {
      status.value = 'disabled';
      return;
    }

    const account = await entraAuth.initialize(loaded);
    if (account === null) {
      status.value = 'signing-in';
      await entraAuth.signIn();
      return;
    }

    const token = await entraAuth.acquireAccessToken();
    if (token === null) {
      // acquireAccessToken has redirected for interaction; this page is going away.
      status.value = 'signing-in';
      return;
    }

    // Only now does a broker with an active account exist, so only now can the fetch layer ask it
    // for a token. Registering earlier would point the renewal path at an uninitialised MSAL
    // instance during the anonymous config request above.
    setTokenProvider(() => entraAuth.acquireAccessToken());
    setAccessToken(token);
    status.value = 'signed-in';
  } catch (error) {
    fail(error);
  }
}

/**
 * Moves the state machine to its terminal error state.
 *
 * Every path that can reject has to end up here rather than letting the rejection escape. The
 * status has usually already been advanced to drive a spinner by the time something throws, and
 * an escaping rejection leaves that spinner as the last thing the user ever sees - no reason
 * shown, no way to retry, and no navigation coming to replace it.
 */
function fail(error: unknown): void {
  errorMessage.value = error instanceof Error ? error.message : String(error);
  status.value = 'error';
}

/**
 * Starts an interactive sign-in, for the button on the signed-out screen.
 *
 * Never throws, for the same reason {@link initializeIdentity} does not: this is wired straight
 * to a click handler, so a rejection would be unhandled.
 */
export async function startSignIn(): Promise<void> {
  status.value = 'signing-in';

  try {
    await entraAuth.signIn();
  } catch (error) {
    // signIn resolving means the browser is navigating away and the spinner is correct. It
    // REJECTING means the redirect is not happening, so the spinner would never be replaced.
    fail(error);
  }
}

/** Resets every module-level ref. For tests only — the app initialises once and never tears down. */
export function resetIdentityState(): void {
  status.value = 'loading';
  refusalCode.value = null;
  errorMessage.value = null;
  config.value = null;
  setAccessToken(null);
  onApiRefusal(null);
  onSessionExpired(null);

  // The provider closes over this module's MSAL mock. Leaving it registered lets one test file's
  // broker be called from the next one's `apiFetch`, which is the kind of leak that shows up as an
  // unrelated file failing only when the whole suite runs.
  setTokenProvider(null);
}

/** The app shell's view of identity. Read-only: only this module may advance the state machine. */
export function useIdentity() {
  return {
    status: readonly(status),
    refusalCode: readonly(refusalCode),
    errorMessage: readonly(errorMessage),
    config: readonly(config),
    /**
     * Whether the main application should be rendered. A computed, not a method, so the shell
     * re-renders when the state machine advances rather than only on whatever else happened to
     * change.
     */
    isReady: computed(() => status.value === 'disabled' || status.value === 'signed-in'),
  };
}
