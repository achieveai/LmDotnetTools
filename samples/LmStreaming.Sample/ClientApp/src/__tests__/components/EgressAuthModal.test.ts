import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils';
import { nextTick } from 'vue';
import EgressAuthModal from '@/components/EgressAuthModal.vue';
import type { EgressKeyView } from '@/types/egressAuth';

const mocks = vi.hoisted(() => ({
  egressKeys: null as any,
  isLoading: null as any,
  egressDialogRequest: null as any,
  loadEgressKeys: vi.fn(),
  saveEgressKey: vi.fn(),
  removeEgressKey: vi.fn(),
}));

vi.mock('@/composables/useEgressAuth', async () => {
  const { ref } = await import('vue');
  mocks.egressKeys = ref<EgressKeyView[]>([]);
  mocks.isLoading = ref(false);
  mocks.egressDialogRequest = ref({ open: false, prefillHost: undefined as string | undefined });
  return {
    useEgressAuth: () => ({
      egressKeys: mocks.egressKeys,
      isLoading: mocks.isLoading,
      loadEgressKeys: mocks.loadEgressKeys,
      saveEgressKey: mocks.saveEgressKey,
      removeEgressKey: mocks.removeEgressKey,
    }),
    egressDialogRequest: mocks.egressDialogRequest,
    openEgressDialog: vi.fn(),
    closeEgressDialog: vi.fn(),
  };
});

const keyA: EgressKeyView = {
  id: 'a',
  host: 'a.example.com',
  port: 443,
  kind: 'custom-headers',
  headerName: 'Authorization',
  headerNames: ['Authorization', 'X-Api-Key'],
  hasClientSecret: false,
  hasRefreshToken: false,
  scopes: [],
};

const keyB: EgressKeyView = {
  id: 'b',
  host: 'b.example.com',
  port: 443,
  kind: 'refresh-token',
  headerName: 'Authorization',
  headerNames: [],
  hasClientSecret: true,
  hasRefreshToken: true,
  scopes: ['read', 'write'],
};

let activeWrapper: VueWrapper | null = null;

function mountModal() {
  const wrapper = mount(EgressAuthModal, { attachTo: document.body });
  activeWrapper = wrapper;
  return wrapper;
}

describe('EgressAuthModal', () => {
  beforeEach(() => {
    mocks.loadEgressKeys.mockReset();
    mocks.saveEgressKey.mockReset().mockResolvedValue(keyA);
    mocks.removeEgressKey.mockReset().mockResolvedValue(undefined);
    mocks.egressKeys.value = [keyA, keyB];
    mocks.isLoading.value = false;
    mocks.egressDialogRequest.value = { open: false, prefillHost: undefined };
  });

  afterEach(() => {
    activeWrapper?.unmount();
    activeWrapper = null;
  });

  it('defaults the port to 443 and sends the entered port on save', async () => {
    const wrapper = mountModal();
    await flushPromises();
    await wrapper.get('[data-testid="egress-add-button"]').trigger('click');
    await nextTick();

    const portInput = wrapper.get<HTMLInputElement>('[data-testid="egress-port-input"]');
    expect(portInput.element.value).toBe('443');

    await wrapper.get('[data-testid="egress-host-input"]').setValue('host.docker.internal');
    await portInput.setValue('8443');
    const nameInputs = wrapper.findAll('[data-testid="egress-header-name-input"]');
    await nameInputs[0].setValue('Authorization');
    const valueInputs = wrapper.findAll('[data-testid="egress-header-value-input"]');
    await valueInputs[0].setValue('Bearer t');
    await wrapper.get('[data-testid="egress-save-button"]').trigger('click');
    await flushPromises();

    expect(mocks.saveEgressKey).toHaveBeenCalledTimes(1);
    expect(mocks.saveEgressKey.mock.calls[0][0]).toMatchObject({ host: 'host.docker.internal', port: 8443 });
  });

  it('renders host:port in the list only when the port is not 443', async () => {
    mocks.egressKeys.value = [keyA, { ...keyB, id: 'c', host: 'host.docker.internal', port: 8443 }];
    const wrapper = mountModal();
    await flushPromises();

    const hosts = wrapper.findAll('.key-host').map((h) => h.text());
    expect(hosts).toEqual(['a.example.com', 'host.docker.internal:8443']);
  });

  it('prefills the stored port on edit', async () => {
    mocks.egressKeys.value = [{ ...keyA, port: 8443 }];
    const wrapper = mountModal();
    await flushPromises();
    await wrapper.get('[data-testid="egress-key-item"] .btn-secondary').trigger('click');
    await nextTick();

    expect(wrapper.get<HTMLInputElement>('[data-testid="egress-port-input"]').element.value).toBe('8443');
  });

  it('loads keys on mount and renders one item per key with data-key-id', async () => {
    const wrapper = mountModal();
    await flushPromises();

    expect(mocks.loadEgressKeys).toHaveBeenCalledTimes(1);
    const items = wrapper.findAll('[data-testid="egress-key-item"]');
    expect(items).toHaveLength(2);
    expect(items[0].attributes('data-key-id')).toBe('a');
    expect(items[1].attributes('data-key-id')).toBe('b');
    // Masked indicators, never secret values.
    expect(wrapper.text()).toContain('client secret set');
    expect(wrapper.text()).toContain('refresh token set');
  });

  it('opens the editor in create mode when the add button is clicked', async () => {
    const wrapper = mountModal();
    await flushPromises();

    expect(wrapper.find('[data-testid="egress-host-input"]').exists()).toBe(false);
    await wrapper.get('[data-testid="egress-add-button"]').trigger('click');
    await nextTick();

    expect(wrapper.find('[data-testid="egress-host-input"]').exists()).toBe(true);
    // Default kind is custom-headers -> a header row is present.
    expect(wrapper.findAll('[data-testid="egress-header-name-input"]')).toHaveLength(1);
  });

  it('adds header rows with the add-header-row button', async () => {
    const wrapper = mountModal();
    await flushPromises();
    await wrapper.get('[data-testid="egress-add-button"]').trigger('click');
    await nextTick();

    await wrapper.get('[data-testid="egress-add-header-row"]').trigger('click');
    await nextTick();

    expect(wrapper.findAll('[data-testid="egress-header-name-input"]')).toHaveLength(2);
  });

  it('saves a custom-headers key with the host and header rows', async () => {
    const wrapper = mountModal();
    await flushPromises();
    await wrapper.get('[data-testid="egress-add-button"]').trigger('click');
    await nextTick();

    await wrapper.get('[data-testid="egress-host-input"]').setValue('api.example.com');
    await wrapper.get('[data-testid="egress-header-name-input"]').setValue('Authorization');
    await wrapper.get('[data-testid="egress-header-value-input"]').setValue('Bearer xyz');
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(mocks.saveEgressKey).toHaveBeenCalledWith({
      id: null,
      host: 'api.example.com',
      port: 443,
      kind: 'custom-headers',
      headers: [{ name: 'Authorization', value: 'Bearer xyz' }],
    });
  });

  it('builds an OAuth request when the kind is switched to refresh-token', async () => {
    const wrapper = mountModal();
    await flushPromises();
    await wrapper.get('[data-testid="egress-add-button"]').trigger('click');
    await nextTick();

    await wrapper.get('[data-testid="egress-kind-select"]').setValue('refresh-token');
    await nextTick();
    await wrapper.get('[data-testid="egress-host-input"]').setValue('auth.example.com');
    await wrapper.get('#egress-token-endpoint').setValue('https://auth.example.com/token');
    await wrapper.get('#egress-client-id').setValue('cid');
    await wrapper.get('#egress-client-secret').setValue('csecret');
    await wrapper.get('#egress-refresh-token').setValue('rtoken');
    await wrapper.get('#egress-scopes').setValue('read write');
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(mocks.saveEgressKey).toHaveBeenCalledWith({
      id: null,
      host: 'auth.example.com',
      port: 443,
      kind: 'refresh-token',
      headerName: 'Authorization',
      scopes: ['read', 'write'],
      tokenEndpoint: 'https://auth.example.com/token',
      clientId: 'cid',
      clientSecret: 'csecret',
      refreshToken: 'rtoken',
    });
  });

  it.each(['0', '', '70000', '84a3'])(
    'rejects an invalid port %j before saving instead of coercing it to 443',
    async (typed) => {
      const wrapper = mountModal();
      await flushPromises();
      await wrapper.get('[data-testid="egress-add-button"]').trigger('click');
      await nextTick();

      await wrapper.get('[data-testid="egress-host-input"]').setValue('host.docker.internal');
      await wrapper.get('[data-testid="egress-port-input"]').setValue(typed);
      const nameInputs = wrapper.findAll('[data-testid="egress-header-name-input"]');
      await nameInputs[0].setValue('Authorization');
      const valueInputs = wrapper.findAll('[data-testid="egress-header-value-input"]');
      await valueInputs[0].setValue('Bearer t');
      // Submit the form directly: the input's min/max let the browser (and jsdom) block a submit-button
      // click for 0 / 70000 natively, which would leave the script-side validator unexercised.
      await wrapper.get('form').trigger('submit');
      await flushPromises();

      expect(mocks.saveEgressKey).not.toHaveBeenCalled();
      expect(wrapper.get('[data-testid="egress-port-error"]').text()).toContain('1 to 65535');
    }
  );

  it('validates that host is required before saving', async () => {
    const wrapper = mountModal();
    await flushPromises();
    await wrapper.get('[data-testid="egress-add-button"]').trigger('click');
    await nextTick();

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(mocks.saveEgressKey).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('Host is required');
  });

  it('prefills non-secret fields on edit and leaves the header value blank', async () => {
    const wrapper = mountModal();
    await flushPromises();

    // Second Edit button belongs to keyB; edit keyA (custom-headers) via its Edit button.
    const items = wrapper.findAll('[data-testid="egress-key-item"]');
    await items[0].get('.btn-secondary').trigger('click');
    await nextTick();

    const hostInput = wrapper.get<HTMLInputElement>('[data-testid="egress-host-input"]');
    expect(hostInput.element.value).toBe('a.example.com');
    const nameInputs = wrapper.findAll<HTMLInputElement>('[data-testid="egress-header-name-input"]');
    expect(nameInputs.map((n) => n.element.value)).toEqual(['Authorization', 'X-Api-Key']);
    const valueInputs = wrapper.findAll<HTMLInputElement>('[data-testid="egress-header-value-input"]');
    expect(valueInputs.every((v) => v.element.value === '')).toBe(true);

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    // Update carries the id and preserves blank header values (server keeps stored secret).
    expect(mocks.saveEgressKey).toHaveBeenCalledWith({
      id: 'a',
      host: 'a.example.com',
      port: 443,
      kind: 'custom-headers',
      headers: [
        { name: 'Authorization', value: '' },
        { name: 'X-Api-Key', value: '' },
      ],
    });
  });

  it('deletes a key via its delete button', async () => {
    const wrapper = mountModal();
    await flushPromises();

    const deleteButtons = wrapper.findAll('[data-testid="egress-delete-button"]');
    await deleteButtons[1].trigger('click');
    await flushPromises();

    expect(mocks.removeEgressKey).toHaveBeenCalledWith('b');
  });

  it('surfaces a server validation error inline on save failure', async () => {
    mocks.saveEgressKey.mockRejectedValueOnce(new Error('host already configured'));
    const wrapper = mountModal();
    await flushPromises();
    await wrapper.get('[data-testid="egress-add-button"]').trigger('click');
    await nextTick();

    await wrapper.get('[data-testid="egress-host-input"]').setValue('dup.example.com');
    await wrapper.get('[data-testid="egress-header-name-input"]').setValue('Authorization');
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    const error = wrapper.find('[data-testid="egress-error"]');
    expect(error.exists()).toBe(true);
    expect(error.text()).toContain('host already configured');
  });

  it('opens prefilled in create mode when egressDialogRequest carries a host', async () => {
    mocks.egressDialogRequest.value = { open: true, prefillHost: 'prefill.example.com' };
    const wrapper = mountModal();
    await flushPromises();

    const hostInput = wrapper.get<HTMLInputElement>('[data-testid="egress-host-input"]');
    expect(hostInput.element.value).toBe('prefill.example.com');
  });

  it('emits close when the close button is clicked', async () => {
    const wrapper = mountModal();
    await flushPromises();

    await wrapper.get('[data-testid="egress-auth-modal-close"]').trigger('click');
    expect(wrapper.emitted('close')).toBeTruthy();
  });
});
