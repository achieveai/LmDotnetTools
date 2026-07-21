import { describe, it, expect, vi, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import AuthRequiredBanner from '@/components/AuthRequiredBanner.vue';
import type { AuthRequiredEvent } from '@/types/auth';

// The banner routes a pre-defined-key failure to the Egress Auth dialog (openEgressDialog) and an
// OAuth provider to the same-origin sign-in popup (window.open). Pin both branches.
const mocks = vi.hoisted(() => ({ openEgressDialog: vi.fn() }));

vi.mock('@/composables/useEgressAuth', () => ({
  openEgressDialog: mocks.openEgressDialog,
}));

function oauthRequest(): AuthRequiredEvent {
  return { $type: 'auth_required', providerId: 'github', signinUrl: '/auth/github', reason: 'sign in' };
}

function predefinedRequest(): AuthRequiredEvent {
  return { $type: 'auth_required', providerId: 'predefined-abc', signinUrl: '/auth/predefined-abc', reason: 'key invalid' };
}

describe('AuthRequiredBanner routing', () => {
  beforeEach(() => {
    mocks.openEgressDialog.mockClear();
  });

  it('opens the OAuth sign-in popup for a managed provider', () => {
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null);
    const wrapper = mount(AuthRequiredBanner, { props: { requests: [oauthRequest()] } });

    expect(wrapper.find('[data-testid="auth-signin-button"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="auth-update-key-button"]').exists()).toBe(false);

    wrapper.find('[data-testid="auth-signin-button"]').trigger('click');
    expect(openSpy).toHaveBeenCalledWith('/auth/github', expect.any(String), expect.any(String));
    expect(mocks.openEgressDialog).not.toHaveBeenCalled();
    openSpy.mockRestore();
  });

  it('opens the Egress Auth dialog for a pre-defined key', async () => {
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null);
    const wrapper = mount(AuthRequiredBanner, { props: { requests: [predefinedRequest()] } });

    expect(wrapper.find('[data-testid="auth-update-key-button"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="auth-signin-button"]').exists()).toBe(false);

    await wrapper.find('[data-testid="auth-update-key-button"]').trigger('click');
    expect(mocks.openEgressDialog).toHaveBeenCalledTimes(1);
    expect(openSpy).not.toHaveBeenCalled();
    openSpy.mockRestore();
  });
});
