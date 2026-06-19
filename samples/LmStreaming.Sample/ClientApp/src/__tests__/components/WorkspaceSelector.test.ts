import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils';
import { nextTick } from 'vue';
import WorkspaceSelector from '@/components/WorkspaceSelector.vue';
import type { Workspace } from '@/types/workspace';

// The selector now sources its marketplace options from the gateway catalog. Mock the API so the
// component renders deterministic, gateway-shaped options (aliases) instead of the old static seed.
vi.mock('@/api/marketplacesApi', () => ({
  MarketplaceGatewayUnavailableError: class extends Error {},
  listMarketplaces: vi.fn(async () => ({
    selected: ['ClaudePlugins', 'superpowers'],
    marketplaces: [
      { alias: 'ClaudePlugins', error: null, plugins: [] },
      { alias: 'superpowers', error: null, plugins: [] },
    ],
  })),
}));

const workspaces: Workspace[] = [
  {
    id: 'default',
    name: 'Default',
    directoryRelPath: '',
    marketplaces: [],
    isSystemDefined: true,
    createdAt: 0,
    updatedAt: 0,
  },
  {
    id: 'ws-user',
    name: 'My Project',
    directoryRelPath: 'my-project',
    marketplaces: ['core'],
    isSystemDefined: false,
    createdAt: 0,
    updatedAt: 0,
  },
];

// Track wrappers mounted with attachTo so each test tears them down (removes
// the component from document.body and detaches its document click listener).
let activeWrapper: VueWrapper | null = null;

function mountSelector() {
  const wrapper = mount(WorkspaceSelector, {
    attachTo: document.body,
    props: {
      workspaces,
      selectedWorkspaceId: 'default',
    },
  });
  activeWrapper = wrapper;
  return wrapper;
}

async function openDropdown(wrapper: VueWrapper) {
  await wrapper.get('[data-testid="workspace-selector-button"]').trigger('click');
  await nextTick();
}

afterEach(() => {
  activeWrapper?.unmount();
  activeWrapper = null;
});

describe('WorkspaceSelector', () => {
  it('opens the create form when "+ New workspace" is clicked and keeps the dropdown open', async () => {
    const wrapper = mountSelector();
    await openDropdown(wrapper);

    // Sanity: dropdown is open in list mode (create trigger is present).
    expect(wrapper.find('[data-testid="workspace-create-open"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-form"]').exists()).toBe(false);

    // Regression guard: clicking the create button must NOT bubble to the
    // document outside-click handler (which would close the whole dropdown).
    // Without @click.stop the menu disappears instead of showing the form.
    await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
    await nextTick();

    // The inline create form renders with all its fields.
    expect(wrapper.find('[data-testid="workspace-create-form"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-name"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-directory"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-submit"]').exists()).toBe(true);

    // ...and the dropdown menu itself is still open (not collapsed back to the button).
    expect(wrapper.find('.dropdown-menu').exists()).toBe(true);
  });

  it('auto-slugs the directory from the name until the directory is edited', async () => {
    const wrapper = mountSelector();
    await openDropdown(wrapper);
    await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
    await nextTick();

    const nameInput = wrapper.get('[data-testid="workspace-create-name"]');
    await nameInput.setValue('Demo Workspace');
    await nextTick();

    const directoryInput = wrapper.get<HTMLInputElement>(
      '[data-testid="workspace-create-directory"]'
    );
    expect(directoryInput.element.value).toBe('demo-workspace');
  });

  it('emits create-workspace with the name and slugged directory on submit', async () => {
    const wrapper = mountSelector();
    await openDropdown(wrapper);
    await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
    await nextTick();

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Demo Workspace');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const emitted = wrapper.emitted('create-workspace');
    expect(emitted).toBeTruthy();
    expect(emitted![0][0]).toEqual({
      name: 'Demo Workspace',
      directoryRelPath: 'demo-workspace',
      marketplaces: [],
    });
  });

  it('renders gateway-sourced marketplaces and emits the selected alias on create', async () => {
    const wrapper = mountSelector();
    await openDropdown(wrapper);
    await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
    await flushPromises(); // let loadAvailableMarketplaces() resolve
    await nextTick();

    // Gateway aliases render; the old static seed (core/community) is gone.
    expect(wrapper.find('[data-testid="workspace-create-marketplace-ClaudePlugins"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-marketplace-superpowers"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-marketplace-core"]').exists()).toBe(false);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Plugins WS');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-marketplace-ClaudePlugins"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const emitted = wrapper.emitted('create-workspace');
    expect(emitted).toBeTruthy();
    expect(emitted![0][0]).toEqual({
      name: 'Plugins WS',
      directoryRelPath: 'plugins-ws',
      marketplaces: ['ClaudePlugins'],
    });
  });
});
