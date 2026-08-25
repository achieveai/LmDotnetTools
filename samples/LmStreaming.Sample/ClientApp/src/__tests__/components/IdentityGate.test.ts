import { describe, it, expect, vi, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { ref } from 'vue';
import type { IdentityRefusalCode, IdentityStatus } from '@/types/identity';

const status = ref<IdentityStatus>('loading');
const refusalCode = ref<IdentityRefusalCode | null>(null);
const errorMessage = ref<string | null>(null);
const startSignIn = vi.fn();

vi.mock('@/composables/useIdentity', () => ({
  useIdentity: () => ({ status, refusalCode, errorMessage }),
  startSignIn: () => startSignIn(),
}));

const IdentityGate = (await import('@/components/IdentityGate.vue')).default;

function show(next: IdentityStatus, code: IdentityRefusalCode | null = null) {
  status.value = next;
  refusalCode.value = code;
  return mount(IdentityGate);
}

beforeEach(() => {
  startSignIn.mockReset();
  errorMessage.value = null;
});

describe('IdentityGate', () => {
  it('offers sign-in when the user is simply not signed in', () => {
    const wrapper = show('signing-in');
    expect(wrapper.text()).toContain('Redirecting');

    const signedOut = show('error');
    expect(signedOut.find('button').exists()).toBe(true);
  });

  it('explains an unprovisioned organisation', () => {
    const wrapper = show('rejected', 'tenant_not_provisioned');

    expect(wrapper.find('[data-testid="identity-gate-not-provisioned"]').exists()).toBe(true);
    expect(wrapper.text()).toContain('not been onboarded');
  });

  it('explains a suspended organisation', () => {
    const wrapper = show('rejected', 'tenant_suspended');

    expect(wrapper.find('[data-testid="identity-gate-suspended"]').exists()).toBe(true);
    expect(wrapper.text()).toContain('suspended');
  });

  it.each<IdentityRefusalCode>(['tenant_not_provisioned', 'tenant_suspended'])(
    'offers no sign-in control on the %s screen',
    (code) => {
      const wrapper = show('rejected', code);

      // The clause with teeth. A sign-in button here is the sign-in loop: pressing it produces the
      // identical refusal, and a screen that invites the press is how a user ends up bouncing
      // between the app and Entra forever.
      expect(wrapper.findAll('button')).toHaveLength(0);
    },
  );

  it('renders the failure reason when sign-in could not be completed', () => {
    errorMessage.value = 'config unreachable';
    const wrapper = show('error');

    expect(wrapper.find('[data-testid="identity-gate-error"]').text()).toBe('config unreachable');
  });

  it('starts sign-in when the button is pressed', async () => {
    const wrapper = show('error');

    await wrapper.find('button').trigger('click');

    expect(startSignIn).toHaveBeenCalledOnce();
  });
});

describe('IdentityGate on an expired session', () => {
  it('explains the expiry and offers a fresh sign-in', async () => {
    const wrapper = show('expired');

    expect(wrapper.find('[data-testid="identity-gate-expired"]').exists()).toBe(true);

    await wrapper.find('button').trigger('click');
    expect(startSignIn).toHaveBeenCalledOnce();
  });

  it('does not reuse the refusal wording, because the remedy is the opposite one', () => {
    const expired = show('expired');
    const refused = show('rejected', 'tenant_suspended');

    // Expiry is fixed by signing in again; a refusal is not fixed by signing in at all. One screen
    // for both causes would either invite a pointless sign-in loop or withhold the one control
    // that actually works.
    expect(expired.findAll('button')).toHaveLength(1);
    expect(refused.findAll('button')).toHaveLength(0);
    expect(expired.find('[data-testid="identity-gate-suspended"]').exists()).toBe(false);
  });
});
