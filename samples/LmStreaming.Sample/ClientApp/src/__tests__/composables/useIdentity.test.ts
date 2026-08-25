import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { IdentityConfig } from '@/types/identity';

const getIdentityConfig = vi.fn<() => Promise<IdentityConfig>>();
const initialize = vi.fn();
const signIn = vi.fn();
const acquireAccessToken = vi.fn();

vi.mock('@/api/identityApi', () => ({ getIdentityConfig: () => getIdentityConfig() }));
vi.mock('@/auth/entraAuth', () => ({
  initialize: (...args: unknown[]) => initialize(...args),
  signIn: () => signIn(),
  acquireAccessToken: () => acquireAccessToken(),
  reset: () => {},
}));

const { useIdentity, initializeIdentity, startSignIn, resetIdentityState } = await import(
  '@/composables/useIdentity'
);
const { getAccessToken, apiFetch, onApiRefusal } = await import('@/api/http');
const { IDENTITY_REFUSAL_HEADER } = await import('@/types/identity');

const CONFIGURED: IdentityConfig = {
  enforce: true,
  clientId: 'client-1',
  authority: 'https://login.microsoftonline.com/organizations',
  scopes: ['api://client-1/access_as_user'],
};

beforeEach(() => {
  resetIdentityState();
  getIdentityConfig.mockReset();
  initialize.mockReset();
  signIn.mockReset();
  acquireAccessToken.mockReset();
});

afterEach(() => {
  vi.restoreAllMocks();
  resetIdentityState();
});

describe('initializeIdentity when the deployment does not enforce', () => {
  it('shows the app without touching MSAL', async () => {
    getIdentityConfig.mockResolvedValue({
      enforce: false,
      clientId: null,
      authority: null,
      scopes: [],
    });

    await initializeIdentity();

    const { status, isReady } = useIdentity();
    expect(status.value).toBe('disabled');
    expect(isReady.value).toBe(true);

    // The regression gate, expressed on the client: with enforcement off nothing signs in, so the
    // app renders exactly as it did before identity existed.
    expect(initialize).not.toHaveBeenCalled();
    expect(signIn).not.toHaveBeenCalled();
    expect(getAccessToken()).toBeNull();
  });

  it('shows the app when enforcement is on but no app registration is configured', async () => {
    getIdentityConfig.mockResolvedValue({
      enforce: true,
      clientId: null,
      authority: null,
      scopes: [],
    });

    await initializeIdentity();

    // There is no directory to sign in to. Rendering a sign-in button here would give the user a
    // control that cannot possibly work.
    expect(useIdentity().status.value).toBe('disabled');
    expect(signIn).not.toHaveBeenCalled();
  });
});

describe('initializeIdentity when the deployment enforces', () => {
  it('signs in when there is no account yet', async () => {
    getIdentityConfig.mockResolvedValue(CONFIGURED);
    initialize.mockResolvedValue(null);
    signIn.mockResolvedValue(undefined);

    await initializeIdentity();

    expect(signIn).toHaveBeenCalledOnce();
    expect(useIdentity().status.value).toBe('signing-in');
    expect(useIdentity().isReady.value).toBe(false);
  });

  it('publishes the acquired token and shows the app', async () => {
    getIdentityConfig.mockResolvedValue(CONFIGURED);
    initialize.mockResolvedValue({ homeAccountId: 'a' });
    acquireAccessToken.mockResolvedValue('token-xyz');

    await initializeIdentity();

    expect(useIdentity().status.value).toBe('signed-in');
    expect(useIdentity().isReady.value).toBe(true);
    expect(getAccessToken()).toBe('token-xyz');
    expect(signIn).not.toHaveBeenCalled();
  });

  it('does not show the app when the token acquisition redirected away', async () => {
    getIdentityConfig.mockResolvedValue(CONFIGURED);
    initialize.mockResolvedValue({ homeAccountId: 'a' });
    acquireAccessToken.mockResolvedValue(null);

    await initializeIdentity();

    expect(useIdentity().status.value).toBe('signing-in');
    expect(getAccessToken()).toBeNull();
  });

  it('turns a failure into a rendered error rather than an unhandled rejection', async () => {
    getIdentityConfig.mockRejectedValue(new Error('config unreachable'));

    await initializeIdentity();

    const { status, errorMessage, isReady } = useIdentity();
    expect(status.value).toBe('error');
    expect(errorMessage.value).toBe('config unreachable');
    expect(isReady.value).toBe(false);
  });
});

describe('a whole-organisation refusal arriving on any api call', () => {
  async function signedIn(): Promise<void> {
    getIdentityConfig.mockResolvedValue(CONFIGURED);
    initialize.mockResolvedValue({ homeAccountId: 'a' });
    acquireAccessToken.mockResolvedValue('token-xyz');
    await initializeIdentity();
  }

  function refusal(code: string): Response {
    return {
      ok: false,
      status: 403,
      headers: new Headers({ [IDENTITY_REFUSAL_HEADER]: code }),
      json: async () => ({}),
    } as Response;
  }

  it('takes a signed-in session to the not-provisioned screen', async () => {
    await signedIn();
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusal('tenant_not_provisioned'));

    await apiFetch('/api/conversations');

    const { status, refusalCode, isReady } = useIdentity();
    expect(status.value).toBe('rejected');
    expect(refusalCode.value).toBe('tenant_not_provisioned');
    expect(isReady.value).toBe(false);
  });

  it('takes a signed-in session to the suspended screen', async () => {
    await signedIn();
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusal('tenant_suspended'));

    await apiFetch('/api/conversations');

    expect(useIdentity().refusalCode.value).toBe('tenant_suspended');
  });

  it('drops the token and never re-triggers sign-in', async () => {
    await signedIn();
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusal('tenant_not_provisioned'));

    await apiFetch('/api/conversations');

    // The refusal is terminal. Re-entering sign-in produces the identical refusal, which is the
    // infinite loop this whole design exists to avoid.
    expect(getAccessToken()).toBeNull();
    expect(signIn).not.toHaveBeenCalled();
  });

  it('is not registered once the state has been reset', async () => {
    await signedIn();
    resetIdentityState();
    onApiRefusal(null);
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(refusal('tenant_suspended'));

    await apiFetch('/api/conversations');

    expect(useIdentity().status.value).toBe('loading');
  });
});

describe('startSignIn from the signed-out screen', () => {
  it('reports a redirect that never happens instead of leaving the user on the spinner', async () => {
    // loginRedirect can reject before the browser ever navigates - a network failure, a
    // misconfigured app registration, a blocked redirect. The status was already moved to
    // 'signing-in' to drive the spinner, so if the rejection escapes, that spinner is the last
    // thing the user ever sees: no error, no retry, and the page is not going anywhere.
    signIn.mockRejectedValue(new Error('redirect_uri mismatch'));

    await startSignIn();

    const { status, errorMessage } = useIdentity();
    expect(status.value).toBe('error');
    expect(errorMessage.value).toBe('redirect_uri mismatch');
  });

  it('leaves the spinner up when the redirect is actually under way', async () => {
    // The success path must NOT be turned into an error state: signIn resolving means the
    // browser is navigating away, and 'signing-in' is the correct thing to render until it does.
    signIn.mockResolvedValue(undefined);

    await startSignIn();

    const { status, errorMessage } = useIdentity();
    expect(status.value).toBe('signing-in');
    expect(errorMessage.value).toBeNull();
  });
});
