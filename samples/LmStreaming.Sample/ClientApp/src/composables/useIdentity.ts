import { ref, readonly, computed, type Ref } from 'vue';
import { getIdentityConfig } from '@/api/identityApi';
import { onApiRefusal, setAccessToken } from '@/api/http';
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

    setAccessToken(token);
    status.value = 'signed-in';
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error);
    status.value = 'error';
  }
}

/** Starts an interactive sign-in, for the button on the signed-out screen. */
export async function startSignIn(): Promise<void> {
  status.value = 'signing-in';
  await entraAuth.signIn();
}

/** Resets every module-level ref. For tests only — the app initialises once and never tears down. */
export function resetIdentityState(): void {
  status.value = 'loading';
  refusalCode.value = null;
  errorMessage.value = null;
  config.value = null;
  setAccessToken(null);
  onApiRefusal(null);
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
