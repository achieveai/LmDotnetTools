import { describe, it, expect, vi, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { ref, defineComponent } from 'vue';
import type { IdentityRefusalCode, IdentityStatus } from '@/types/identity';

const status = ref<IdentityStatus>('loading');
const refusalCode = ref<IdentityRefusalCode | null>(null);
const isReady = ref(false);
const initializeIdentity = vi.fn().mockResolvedValue(undefined);

vi.mock('@/composables/useIdentity', () => ({
  useIdentity: () => ({ status, refusalCode, errorMessage: ref(null), isReady }),
  initializeIdentity: () => initializeIdentity(),
  startSignIn: vi.fn(),
}));

/**
 * A stand-in for the real chat layout. It records that it MOUNTED, which is the property under
 * test: the real component issues its first API calls from `onMounted`, so a gate that merely
 * hides it would still have let those requests go out unauthenticated.
 */
let chatLayoutMounts = 0;
vi.mock('@/components/ChatLayout.vue', () => ({
  default: defineComponent({
    name: 'ChatLayoutStub',
    setup() {
      chatLayoutMounts += 1;
      return () => 'chat';
    },
  }),
}));

const App = (await import('@/App.vue')).default;

beforeEach(() => {
  chatLayoutMounts = 0;
  initializeIdentity.mockClear();
});

describe('the app shell gate', () => {
  it('starts identity resolution on mount', () => {
    mount(App);

    expect(initializeIdentity).toHaveBeenCalledOnce();
  });

  it('does not mount the chat layout while identity is unresolved', () => {
    isReady.value = false;
    status.value = 'loading';

    const wrapper = mount(App);

    // v-if, not v-show. ChatLayout loads conversations from onMounted; hiding it would still fire
    // those requests, unauthenticated, at a deployment that is about to refuse them.
    expect(chatLayoutMounts).toBe(0);
    expect(wrapper.find('[data-testid="identity-gate"]').exists()).toBe(true);
  });

  it('does not mount the chat layout behind a refusal screen', () => {
    isReady.value = false;
    status.value = 'rejected';
    refusalCode.value = 'tenant_not_provisioned';

    mount(App);

    expect(chatLayoutMounts).toBe(0);
  });

  it('mounts the chat layout once identity is ready', () => {
    isReady.value = true;
    status.value = 'disabled';

    const wrapper = mount(App);

    // The regression gate: with Identity:Enforce false the state resolves to `disabled` and the
    // app renders exactly as it did before identity existed.
    expect(chatLayoutMounts).toBe(1);
    expect(wrapper.find('[data-testid="identity-gate"]').exists()).toBe(false);
  });
});
