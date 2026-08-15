import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils';
import { nextTick } from 'vue';
import WorkspaceSelector from '@/components/WorkspaceSelector.vue';
import { listMarketplaces } from '@/api/marketplacesApi';
import type { Workspace } from '@/types/workspace';

// The selector now sources its marketplace options from the gateway catalog. Mock the API so the
// component renders deterministic, gateway-shaped options (aliases) instead of the old static seed.
// The catalog is mutable so a test can advertise plugin-filtering capability and nested plugins;
// the DEFAULT deliberately advertises no `capabilities` block at all — that is the "unknown" state
// an older gateway reports, and it must fail closed to today's marketplace-only UI.
const catalog = vi.hoisted(() => ({
  value: {
    selected: ['ClaudePlugins', 'superpowers'],
    marketplaces: [
      { alias: 'ClaudePlugins', error: null, plugins: [] },
      { alias: 'superpowers', error: null, plugins: [] },
    ],
  } as Record<string, unknown>,
}));

const defaultCatalog = {
  selected: ['ClaudePlugins', 'superpowers'],
  marketplaces: [
    { alias: 'ClaudePlugins', error: null, plugins: [] },
    { alias: 'superpowers', error: null, plugins: [] },
  ],
} as Record<string, unknown>;

/** Catalog plugin entry, shaped like the gateway's `CatalogPlugin`. */
const plugin = (name: string) => ({
  name,
  version: null,
  description: '',
  skills: [],
  agents: [],
});

/** A catalog that DOES advertise plugin filtering, with two plugins under `demo`. */
const filteringCatalog = {
  selected: ['demo'],
  marketplaces: [{ alias: 'demo', error: null, plugins: [plugin('toolkit'), plugin('extras')] }],
  capabilities: { pluginFiltering: true },
} as Record<string, unknown>;

/**
 * TWO plugin-bearing marketplaces. A single-marketplace fixture cannot tell a correctly scoped
 * operation from a global one: with only `demo` in the catalog, "every plugin of the ENABLED
 * marketplaces" and "every plugin in the catalog" are the same list, and pruning one marketplace
 * looks identical to clearing the selection. Every scoping claim is asserted against this.
 */
const twoMarketplaceCatalog = {
  selected: ['demo', 'extra-mp'],
  marketplaces: [
    { alias: 'demo', error: null, plugins: [plugin('toolkit'), plugin('extras')] },
    { alias: 'extra-mp', error: null, plugins: [plugin('widget')] },
  ],
  capabilities: { pluginFiltering: true },
} as Record<string, unknown>;

/**
 * A plugin-bearing catalog with NO `capabilities` block — what an older gateway reports. Used to
 * prove the fail-closed gate: the marketplace here HAS plugins to render, so an absent per-plugin UI
 * is caused by the gate rather than by there being nothing to show.
 */
const uncapableCatalog = {
  selected: ['demo'],
  marketplaces: [{ alias: 'demo', error: null, plugins: [plugin('toolkit'), plugin('extras')] }],
} as Record<string, unknown>;

vi.mock('@/api/marketplacesApi', () => ({
  MarketplaceGatewayUnavailableError: class extends Error {},
  listMarketplaces: vi.fn(async () => catalog.value),
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

function mountSelector(props: Partial<Record<string, unknown>> = {}) {
  const wrapper = mount(WorkspaceSelector, {
    attachTo: document.body,
    props: {
      workspaces,
      selectedWorkspaceId: 'default',
      ...props,
    },
  });
  activeWrapper = wrapper;
  return wrapper;
}

async function openDropdown(wrapper: VueWrapper) {
  await wrapper.get('[data-testid="workspace-selector-button"]').trigger('click');
  await nextTick();
}

beforeEach(() => {
  catalog.value = defaultCatalog;
});

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

// --- Per-plugin selection -----------------------------------------------------------------

type CreatePayload = {
  name: string;
  directoryRelPath?: string;
  marketplaces?: string[];
  pluginSelection?: { marketplace: string; plugin: string }[] | null;
};

type UpdatePayload = {
  marketplaces: string[];
  pluginSelection?: { marketplace: string; plugin: string }[] | null;
  pluginsRevision?: number;
};

async function openCreateForm(wrapper: VueWrapper) {
  await openDropdown(wrapper);
  await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
  await flushPromises();
  await nextTick();
}

async function openEditForm(wrapper: VueWrapper, workspaceId: string) {
  await openDropdown(wrapper);
  await wrapper.get(`[data-testid="workspace-edit-${workspaceId}"]`).trigger('click');
  await flushPromises();
  await nextTick();
}

describe('WorkspaceSelector plugin filtering capability gate', () => {
  /**
   * Fail closed when the gateway reports no `capabilities` block at all (an older build).
   *
   * The catalog here is `uncapableCatalog`, which HAS two plugins, and the marketplace is ENABLED
   * before the DOM is inspected. Both matter: under the default catalog (no plugins, nothing
   * enabled) the "no plugin checkboxes" assertion holds no matter what the gate does, so it would
   * look like coverage while proving nothing. With plugins present and the marketplace on, the only
   * thing keeping the checkboxes out of the DOM is the gate.
   */
  it('hides the plugin UI and omits pluginSelection when capabilities are absent', async () => {
    catalog.value = uncapableCatalog;
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();

    // Non-vacuity guard: the marketplace IS enabled and DOES publish plugins...
    const marketplaceBox = wrapper.get<HTMLInputElement>(
      '[data-testid="workspace-create-marketplace-demo"]'
    );
    expect(marketplaceBox.element.checked).toBe(true);
    expect((catalog.value.marketplaces as { plugins: unknown[] }[])[0].plugins).toHaveLength(2);
    // ...and yet nothing per-plugin renders, because the gate is closed.
    expect(wrapper.findAll('[data-plugin-checkbox="true"]')).toHaveLength(0);
    expect(wrapper.find('[data-testid="workspace-create-plugins-demo"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="workspace-create-plugins-reset"]').exists()).toBe(false);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('No Filtering');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    expect('pluginSelection' in payload).toBe(false);
    // The rest of the request is exactly what this form sent before per-plugin selection existed.
    expect(payload.marketplaces).toEqual(['demo']);
  });

  it('hides the plugin UI when the gateway explicitly reports pluginFiltering: false', async () => {
    catalog.value = { ...filteringCatalog, capabilities: { pluginFiltering: false } };
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();

    expect(wrapper.findAll('[data-plugin-checkbox="true"]')).toHaveLength(0);
  });

  it('hides the plugin UI when the capability flag is null ("unknown")', async () => {
    catalog.value = { ...filteringCatalog, capabilities: { pluginFiltering: null } };
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();

    expect(wrapper.findAll('[data-plugin-checkbox="true"]')).toHaveLength(0);
  });

  it('renders nested plugin checkboxes for an enabled marketplace when filtering is supported', async () => {
    catalog.value = filteringCatalog;
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    // Plugins of a marketplace that is not enabled are not offered.
    expect(wrapper.findAll('[data-plugin-checkbox="true"]')).toHaveLength(0);

    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();

    expect(wrapper.findAll('[data-plugin-checkbox="true"]')).toHaveLength(2);
    expect(wrapper.find('[data-testid="workspace-create-plugin-demo-toolkit"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-plugin-demo-extras"]').exists()).toBe(true);
  });
});

describe('WorkspaceSelector plugin selection tri-state', () => {
  beforeEach(() => {
    catalog.value = filteringCatalog;
  });

  /**
   * The legacy state must survive untouched. RED if enabling a marketplace materializes the
   * selection (e.g. `createPluginSelection.value = allPluginsOf(...)` on add): the payload would
   * carry an enumerated list instead of `null`, and the workspace would stop picking up plugins the
   * marketplace gains later.
   */
  it('emits pluginSelection: null when a marketplace is enabled but no plugin is touched', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Legacy');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();

    // Under `null` every plugin renders checked, because null means "all of them".
    const toolkit = wrapper.get<HTMLInputElement>('[data-testid="workspace-create-plugin-demo-toolkit"]');
    expect(toolkit.element.checked).toBe(true);

    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    expect(payload.pluginSelection).toBeNull();
  });

  /**
   * RED if `togglePluginIn` drops the `allPluginsOf` materialization and starts from `[]`:
   * unchecking `extras` would emit `[]` instead of `[toolkit]`, silently disabling toolkit too.
   */
  it('materializes the remaining plugins when one is unchecked from the legacy state', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Subset');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-plugin-demo-extras"]').trigger('change');
    await nextTick();

    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    expect(payload.pluginSelection).toEqual([{ marketplace: 'demo', plugin: 'toolkit' }]);
  });

  /**
   * `[]` must stay `[]`. RED if any `?? []`/`|| []` is introduced (it would make this pass while
   * the null case breaks) OR if an empty explicit selection is normalised back to `null` — the two
   * cases here pin both directions at once.
   */
  it('emits an explicit [] when every plugin is unchecked, distinct from the legacy null', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('None');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-plugin-demo-extras"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-plugin-demo-toolkit"]').trigger('change');
    await nextTick();

    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    expect(payload.pluginSelection).toEqual([]);
    expect(payload.pluginSelection).not.toBeNull();
  });

  it('returns to the legacy null state via "Use all plugins"', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Reset');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    // The reset control only exists once the selection is explicit.
    expect(wrapper.find('[data-testid="workspace-create-plugins-reset"]').exists()).toBe(false);
    await wrapper.get('[data-testid="workspace-create-plugin-demo-extras"]').trigger('change');
    await nextTick();
    expect(wrapper.find('[data-testid="workspace-create-plugins-reset"]').exists()).toBe(true);

    await wrapper.get('[data-testid="workspace-create-plugins-reset"]').trigger('click');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    expect(payload.pluginSelection).toBeNull();
  });

  /**
   * `indeterminate` has no HTML attribute — it is DOM-property-only, so this asserts
   * `element.indeterminate`, not markup. (Vue sets it as a property for `<input>` with or without
   * the `.prop` modifier; the modifier is kept as an explicit statement of intent. Verified: this
   * test does NOT go red when `.prop` is dropped, but DOES go red when the predicate is stubbed to
   * false, so it pins the behaviour rather than the binding syntax.) Also RED if the predicate
   * returns true for the all/none cases, which are determinate.
   */
  it('marks the marketplace checkbox indeterminate for a partial selection only', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    const marketplace = wrapper.get<HTMLInputElement>(
      '[data-testid="workspace-create-marketplace-demo"]'
    );
    await marketplace.trigger('change');
    await nextTick();
    // All plugins on (legacy null) — determinate.
    expect(marketplace.element.indeterminate).toBe(false);

    await wrapper.get('[data-testid="workspace-create-plugin-demo-extras"]').trigger('change');
    await nextTick();
    // 1 of 2 selected — indeterminate.
    expect(marketplace.element.indeterminate).toBe(true);
    expect(marketplace.element.checked).toBe(true);

    await wrapper.get('[data-testid="workspace-create-plugin-demo-toolkit"]').trigger('change');
    await nextTick();
    // 0 of 2 selected — determinate again.
    expect(marketplace.element.indeterminate).toBe(false);
  });

  it('drops a marketplace\'s plugins from an explicit selection when it is disabled', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Pruned');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-plugin-demo-extras"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();

    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    expect(payload.marketplaces).toEqual([]);
    // Still explicit ([], not null) — the user made a choice; only the stale refs are gone.
    // That the prune is TARGETED rather than a blanket clear is pinned separately, against a
    // two-marketplace catalog, in "WorkspaceSelector plugin selection across marketplaces" below.
    expect(payload.pluginSelection).toEqual([]);
  });

  it('keeps a large marketplace usable by scrolling rather than unmounting its plugins', async () => {
    catalog.value = {
      selected: ['big'],
      marketplaces: [
        {
          alias: 'big',
          error: null,
          plugins: Array.from({ length: 22 }, (_, i) => plugin(`plugin-${i}`)),
        },
      ],
      capabilities: { pluginFiltering: true },
    };
    const wrapper = mountSelector();
    await openCreateForm(wrapper);
    await wrapper.get('[data-testid="workspace-create-marketplace-big"]').trigger('change');
    await nextTick();

    // Every one stays MOUNTED — the panel is bounded by CSS (max-height + overflow-y), not by
    // dropping rows, so a Playwright proof can count and click all 22. Asserting the boundedness
    // itself is out of reach here: scoped styles are not applied in jsdom, so a class assertion
    // would only restate the template. The load-bearing claim is that nothing is truncated.
    expect(wrapper.findAll('[data-plugin-checkbox="true"]')).toHaveLength(22);
    expect(wrapper.find('[data-testid="workspace-create-plugin-big-plugin-0"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-plugin-big-plugin-21"]').exists()).toBe(
      true
    );
  });
});

/**
 * F3. Scoping claims that a one-marketplace catalog cannot express: with a single marketplace,
 * "plugins of the ENABLED marketplaces" and "plugins in the catalog" coincide, and "prune this
 * marketplace" and "clear everything" produce the same list. Each test below fails if the operation
 * under test loses its scope.
 */
describe('WorkspaceSelector plugin selection across marketplaces', () => {
  beforeEach(() => {
    catalog.value = twoMarketplaceCatalog;
  });

  /**
   * Materialization must enumerate only the ENABLED marketplaces.
   *
   * Mutation proving non-vacuity: delete `.filter((m) => enabled.has(m.id))` from `allPluginsOf`
   * -> RED. Unchecking `extras` with only `demo` enabled would then write down `extra-mp/widget`
   * too, and the workspace would be sent a plugin from a marketplace it does not even have.
   */
  it('materializes plugins of the enabled marketplace only, never the whole catalog', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Scoped');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();

    // `extra-mp` is in the catalog but NOT enabled, so it offers no checkboxes...
    expect(wrapper.find('[data-testid="workspace-create-plugins-extra-mp"]').exists()).toBe(false);
    expect(wrapper.findAll('[data-plugin-checkbox="true"]')).toHaveLength(2);

    await wrapper.get('[data-testid="workspace-create-plugin-demo-extras"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    // ...and its plugin never reaches the wire.
    expect(payload.pluginSelection).toEqual([{ marketplace: 'demo', plugin: 'toolkit' }]);
  });

  /**
   * Disabling one marketplace prunes ITS refs and leaves the others intact.
   *
   * Mutation proving non-vacuity: replace `pruneMarketplaceFrom`'s body with `return []` (a blanket
   * clear) -> RED, because `extra-mp/widget` disappears. The single-marketplace version of this test
   * passes under that mutation, which is exactly why it could not be the only one.
   */
  it('prunes only the disabled marketplace, keeping the other marketplace\'s plugins', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Partial prune');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await wrapper.get('[data-testid="workspace-create-marketplace-extra-mp"]').trigger('change');
    await nextTick();

    // Materialize all three, minus `extras`.
    expect(wrapper.findAll('[data-plugin-checkbox="true"]')).toHaveLength(3);
    await wrapper.get('[data-testid="workspace-create-plugin-demo-extras"]').trigger('change');
    await nextTick();

    // Now drop `demo` entirely.
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    expect(payload.marketplaces).toEqual(['extra-mp']);
    expect(payload.pluginSelection).toEqual([{ marketplace: 'extra-mp', plugin: 'widget' }]);
  });

  /**
   * The same prune on the EDIT form, which is a separate call site (`toggleEditMarketplace`) that
   * the create-form tests do not reach at all. RED if the edit handler forgets to prune (stale refs
   * for a marketplace the workspace no longer has, which the backend rejects as unsupported_plugins)
   * or prunes everything.
   */
  it('prunes only the disabled marketplace on the edit form', async () => {
    const spanning: Workspace[] = [
      {
        id: 'ws-user',
        name: 'My Project',
        directoryRelPath: 'my-project',
        marketplaces: ['demo', 'extra-mp'],
        isSystemDefined: false,
        createdAt: 0,
        updatedAt: 0,
        pluginSelection: [
          { marketplace: 'demo', plugin: 'toolkit' },
          { marketplace: 'extra-mp', plugin: 'widget' },
        ],
        pluginsRevision: 3,
      },
    ];
    const wrapper = mountSelector({ workspaces: spanning });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.get('[data-testid="workspace-edit-marketplace-demo"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect(payload.marketplaces).toEqual(['extra-mp']);
    expect(payload.pluginSelection).toEqual([{ marketplace: 'extra-mp', plugin: 'widget' }]);
    expect(payload.pluginsRevision).toBe(3);
  });
});

describe('WorkspaceSelector edit form plugin selection', () => {
  const editable: Workspace[] = [
    {
      id: 'ws-user',
      name: 'My Project',
      directoryRelPath: 'my-project',
      marketplaces: ['demo'],
      isSystemDefined: false,
      createdAt: 0,
      updatedAt: 0,
      pluginSelection: [{ marketplace: 'demo', plugin: 'toolkit' }],
      pluginsRevision: 7,
    },
  ];

  beforeEach(() => {
    catalog.value = filteringCatalog;
  });

  /**
   * The EDIT form carries its own copy of the predicate (`editPluginsBlocked` over
   * `editMarketplaces`). A copy-paste that read the CREATE form's marketplaces instead would leave
   * the create-side tests above green while the edit form — where a stored selection can actually be
   * narrowed and saved — stayed unguarded. This pins the edit side to its own enabled set.
   *
   * Mutation proving non-vacuity: point `editPluginsBlocked` at `createMarketplaces` -> RED, because
   * the create form's marketplaces are empty here and nothing would be blocked.
   */
  it('disables edit-form plugin toggling when one of ITS enabled marketplaces is errored', async () => {
    catalog.value = {
      selected: ['demo', 'broken'],
      marketplaces: [
        { alias: 'demo', error: null, plugins: [plugin('toolkit')] },
        { alias: 'broken', error: 'clone failed: timeout', plugins: [] },
      ],
      capabilities: { pluginFiltering: true },
    };
    const withBroken: Workspace[] = [{ ...editable[0], marketplaces: ['demo', 'broken'] }];

    const wrapper = mountSelector({ workspaces: withBroken });
    await openEditForm(wrapper, 'ws-user');

    const toolkit = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-toolkit"]');
    expect(toolkit.element.disabled).toBe(true);
  });

  /**
   * Seeding + the CAS token. RED if the token is not threaded through: without `pluginsRevision`
   * the backend treats the update as revision-omitted and rejects it against the sentinel -1, so
   * every genuine selection change would 409.
   *
   * The change here (ticking `extras`) is not incidental — see the F1 block below: a save that
   * changes nothing deliberately sends NO selection at all, so a test that submitted the seeded
   * form untouched could no longer observe either key.
   */
  it('seeds from the workspace and emits the changed selection plus its revision', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    const toolkit = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-toolkit"]');
    const extras = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]');
    expect(toolkit.element.checked).toBe(true);
    expect(extras.element.checked).toBe(false);

    await extras.trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const emitted = wrapper.emitted('update-workspace')![0];
    expect(emitted[0]).toBe('ws-user');
    const payload = emitted[1] as UpdatePayload;
    expect(payload.pluginSelection).toEqual([
      { marketplace: 'demo', plugin: 'toolkit' },
      { marketplace: 'demo', plugin: 'extras' },
    ]);
    expect(payload.pluginsRevision).toBe(7);
    expect(payload.marketplaces).toEqual(['demo']);
  });

  /**
   * A workspace that never expressed a preference seeds as `null`, which renders as "everything on".
   * RED on any `?? []` in the seeding: the form would open with every box unchecked, and the first
   * plugin the user ticked would emit `[that one]` instead of "all but the one you unticked".
   */
  it('seeds an absent selection as the legacy all-on state, not as an empty list', async () => {
    const legacy: Workspace[] = [{ ...editable[0], pluginSelection: null }];
    const wrapper = mountSelector({ workspaces: legacy });
    await openEditForm(wrapper, 'ws-user');

    const toolkit = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-toolkit"]');
    const extras = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]');
    expect(toolkit.element.checked).toBe(true);
    expect(extras.element.checked).toBe(true);

    // Unticking one materializes the REST — only possible if the seed carried "all", not "none".
    await extras.trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect(payload.pluginSelection).toEqual([{ marketplace: 'demo', plugin: 'toolkit' }]);
  });

  /**
   * `null` must be reachable ON THE WIRE, not just as a seed: "Use all plugins" is the one control
   * that returns an explicit selection to the legacy state. RED if the reset writes `[]` (which
   * would disable every plugin) or if `null` is coerced anywhere on the way out.
   */
  it('emits an explicit null when the stored selection is reset to "Use all plugins"', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.get('[data-testid="workspace-edit-plugins-reset"]').trigger('click');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect(payload.pluginSelection).toBeNull();
    expect(payload.pluginsRevision).toBe(7);
  });

  /**
   * `[]` must be reachable on the wire too, and must stay distinct from `null`. RED on any
   * `|| null` / empty-means-unset normalisation: the workspace would silently go back to running
   * every plugin instead of none.
   */
  it('emits an explicit [] when the last plugin is unticked, distinct from null', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    const toolkit = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-toolkit"]');
    await toolkit.trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect(payload.pluginSelection).toEqual([]);
    expect(payload.pluginSelection).not.toBeNull();
  });

  /**
   * A stored `[]` seeds as "nothing on" — the mirror of the legacy-null case above, pinning the
   * other direction of the same coercion.
   */
  it('seeds an explicit empty selection as nothing on, not as everything on', async () => {
    const none: Workspace[] = [{ ...editable[0], pluginSelection: [] }];
    const wrapper = mountSelector({ workspaces: none });
    await openEditForm(wrapper, 'ws-user');

    const toolkit = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-toolkit"]');
    const extras = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]');
    expect(toolkit.element.checked).toBe(false);
    expect(extras.element.checked).toBe(false);
  });

  /**
   * The four-state contract's fourth state, at the component boundary. RED if `submitEdit` sets
   * `pluginSelection` unconditionally: a marketplace-only edit against a gateway that cannot filter
   * plugins would then clobber a stored selection the form never rendered.
   */
  it('omits pluginSelection entirely when the gateway cannot filter plugins', async () => {
    catalog.value = defaultCatalog;
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect('pluginSelection' in payload).toBe(false);
    expect('pluginsRevision' in payload).toBe(false);
    expect(payload.marketplaces).toEqual(['demo']);
  });

  it('renders indeterminate for a workspace stored with a partial selection', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    const marketplace = wrapper.get<HTMLInputElement>(
      '[data-testid="workspace-edit-marketplace-demo"]'
    );
    expect(marketplace.element.checked).toBe(true);
    expect(marketplace.element.indeterminate).toBe(true);
  });

  it('surfaces an API error from the parent through showFormError', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    // This is the parent's catch block calling into the exposed method (ChatLayout does exactly
    // this for both the 409 conflict and the 400 unsupported_plugins).
    (wrapper.vm as unknown as { showFormError: (m: string) => void }).showFormError(
      'These plugins are not available in the selected marketplaces: demo/ghost.'
    );
    await nextTick();

    const error = wrapper.get('[data-testid="workspace-form-error"]');
    expect(error.text()).toContain('demo/ghost');
    // The form stays interactive so the user can fix the selection and retry.
    expect(
      wrapper.get<HTMLButtonElement>('[data-testid="workspace-edit-submit"]').element.disabled
    ).toBe(false);
  });
});

/**
 * F1. `pluginSelection` is FOUR-state and the backend routes on the key's PRESENCE, not its value:
 * `WorkspacesController` checks `PluginSelection.IsSet` to decide whether to migrate every live
 * sandbox session for the workspace, and `FileWorkspaceStore` bumps `pluginsRevision` off the same
 * flag rather than off the value changing. So sending the key on a save that changed no plugin
 * destroys and recreates live sessions (blocking on the idle wait, 503 if a run is mid-flight) and
 * invalidates every other tab's CAS token, for nothing.
 *
 * Mutation proving all four tests below are non-vacuous: drop the `selectionChanged` guard in
 * `submitEdit` so the key is always written -> RED in all four ("expected false to be true" on the
 * `in payload` assertions), while the "genuine change" contrast test stays green.
 */
describe('WorkspaceSelector edit form omits an unchanged plugin selection', () => {
  const editable: Workspace[] = [
    {
      id: 'ws-user',
      name: 'My Project',
      directoryRelPath: 'my-project',
      marketplaces: ['demo'],
      isSystemDefined: false,
      createdAt: 0,
      updatedAt: 0,
      pluginSelection: [{ marketplace: 'demo', plugin: 'toolkit' }],
      pluginsRevision: 7,
    },
  ];

  beforeEach(() => {
    catalog.value = twoMarketplaceCatalog;
  });

  it('omits both keys when the form is saved without touching anything', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect('pluginSelection' in payload).toBe(false);
    expect('pluginsRevision' in payload).toBe(false);
    expect(payload.marketplaces).toEqual(['demo']);
  });

  it('omits both keys for a marketplace-only edit', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.get('[data-testid="workspace-edit-marketplace-extra-mp"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    // The marketplace change IS sent — this is not a blanket "send nothing".
    expect(payload.marketplaces).toEqual(['demo', 'extra-mp']);
    expect('pluginSelection' in payload).toBe(false);
    expect('pluginsRevision' in payload).toBe(false);
  });

  /**
   * Toggling a plugin off and back on rebuilds the list in a different ORDER. The comparison is
   * order-insensitive, so this is still "unchanged". RED if `pluginSelectionEquals` degrades to a
   * positional/`JSON.stringify` comparison.
   */
  it('omits both keys when the selection is rebuilt in a different order', async () => {
    const reordered: Workspace[] = [
      {
        ...editable[0],
        pluginSelection: [
          { marketplace: 'demo', plugin: 'extras' },
          { marketplace: 'demo', plugin: 'toolkit' },
        ],
      },
    ];
    const wrapper = mountSelector({ workspaces: reordered });
    await openEditForm(wrapper, 'ws-user');

    const extras = wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]');
    await extras.trigger('change'); // -> [toolkit]
    await nextTick();
    await extras.trigger('change'); // -> [toolkit, extras], set-equal to the stored [extras, toolkit]
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect('pluginSelection' in payload).toBe(false);
    expect('pluginsRevision' in payload).toBe(false);
  });

  /**
   * `null` vs `[]` must not collapse into "unchanged". A workspace running every plugin, saved with
   * every box unticked, is a REAL change and must be sent. RED if the equality helper treats the
   * empty list and the legacy null as equal (e.g. `(a ?? []).length === (b ?? []).length`).
   */
  it('sends [] against a stored null, which is a real change', async () => {
    const legacy: Workspace[] = [{ ...editable[0], pluginSelection: null }];
    const wrapper = mountSelector({ workspaces: legacy });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.get('[data-testid="workspace-edit-plugin-demo-toolkit"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect(payload.pluginSelection).toEqual([]);
    expect(payload.pluginsRevision).toBe(7);
  });

  /** Contrast case: a genuine change still carries BOTH keys. Green under the mutation above. */
  it('sends both keys when a plugin is actually toggled', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect('pluginSelection' in payload).toBe(true);
    expect(payload.pluginsRevision).toBe(7);
  });
});

/**
 * F2. After a 409 the composable reloads the workspace list, which hands the next save a FRESH CAS
 * token. Leaving the form on the pre-conflict selection at that point is a lost-update generator:
 * one more click would pass compare-and-swap and overwrite the other writer's change. `reseedEditForm`
 * (called by ChatLayout's catch — see ChatLayout.test.ts for that wiring) re-reads the form from the
 * refreshed workspace instead, discarding the pending edit, which the conflict message announces.
 */
describe('WorkspaceSelector reseedEditForm after a revision conflict', () => {
  const stored: Workspace = {
    id: 'ws-user',
    name: 'My Project',
    directoryRelPath: 'my-project',
    marketplaces: ['demo'],
    isSystemDefined: false,
    createdAt: 0,
    updatedAt: 0,
    pluginSelection: [{ marketplace: 'demo', plugin: 'toolkit' }],
    pluginsRevision: 7,
  };

  /** What the reload returns: the OTHER writer kept only `extras`, and the revision moved on. */
  const refreshed: Workspace = {
    ...stored,
    pluginSelection: [{ marketplace: 'demo', plugin: 'extras' }],
    pluginsRevision: 8,
  };

  beforeEach(() => {
    catalog.value = filteringCatalog;
  });

  /**
   * Mutation proving non-vacuity: make `reseedEditForm` a no-op (`return;` as its first statement)
   * -> RED. The form keeps the pending `[toolkit, extras]`, which differs from the refreshed
   * `[extras]`, so the save emits `pluginSelection: [toolkit, extras]` with the now-VALID revision 8
   * — i.e. exactly the silent overwrite this exists to prevent.
   */
  it('re-reads the form from the refreshed workspace, so the next save carries no stale selection', async () => {
    const wrapper = mountSelector({ workspaces: [stored] });
    await openEditForm(wrapper, 'ws-user');

    // The user's pending edit: tick `extras` as well.
    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await nextTick();

    // First Save — this is the one the backend answers with 409.
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();
    expect(
      (wrapper.emitted('update-workspace')![0][1] as UpdatePayload).pluginsRevision
    ).toBe(7);

    // The 409 path, in the parent's order: the list is reloaded (prop update), the form is
    // re-seeded, and only then is the error shown (which also clears the in-flight lock).
    await wrapper.setProps({ workspaces: [refreshed] });
    const vm = wrapper.vm as unknown as {
      reseedEditForm: () => void;
      showFormError: (m: string) => void;
    };
    vm.reseedEditForm();
    vm.showFormError('This workspace was changed elsewhere…');
    await nextTick();

    // The form now shows what is actually stored, not what the user had pending.
    const toolkit = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-toolkit"]');
    const extras = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]');
    expect(toolkit.element.checked).toBe(false);
    expect(extras.element.checked).toBe(true);

    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    // ...and a second Save is a no-op on the selection: it cannot clobber the other writer.
    const payload = wrapper.emitted('update-workspace')![1][1] as UpdatePayload;
    expect('pluginSelection' in payload).toBe(false);
    expect('pluginsRevision' in payload).toBe(false);
  });

  /**
   * Guarded so a stray call cannot wipe an edit in progress. RED if the `formMode === 'edit'` guard
   * is dropped: the re-seed would fire while the CREATE form is open and overwrite `editMarketplaces`
   * from whatever `editWorkspaceId` happened to hold.
   */
  it('does nothing when the edit form is not open', async () => {
    const wrapper = mountSelector({ workspaces: [stored] });
    await openEditForm(wrapper, 'ws-user');
    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-cancel"]').trigger('click');
    await nextTick();

    (wrapper.vm as unknown as { reseedEditForm: () => void }).reseedEditForm();
    await nextTick();

    // The form is closed and stays closed — no state was touched, nothing was rendered.
    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(false);

    // Re-opening still seeds from the workspace itself, unaffected by the stray call.
    await wrapper.get('[data-testid="workspace-edit-ws-user"]').trigger('click');
    await flushPromises();
    await nextTick();
    const extras = wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]');
    expect(extras.element.checked).toBe(false);
  });
});

/**
 * F5. "Use all plugins" is `v-if`'d on the very state its own `@click` clears, so the element is
 * gone by the time the click reaches the document listener — the browser runs a microtask
 * checkpoint between listener invocations and Vue's scheduler flushes there. `handleClickOutside`
 * then measured a DETACHED node with `dropdownRef.contains(...)`, got `false`, and closed the whole
 * dropdown: the form vanished and the reset was never saved. Confirmed against the running build by
 * a browser probe (0 PUTs, form gone), which is also why `null` — the legacy "all plugins" state —
 * was unreachable through the UI at all.
 *
 * The existing tri-state test passed throughout because it asserted only the emitted payload and
 * never that the dropdown survived the click.
 *
 * HONEST LIMIT OF THIS TEST: jsdom does NOT perform the between-listener microtask checkpoint that
 * a browser does, so a plain `trigger('click')` leaves the button still connected when the document
 * listener runs — it cannot reproduce the defect, with or without the fix. These tests therefore
 * emulate the re-render explicitly: a listener registered after Vue's removes the node during the
 * same propagation (jsdom keeps walking the path captured at dispatch), which is exactly what the
 * real re-render does at that point. What is emulated is the TIMING of the removal; the code under
 * test — `handleClickOutside` receiving a detached target — is the real thing.
 */
describe('WorkspaceSelector self-removing controls do not close the dropdown (F5)', () => {
  beforeEach(() => {
    catalog.value = filteringCatalog;
  });

  /** Clicks the element the way a browser delivers it once Vue has already re-rendered it away. */
  function clickAndDetach(element: Element) {
    element.addEventListener('click', () => element.remove());
    element.dispatchEvent(new MouseEvent('click', { bubbles: true }));
  }

  /**
   * Mutation proving non-vacuity: delete the `!target.isConnected` bail-out from
   * `handleClickOutside` -> RED in both tests below (the form unmounts, so `.get(...)` throws).
   */
  it('keeps the create form open when "Use all plugins" removes itself', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Reset survives');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-plugin-demo-extras"]').trigger('change');
    await nextTick();

    clickAndDetach(wrapper.get('[data-testid="workspace-create-plugins-reset"]').element);
    await nextTick();

    // The dropdown and the form are STILL MOUNTED — the click was inside, whatever `contains` said.
    expect(wrapper.find('.dropdown-menu').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-form"]').exists()).toBe(true);
    // The reset itself took effect: every box is ticked again and the link is gone.
    expect(wrapper.find('[data-testid="workspace-create-plugins-reset"]').exists()).toBe(false);
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-create-plugin-demo-extras"]').element
        .checked
    ).toBe(true);

    // ...and the user can still reach Save, so `null` is actually reachable through the UI.
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    expect(payload.pluginSelection).toBeNull();
  });

  it('keeps the edit form open when "Use all plugins" removes itself', async () => {
    const editable: Workspace[] = [
      {
        id: 'ws-user',
        name: 'My Project',
        directoryRelPath: 'my-project',
        marketplaces: ['demo'],
        isSystemDefined: false,
        createdAt: 0,
        updatedAt: 0,
        pluginSelection: [{ marketplace: 'demo', plugin: 'toolkit' }],
        pluginsRevision: 7,
      },
    ];
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    clickAndDetach(wrapper.get('[data-testid="workspace-edit-plugins-reset"]').element);
    await nextTick();

    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);

    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    // The `null` half of the tri-state round trip, end to end through the actual control.
    const payload = wrapper.emitted('update-workspace')![0][1] as UpdatePayload;
    expect(payload.pluginSelection).toBeNull();
    expect(payload.pluginsRevision).toBe(7);
  });

  /**
   * The guard must not stop the dropdown closing on a genuine outside click. RED if the bail-out is
   * widened (e.g. `return` unconditionally, or a truthiness test on `target`).
   */
  it('still closes on a genuine outside click', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);
    expect(wrapper.find('[data-testid="workspace-create-form"]').exists()).toBe(true);

    const outside = document.createElement('button');
    document.body.appendChild(outside);
    outside.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await nextTick();

    expect(wrapper.find('.dropdown-menu').exists()).toBe(false);
    outside.remove();
  });
});

/**
 * P1. A marketplace whose plugins the gateway could not enumerate reports `error` non-null and an
 * EMPTY plugin list. That is indistinguishable, in the DOM, from a marketplace that genuinely has no
 * plugins — and the difference matters: materializing a selection while such a marketplace is enabled
 * writes down every OTHER marketplace's plugins and silently leaves this one's out, so the workspace
 * loses them. The client cannot enumerate what the gateway failed to list, so the most it can
 * correctly do is say so.
 */
describe('WorkspaceSelector marketplace plugin load errors', () => {
  beforeEach(() => {
    catalog.value = {
      selected: ['demo', 'broken'],
      marketplaces: [
        { alias: 'demo', error: null, plugins: [plugin('toolkit')] },
        { alias: 'broken', error: 'clone failed: timeout', plugins: [] },
      ],
      capabilities: { pluginFiltering: true },
    };
  });

  it('explains an errored marketplace instead of rendering it as having no plugins', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    // Nothing is claimed before the marketplace is even enabled.
    expect(wrapper.find('[data-testid="workspace-create-plugins-error-broken"]').exists()).toBe(
      false
    );

    await wrapper.get('[data-testid="workspace-create-marketplace-broken"]').trigger('change');
    await nextTick();

    const notice = wrapper.get('[data-testid="workspace-create-plugins-error-broken"]');
    expect(notice.text()).toContain('could not be listed');
    // A healthy marketplace never shows it.
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    expect(wrapper.find('[data-testid="workspace-create-plugins-error-demo"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="workspace-create-plugins-demo"]').exists()).toBe(true);
  });

  /**
   * Saying so is necessary but NOT sufficient. The notice tells the user the marketplace could not be
   * listed; it does not stop a save from silently narrowing their plugin set. Toggling a plugin of the
   * HEALTHY marketplace materializes the legacy `null` through `allPluginsOf`, which enumerates
   * `m.plugins` — empty for the errored one — so the saved explicit list omits every plugin of
   * `broken`, and keeps omitting them after its catalog recovers.
   *
   * ONE predicate (`createPluginsBlocked`) is enforced at TWO points: the checkbox is disabled, and
   * `toggleCreatePlugin` returns early. They are proven separately below because either alone keeps
   * this case green — a single test covering both cannot fail when only one is removed.
   *
   * Mutation proving non-vacuity: `:disabled="false && createPluginsBlocked"` -> RED here.
   */
  it('disables plugin toggling while an enabled marketplace could not be listed', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await wrapper.get('[data-testid="workspace-create-marketplace-broken"]').trigger('change');
    await nextTick();

    // The healthy marketplace's checkbox still renders — inert, not hidden, so the user can see what
    // they would be choosing while the adjacent notice explains why they cannot yet.
    const box = wrapper.get('[data-testid="workspace-create-plugin-demo-toolkit"]');
    expect((box.element as HTMLInputElement).disabled).toBe(true);
    expect(wrapper.get('[data-testid="workspace-create-plugins-error-broken"]').text()).toContain(
      'would drop'
    );
  });

  /**
   * The second enforcement point, proven with the disabled attribute deliberately bypassed: a native
   * `dispatchEvent` reaches the handler even on a disabled input (Vue Test Utils' own `trigger()`
   * skips it, which is why this case cannot use it). Without the early return the legacy `null` would
   * materialize into a list built only from what enumerated — dropping `broken`'s plugins.
   *
   * Mutation proving non-vacuity: remove `if (createPluginsBlocked.value) return;` from
   * `toggleCreatePlugin` -> RED, the payload becomes an explicit list instead of `null`.
   */
  it('keeps the legacy null selection when a blocked plugin toggle is dispatched anyway', async () => {
    const wrapper = mountSelector();
    await openCreateForm(wrapper);

    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Errored');
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await wrapper.get('[data-testid="workspace-create-marketplace-broken"]').trigger('change');
    await nextTick();

    const box = wrapper.get('[data-testid="workspace-create-plugin-demo-toolkit"]');
    box.element.dispatchEvent(new Event('change'));
    await nextTick();

    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();

    const payload = wrapper.emitted('create-workspace')![0][0] as CreatePayload;
    // The legacy "all plugins" state survives instead of a list that silently omits `broken`.
    expect(payload.pluginSelection).toBeNull();
  });
});

/**
 * `isLoading` (a list refresh in flight) and `disabled` (gateway down / streaming / locked) must NOT
 * behave the same way. Both block interaction — acting on a mid-refresh list is unsafe — but only
 * `disabled` may tear the dropdown down. See the F6 case in ChatLayout.test.ts for what a teardown on
 * the transient flag destroyed.
 */
describe('WorkspaceSelector transient loading vs terminal disabled', () => {
  // The shared `workspaces` fixture omits `compatibility`, which disables every option row for an
  // unrelated reason — an assertion about `isLoading` written against it passes no matter what the
  // guard says. (Found by mutation: reverting the guard left that version of this test green.)
  const selectable: Workspace[] = [
    { ...workspaces[1], compatibility: 'compatible' },
  ] as Workspace[];

  it('blocks selection while loading without closing an open dropdown or form', async () => {
    const wrapper = mountSelector({ workspaces: selectable });
    await openDropdown(wrapper);
    await wrapper.get('[data-testid="workspace-edit-ws-user"]').trigger('click');
    await nextTick();
    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);

    await wrapper.setProps({ isLoading: true });
    await nextTick();

    // Still there: a refresh is not a reason to throw the user's form away.
    expect(wrapper.find('.dropdown-menu').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);

    await wrapper.get('[data-testid="workspace-edit-cancel"]').trigger('click');
    await nextTick();

    // But it IS a reason not to act on the list.
    const option = wrapper.get('[data-testid="workspace-option-ws-user"]');
    expect(option.attributes('disabled')).toBeDefined();
    await option.trigger('click');
    expect(wrapper.emitted('select-workspace')).toBeUndefined();

    // Non-vacuity: the SAME click on the SAME row emits once the refresh finishes, so the block
    // above is attributable to `isLoading` and to nothing else.
    await wrapper.setProps({ isLoading: false });
    await nextTick();
    const enabled = wrapper.get('[data-testid="workspace-option-ws-user"]');
    expect(enabled.attributes('disabled')).toBeUndefined();
    await enabled.trigger('click');
    expect(wrapper.emitted('select-workspace')).toHaveLength(1);
  });

  it('closes the dropdown when disabled becomes true', async () => {
    const wrapper = mountSelector();
    await openDropdown(wrapper);
    expect(wrapper.find('.dropdown-menu').exists()).toBe(true);

    await wrapper.setProps({ disabled: true });
    await nextTick();

    expect(wrapper.find('.dropdown-menu').exists()).toBe(false);
  });
});

/**
 * A refetch in flight is TRANSIENT: it flips true and back inside one user operation (the 409
 * conflict path awaits `loadWorkspaces()` before it reseeds the form and shows its error). That is
 * why these tests assert the form is STILL MOUNTED — the transient flag may block interaction, but
 * the moment it tears the form down it destroys the very handler that raised it, and the user's
 * edit is discarded with nothing rendered. Teardown stays keyed off the terminal set alone.
 *
 * They also assert on rendered DOM and emitted events rather than on spies: a spy records a call
 * whether or not the subtree still exists, so a spy-based version of this suite is green against a
 * torn-down form.
 */
describe('WorkspaceSelector blocks form interaction during a transient refresh', () => {
  const editable: Workspace[] = [
    {
      id: 'ws-user',
      name: 'My Project',
      directoryRelPath: 'my-project',
      marketplaces: ['demo'],
      isSystemDefined: false,
      createdAt: 0,
      updatedAt: 0,
      pluginSelection: [{ marketplace: 'demo', plugin: 'toolkit' }],
      pluginsRevision: 7,
    },
  ];

  beforeEach(() => {
    catalog.value = filteringCatalog;
  });

  it('disables create-form controls while isLoading and drops a submit attempted in that window', async () => {
    const wrapper = mountSelector();
    await openDropdown(wrapper);
    await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
    await nextTick();
    await wrapper.get('[data-testid="workspace-create-name"]').setValue('Demo Workspace');
    await nextTick();

    await wrapper.setProps({ isLoading: true });
    await nextTick();

    const marketplace = wrapper.get<HTMLInputElement>('[data-testid="workspace-create-marketplace-demo"]');
    expect(marketplace.element.disabled).toBe(true);
    expect(
      wrapper.get<HTMLButtonElement>('[data-testid="workspace-create-submit"]').element.disabled
    ).toBe(true);

    // The handlers guard independently of the attribute: a submit dispatched despite the disabled
    // button (Enter in a text field, a stale event) must still be dropped.
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await nextTick();
    expect(wrapper.emitted('create-workspace')).toBeFalsy();

    // The form is still there — blocked, not dismantled.
    expect(wrapper.find('[data-testid="workspace-create-form"]').exists()).toBe(true);
  });

  it('disables edit-form controls while isLoading and drops a submit attempted in that window', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.setProps({ isLoading: true });
    await nextTick();

    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-marketplace-demo"]').element.disabled
    ).toBe(true);
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]').element.disabled
    ).toBe(true);
    expect(
      wrapper.get<HTMLButtonElement>('[data-testid="workspace-edit-submit"]').element.disabled
    ).toBe(true);

    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();
    expect(wrapper.emitted('update-workspace')).toBeFalsy();

    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);
  });

  /**
   * A toggle applied mid-refresh would be written back against a revision the user never saw. The
   * checkbox is disabled, but the handler must refuse too — `change` still fires from a programmatic
   * dispatch, and the emitted payload is what actually reaches the server.
   */
  it('ignores a plugin toggle dispatched while isLoading, leaving the selection intact', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.setProps({ isLoading: true });
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await nextTick();

    // Clearing the transient flag must restore the form to exactly the state it was blocked in —
    // not to a state that absorbed the blocked toggle.
    await wrapper.setProps({ isLoading: false });
    await nextTick();

    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]').element.checked
    ).toBe(false);

    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const emitted = wrapper.emitted('update-workspace');
    expect(emitted).toBeTruthy();
    expect((emitted![0][1] as UpdatePayload).pluginSelection).toBeUndefined();
  });

  /**
   * The whole point of blocking rather than tearing down: once the refetch settles, the same form
   * the user was typing into is still usable. RED if the transient flag ever reaches the teardown
   * watcher.
   */
  it('restores full interaction once the transient refresh settles', async () => {
    const wrapper = mountSelector({ workspaces: editable });
    await openEditForm(wrapper, 'ws-user');

    await wrapper.setProps({ isLoading: true });
    await nextTick();
    await wrapper.setProps({ isLoading: false });
    await nextTick();

    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-marketplace-demo"]').element.disabled
    ).toBe(false);

    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await nextTick();
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await nextTick();

    const emitted = wrapper.emitted('update-workspace');
    expect(emitted).toBeTruthy();
    expect((emitted![0][1] as UpdatePayload).pluginSelection).toEqual([
      { marketplace: 'demo', plugin: 'toolkit' },
      { marketplace: 'demo', plugin: 'extras' },
    ]);
  });
});

/**
 * The catalog is fetched on mount AND whenever a create/edit form opens, so two requests are easily
 * in flight at once. These resolve them OUT OF ORDER — the mount request lands last — because that
 * is the only ordering that tells a sequenced implementation from an unsequenced one.
 *
 * What makes this more than cosmetic: the response sets `pluginFilteringEnabled`, which decides
 * whether the plugin UI renders and whether the next submit carries a `pluginSelection` key at all.
 * A stale response therefore changes the PAYLOAD, not just the paint.
 */
describe('WorkspaceSelector concurrent catalog load ordering', () => {
  beforeEach(() => {
    vi.mocked(listMarketplaces).mockReset();
  });

  afterEach(() => {
    // Hand the shared module mock back to the default the rest of the file relies on.
    vi.mocked(listMarketplaces).mockImplementation(async () => catalog.value as never);
  });

  function deferredCatalog() {
    let release!: (value: unknown) => void;
    const promise = new Promise((resolve) => {
      release = resolve;
    });
    return { promise, release };
  }

  it('ignores a stale catalog response that lands after a newer one', async () => {
    const mountLoad = deferredCatalog();
    const formLoad = deferredCatalog();
    vi.mocked(listMarketplaces)
      .mockReturnValueOnce(mountLoad.promise as never)
      .mockReturnValueOnce(formLoad.promise as never);

    const wrapper = mountSelector();
    await openDropdown(wrapper);
    await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
    await nextTick();

    // The form's request answers first, advertising plugin filtering.
    formLoad.release(filteringCatalog);
    await flushPromises();
    await nextTick();

    // Enable the marketplace so its plugin list renders — that list is the visible proof of
    // `pluginFilteringEnabled`, which is the field the stale response would flip.
    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    expect(wrapper.find('[data-testid="workspace-create-plugin-demo-toolkit"]').exists()).toBe(true);

    // The mount request lands afterwards with a catalog that has no capability block — the "older
    // gateway" shape. Applying it would tear the plugin UI out from under the open form and drop
    // pluginSelection from the next submit.
    mountLoad.release(uncapableCatalog);
    await flushPromises();
    await nextTick();

    expect(wrapper.find('[data-testid="workspace-create-marketplace-demo"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-plugin-demo-toolkit"]').exists()).toBe(true);
  });

  it('ignores a stale catalog rejection that lands after a newer success', async () => {
    let failMount!: (e: Error) => void;
    const mountLoad = new Promise((_, reject) => {
      failMount = reject;
    });
    const formLoad = deferredCatalog();
    vi.mocked(listMarketplaces)
      .mockReturnValueOnce(mountLoad as never)
      .mockReturnValueOnce(formLoad.promise as never);

    const wrapper = mountSelector();
    await openDropdown(wrapper);
    await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
    await nextTick();

    formLoad.release(filteringCatalog);
    await flushPromises();
    await nextTick();

    await wrapper.get('[data-testid="workspace-create-marketplace-demo"]').trigger('change');
    await nextTick();
    expect(wrapper.find('[data-testid="workspace-create-plugin-demo-toolkit"]').exists()).toBe(true);

    // The stale catch would blank availableMarketplaces and clear pluginFilteringEnabled, emptying
    // a form the user is looking at.
    failMount(new Error('stale catalog failure'));
    await flushPromises();
    await nextTick();

    expect(wrapper.find('[data-testid="workspace-create-marketplace-demo"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-create-plugin-demo-toolkit"]').exists()).toBe(true);
  });
});
