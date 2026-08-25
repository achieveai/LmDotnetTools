import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  type AccountInfo,
  type Configuration,
} from '@azure/msal-browser';
import type { IdentityConfig } from '@/types/identity';

/**
 * The MSAL Browser sign-in, wrapped so that the rest of the client never imports MSAL directly.
 *
 * Everything here is deliberately behind one narrow surface — {@link signIn} and
 * {@link acquireAccessToken}. The identity composable and the app shell talk to that surface, so
 * swapping the identity broker later touches this file and nothing else.
 */

let instance: PublicClientApplication | null = null;
let scopes: string[] = [];

/**
 * Builds the MSAL configuration.
 *
 * `sessionStorage`, not `localStorage`: a token cached in `localStorage` outlives the browser
 * session and is readable by any script that later runs on this origin. Session storage is scoped
 * to the tab, which matches how long the sign-in is actually meant to be good for.
 */
function configurationFor(config: IdentityConfig): Configuration {
  return {
    auth: {
      clientId: config.clientId!,
      authority: config.authority!,
      redirectUri: window.location.origin,
    },
    cache: {
      cacheLocation: 'sessionStorage',
    },
  };
}

/**
 * Initialises MSAL and completes any redirect that is coming back in.
 *
 * Returns the signed-in account, or null when nobody is signed in yet. Handling the redirect
 * promise BEFORE looking at the account list is required, not stylistic: on the way back from
 * Entra the account only enters the cache as a result of that call.
 */
export async function initialize(config: IdentityConfig): Promise<AccountInfo | null> {
  scopes = config.scopes;
  instance = new PublicClientApplication(configurationFor(config));
  await instance.initialize();

  const redirectResult = await instance.handleRedirectPromise();
  if (redirectResult?.account) {
    instance.setActiveAccount(redirectResult.account);
    return redirectResult.account;
  }

  const [existing] = instance.getAllAccounts();
  if (existing) {
    instance.setActiveAccount(existing);
    return existing;
  }

  return null;
}

/** Starts an interactive sign-in. Navigates away, so nothing after this call runs. */
export async function signIn(): Promise<void> {
  if (instance === null) {
    throw new Error('entraAuth.signIn called before initialize');
  }

  await instance.loginRedirect({ scopes });
}

/**
 * Returns an access token for the active account, or null when there is no active account.
 *
 * Silent first, interactive only on the error type that specifically means "silent cannot work"
 * (consent needed, MFA, expired session). Falling back to a redirect on ANY failure would turn a
 * transient network error into a full page navigation, and — worse — into a sign-in loop whenever
 * the failure is one that signing in cannot fix.
 */
export async function acquireAccessToken(): Promise<string | null> {
  if (instance === null) {
    return null;
  }

  const account = instance.getActiveAccount();
  if (!account) {
    return null;
  }

  try {
    const result = await instance.acquireTokenSilent({ scopes, account });
    return result.accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await instance.acquireTokenRedirect({ scopes, account });
      return null;
    }

    throw error;
  }
}

/** Clears the module's state. Exists so a test can start from a known point. */
export function reset(): void {
  instance = null;
  scopes = [];
}
