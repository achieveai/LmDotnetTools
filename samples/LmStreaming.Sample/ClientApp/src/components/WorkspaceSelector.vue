<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue';
import type {
  Workspace,
  WorkspaceCreate,
  WorkspaceUpdate,
  MarketplaceDescriptor,
  WorkspaceGateway,
  PluginRef,
} from '@/types/workspace';
import { listMarketplaces, MarketplaceGatewayUnavailableError } from '@/api/marketplacesApi';

const props = defineProps<{
  workspaces: Workspace[];
  gateway?: WorkspaceGateway | null;
  selectedWorkspaceId: string | null;
  /**
   * Workspace id locked to the current thread (set after the first message).
   * When provided, the selector renders as a read-only badge instead of a
   * dropdown.
   */
  lockedWorkspaceId?: string | null;
  /**
   * TRANSIENT busy: the workspace list is being (re)fetched. Blocks every action that would read
   * the list — it is momentarily stale — but must NEVER tear the dropdown down, because it flips
   * true and back within a single operation the user is in the middle of.
   *
   * The distinction from {@link disabled} is load-bearing, not stylistic. A post-409 reload raises
   * this flag while the parent is still on its way to re-seed the edit form and show the conflict
   * message. Treating that flip as a teardown unmounted the form first and made both silent (F6).
   */
  isLoading?: boolean;
  /**
   * TERMINAL unavailability: the gateway is down, a run is streaming, the thread is locked. The
   * dropdown is closed and any open form discarded, because the condition is not about to reverse
   * on its own within the user's current action. See {@link isLoading} for the transient case.
   */
  disabled?: boolean;
}>();

/** Any reason not to act on the workspace list right now — transient or terminal. */
const interactionBlocked = computed(() => props.disabled === true || props.isLoading === true);

const emit = defineEmits<{
  'select-workspace': [workspaceId: string];
  'create-workspace': [data: WorkspaceCreate];
  'update-workspace': [workspaceId: string, data: WorkspaceUpdate];
}>();

type FormMode = 'none' | 'create' | 'edit';

const dropdownOpen = ref(false);
const dropdownRef = ref<HTMLElement | null>(null);

const formMode = ref<FormMode>('none');
const formError = ref<string | null>(null);
const submitting = ref(false);

// Create form state
const createName = ref('');
const createDirectory = ref('');
const directoryTouched = ref(false);
const createMarketplaces = ref<string[]>([]);
/** Tri-state, exactly as on the wire — `null` = legacy "all plugins", `[]` = none. Never `?? []`. */
const createPluginSelection = ref<PluginRef[] | null>(null);

// Edit form state
const editWorkspaceId = ref<string | null>(null);
const editMarketplaces = ref<string[]>([]);
/** Tri-state, seeded from the workspace being edited. See {@link createPluginSelection}. */
const editPluginSelection = ref<PluginRef[] | null>(null);

// Marketplace options sourced from the live gateway catalog (GET /api/marketplaces), replacing the
// former static [core, community] seed. Empty when the gateway is offline (marketplacesUnavailable).
const availableMarketplaces = ref<MarketplaceDescriptor[]>([]);
const marketplacesUnavailable = ref(false);
/**
 * Whether the per-plugin UI is shown at all. FAIL CLOSED: only an explicit
 * `capabilities.pluginFiltering === true` enables it. A gateway that reports `false`, reports no
 * capability block at all (older build → `null`/absent), or cannot be reached renders exactly
 * today's marketplace-only form and sends exactly today's payload.
 */
const pluginFilteringEnabled = ref(false);

/**
 * Monotonic id of the most recently STARTED catalog load. This runs on mount AND on every
 * create/edit form open, so two are easily in flight at once. An earlier response landing last would
 * repaint the plugin options and — through `pluginFilteringEnabled` — change whether the very next
 * submit carries a `pluginSelection` key at all. Ordering the writes is not optional here.
 */
let marketplaceGeneration = 0;

async function loadAvailableMarketplaces(): Promise<void> {
  const generation = ++marketplaceGeneration;
  try {
    const catalog = await listMarketplaces();
    if (generation !== marketplaceGeneration) return;
    availableMarketplaces.value = catalog.marketplaces.map((m) => ({
      id: m.alias,
      displayName: m.alias,
      // Defaulting the CATALOG (what exists) to empty is safe and unrelated to the selection
      // tri-state: a marketplace that failed to load offers nothing to choose from.
      plugins: (m.plugins ?? []).map((p) => p.name),
      error: m.error ?? null,
    }));
    pluginFilteringEnabled.value = catalog.capabilities?.pluginFiltering === true;
    marketplacesUnavailable.value = false;
  } catch (e) {
    if (generation !== marketplaceGeneration) return;
    availableMarketplaces.value = [];
    pluginFilteringEnabled.value = false;
    marketplacesUnavailable.value = e instanceof MarketplaceGatewayUnavailableError;
    if (!(e instanceof MarketplaceGatewayUnavailableError)) {
      console.error('Failed to load marketplaces:', e);
    }
  }
}

// --- Plugin selection (pure helpers, shared by the create and edit forms) ------------------

function hasPlugin(selection: PluginRef[], marketplace: string, plugin: string): boolean {
  return selection.some((ref) => ref.marketplace === marketplace && ref.plugin === plugin);
}

/** Every plugin of every enabled marketplace — the explicit spelling of the legacy `null` state. */
function allPluginsOf(marketplaceIds: string[]): PluginRef[] {
  const enabled = new Set(marketplaceIds);
  return availableMarketplaces.value
    .filter((m) => enabled.has(m.id))
    .flatMap((m) => m.plugins.map((plugin) => ({ marketplace: m.id, plugin })));
}

/**
 * Whether any ENABLED marketplace failed to list its plugins. This blocks plugin toggling, and the
 * reason is subtle: `allPluginsOf` builds the materialized selection out of `m.plugins`, and a
 * marketplace whose catalog failed carries `plugins: []`. Materializing the legacy `null` while one
 * is errored would therefore write down an explicit list that OMITS every plugin of that
 * marketplace — silently narrowing the workspace's plugin set to whatever happened to enumerate,
 * and keeping it narrowed after the catalog recovers. A disabled checkbox (next to the existing
 * per-marketplace error) is honest; a save that quietly drops plugins is not.
 *
 * Marketplace-only edits stay available while this is true: `submitEdit` omits `pluginSelection`
 * when it has not changed, and the backend's four-state contract leaves the stored selection alone.
 */
function hasErroredEnabledMarketplace(marketplaceIds: string[]): boolean {
  const enabled = new Set(marketplaceIds);
  return availableMarketplaces.value.some((m) => enabled.has(m.id) && m.error);
}

// `interactionBlocked` folds in here too: a refetch in flight means the catalog and the workspace
// revision under this form are both being replaced, so a toggle applied now would be written back
// against state the user never saw. Blocking is the TRANSIENT response — the form stays mounted and
// the watcher that tears it down still keys off the terminal set only. See the note at that watcher.
const createPluginsBlocked = computed(
  () => interactionBlocked.value || hasErroredEnabledMarketplace(createMarketplaces.value)
);
const editPluginsBlocked = computed(
  () => interactionBlocked.value || hasErroredEnabledMarketplace(editMarketplaces.value)
);

/**
 * Whether a plugin checkbox renders checked. A `null` selection means the workspace expressed no
 * preference, which the gateway reads as "all plugins of the enabled marketplaces" — so every box
 * is checked, truthfully. Plugins of a marketplace that is not enabled are never on.
 */
function isPluginChecked(
  selection: PluginRef[] | null,
  marketplaceIds: string[],
  marketplace: string,
  plugin: string
): boolean {
  if (!marketplaceIds.includes(marketplace)) return false;
  if (selection === null) return true;
  return hasPlugin(selection, marketplace, plugin);
}

/**
 * Whether the marketplace's own checkbox renders indeterminate — i.e. SOME but not all of its
 * plugins are selected. Never indeterminate under `null` (that is "all", a determinate state).
 */
function isMarketplaceIndeterminate(
  selection: PluginRef[] | null,
  marketplaceIds: string[],
  marketplace: MarketplaceDescriptor
): boolean {
  if (selection === null || !marketplaceIds.includes(marketplace.id)) return false;
  if (marketplace.plugins.length === 0) return false;
  const chosen = marketplace.plugins.filter((p) => hasPlugin(selection, marketplace.id, p)).length;
  return chosen > 0 && chosen < marketplace.plugins.length;
}

/**
 * Toggles one plugin, materializing the legacy `null` state on the user's first explicit choice:
 * `null` means "all plugins of the enabled marketplaces", so turning ONE off first requires
 * writing down the rest — there is nothing to subtract from otherwise. Always returns an explicit
 * list (possibly `[]`, meaning no plugins), never `null`.
 */
function togglePluginIn(
  selection: PluginRef[] | null,
  marketplaceIds: string[],
  marketplace: string,
  plugin: string
): PluginRef[] {
  const base = selection === null ? allPluginsOf(marketplaceIds) : selection;
  return hasPlugin(base, marketplace, plugin)
    ? base.filter((ref) => !(ref.marketplace === marketplace && ref.plugin === plugin))
    : [...base, { marketplace, plugin }];
}

/**
 * Drops a disabled marketplace's plugins from an explicit selection. Only ever called on REMOVAL:
 * ENABLING a marketplace deliberately leaves `null` alone, because enumerating "all its plugins"
 * is a different wire value that would stop the workspace picking up plugins the marketplace gains
 * later.
 */
function pruneMarketplaceFrom(
  selection: PluginRef[] | null,
  marketplace: string
): PluginRef[] | null {
  if (selection === null) return null;
  return selection.filter((ref) => ref.marketplace !== marketplace);
}

/**
 * Copies a persisted selection into form state, preserving all three states EXPLICITLY. Written as
 * two `=== null`/`=== undefined` identity tests rather than a truthiness test on purpose: `[]` is
 * truthy in JS, so `x ? [...x] : null` happens to be correct — but only by that quirk, and any later
 * rewrite to `x?.length`, `Array.isArray(x) && x.length`, or `x?.length > 0` would silently collapse
 * "explicitly no plugins" back into "all plugins". An absent field (a backend predating the feature)
 * means the same as null: no preference.
 */
function seedSelection(stored: PluginRef[] | null | undefined): PluginRef[] | null {
  if (stored === null || stored === undefined) return null;
  return [...stored];
}

/**
 * Stable, order-insensitive identity for one plugin ref. JSON-encodes the PAIR rather than
 * concatenating with a separator: no printable delimiter is guaranteed absent from a marketplace
 * alias or a plugin name, so `a|b` + `c` and `a` + `b|c` could otherwise collide. (The previous
 * spelling used a NUL byte, which is collision-free but makes the whole file read as BINARY to git,
 * so the diff is unreviewable.)
 */
function refKey(ref: PluginRef): string {
  return JSON.stringify([ref.marketplace, ref.plugin]);
}

/**
 * Tri-state equality between a form selection and a persisted one. `null` equals only `null` (never
 * `[]`). Two lists are equal when they hold the same refs regardless of ORDER, so merely reordering
 * is not mistaken for a change. Sorted key comparison rather than a Set, so a list containing
 * duplicates cannot compare equal to one that does not.
 */
function pluginSelectionEquals(a: PluginRef[] | null, b: PluginRef[] | null): boolean {
  if (a === null || b === null) return a === null && b === null;
  if (a.length !== b.length) return false;
  const left = a.map(refKey).sort();
  const right = b.map(refKey).sort();
  return left.every((key, i) => key === right[i]);
}


const isLocked = computed(() => !!props.lockedWorkspaceId);

const lockedWorkspace = computed<Workspace | null>(() => {
  if (!props.lockedWorkspaceId) return null;
  return (
    props.workspaces.find((w) => w.id === props.lockedWorkspaceId) ?? {
      id: props.lockedWorkspaceId,
      name: props.lockedWorkspaceId,
      directoryRelPath: '',
      marketplaces: [],
      isSystemDefined: false,
      createdAt: 0,
      updatedAt: 0,
      compatibility: 'unknown',
      unsupportedMarketplaces: [],
    }
  );
});

const selectedWorkspace = computed<Workspace | null>(() =>
  props.workspaces.find((w) => w.id === props.selectedWorkspaceId) ?? null
);

const systemWorkspaces = computed(() => props.workspaces.filter((w) => w.isSystemDefined));
const userWorkspaces = computed(() => props.workspaces.filter((w) => !w.isSystemDefined));

const editWorkspace = computed<Workspace | null>(() =>
  props.workspaces.find((w) => w.id === editWorkspaceId.value) ?? null
);

function toggleDropdown(): void {
  if (interactionBlocked.value || isLocked.value) {
    return;
  }
  dropdownOpen.value = !dropdownOpen.value;
}

function closeDropdown(): void {
  dropdownOpen.value = false;
  closeForm();
}

function closeForm(): void {
  formMode.value = 'none';
  formError.value = null;
  submitting.value = false;
}

function handleSelect(workspaceId: string): void {
  // `isLoading` blocks here too: switching to a workspace picked out of a list that is mid-refresh
  // can act on an entry the server has already changed or removed.
  if (interactionBlocked.value || isLocked.value) {
    return;
  }
  emit('select-workspace', workspaceId);
  closeDropdown();
}

// --- Create form ---------------------------------------------------------

/**
 * Derives a directory-friendly slug from the raw workspace name so the
 * directory input can stay in sync until the user edits it directly.
 */
function slugify(raw: string): string {
  return raw
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

function openCreateForm(): void {
  if (interactionBlocked.value) return;
  formMode.value = 'create';
  formError.value = null;
  createName.value = '';
  createDirectory.value = '';
  directoryTouched.value = false;
  createMarketplaces.value = [];
  // A new workspace starts with no preference, i.e. legacy "all plugins" — NOT "no plugins".
  createPluginSelection.value = null;
  void loadAvailableMarketplaces();
}

watch(createName, (name) => {
  if (formMode.value === 'create' && !directoryTouched.value) {
    createDirectory.value = slugify(name);
  }
});

function onDirectoryInput(): void {
  directoryTouched.value = true;
}

function toggleCreateMarketplace(id: string): void {
  if (interactionBlocked.value) return;
  const idx = createMarketplaces.value.indexOf(id);
  if (idx >= 0) {
    createMarketplaces.value.splice(idx, 1);
    createPluginSelection.value = pruneMarketplaceFrom(createPluginSelection.value, id);
  } else {
    createMarketplaces.value.push(id);
  }
}

function toggleCreatePlugin(marketplace: string, plugin: string): void {
  // Belt-and-suspenders behind the disabled checkbox: never materialize a selection that would
  // silently omit an unlistable marketplace's plugins. See hasErroredEnabledMarketplace.
  if (createPluginsBlocked.value) return;
  createPluginSelection.value = togglePluginIn(
    createPluginSelection.value,
    createMarketplaces.value,
    marketplace,
    plugin
  );
}

/** Returns to the legacy `null` state ("all plugins"), the one state a checkbox cannot reach. */
function resetCreatePlugins(): void {
  createPluginSelection.value = null;
}

function submitCreate(): void {
  if (submitting.value || interactionBlocked.value) return;
  formError.value = null;
  const name = createName.value.trim();
  if (!name) {
    formError.value = 'Name is required';
    return;
  }
  const directory = createDirectory.value.trim();
  const payload: WorkspaceCreate = {
    name,
    directoryRelPath: directory || undefined,
    marketplaces: [...createMarketplaces.value],
  };
  if (pluginFilteringEnabled.value) {
    // Tri-state passthrough: `null` stays `null`, `[]` stays `[]`. When the gateway cannot filter
    // plugins the key is left ABSENT entirely, so the request is what this form sent before
    // per-plugin selection existed.
    payload.pluginSelection =
      createPluginSelection.value === null ? null : [...createPluginSelection.value];
  }
  // Keep the form open and mark it in-flight. The parent awaits the API call and
  // calls closeForm() on success or showFormError() on failure (which re-renders
  // the inline error). Closing here would unmount the error element before the
  // awaited rejection arrives, silently swallowing the message.
  submitting.value = true;
  emit('create-workspace', payload);
}

// --- Edit form -----------------------------------------------------------

function openEditForm(workspace: Workspace): void {
  // Seeding a form from a list that is mid-refresh would capture a stale `pluginsRevision`, so the
  // very first save would 409. Blocked while loading for the same reason as handleSelect.
  if (interactionBlocked.value || workspace.isSystemDefined) return;
  formMode.value = 'edit';
  formError.value = null;
  editWorkspaceId.value = workspace.id;
  seedEditFormFrom(workspace);
  void loadAvailableMarketplaces();
}

/** Writes a workspace's persisted marketplaces + plugin selection into the edit form's state. */
function seedEditFormFrom(workspace: Workspace): void {
  editMarketplaces.value = [...workspace.marketplaces];
  editPluginSelection.value = seedSelection(workspace.pluginSelection);
}

/**
 * Re-reads the edit form from the (refreshed) workspace list, DISCARDING whatever the user had
 * pending. Called by the parent after a 409, where the alternative is worse: the list reload gives
 * the next save a fresh CAS token while the form still holds the pre-conflict selection, so a second
 * click would pass compare-and-swap and silently overwrite the other writer. Discarding is only safe
 * because it is announced — the conflict message tells the user their pending change was dropped.
 * Deliberately an explicit call rather than a `watch` on `props.workspaces`, which would also fire
 * on unrelated list refreshes and wipe an edit in progress for no reason.
 */
function reseedEditForm(): void {
  const workspace = editWorkspace.value;
  if (formMode.value !== 'edit' || !workspace) return;
  seedEditFormFrom(workspace);
}

function toggleEditMarketplace(id: string): void {
  if (interactionBlocked.value) return;
  const idx = editMarketplaces.value.indexOf(id);
  if (idx >= 0) {
    editMarketplaces.value.splice(idx, 1);
    editPluginSelection.value = pruneMarketplaceFrom(editPluginSelection.value, id);
  } else {
    editMarketplaces.value.push(id);
  }
}

function toggleEditPlugin(marketplace: string, plugin: string): void {
  // Belt-and-suspenders behind the disabled checkbox: never materialize a selection that would
  // silently omit an unlistable marketplace's plugins. See hasErroredEnabledMarketplace.
  if (editPluginsBlocked.value) return;
  editPluginSelection.value = togglePluginIn(
    editPluginSelection.value,
    editMarketplaces.value,
    marketplace,
    plugin
  );
}

/** See {@link resetCreatePlugins}. */
function resetEditPlugins(): void {
  editPluginSelection.value = null;
}

function submitEdit(): void {
  if (submitting.value || interactionBlocked.value) return;
  formError.value = null;
  if (!editWorkspaceId.value) return;
  const payload: WorkspaceUpdate = { marketplaces: [...editMarketplaces.value] };
  const workspace = editWorkspace.value;
  // Include the selection ONLY when it actually differs from what is stored. Setting the key on
  // every save would make the four-state contract useless in the one case it exists for: the
  // backend routes on the key's PRESENCE (WorkspacesController: `PluginSelection.IsSet`), so a
  // rename, a marketplace-only toggle, or a no-op save would each take the session-migration path —
  // destroying and recreating every live sandbox session for this workspace, blocking on the idle
  // wait, and failing with 503 if a conversation is mid-run — and would bump pluginsRevision
  // (FileWorkspaceStore keys the bump off IsSet, not off the value changing), invalidating every
  // other tab's CAS token for a change that touched no plugin.
  const selectionChanged =
    pluginFilteringEnabled.value
    && workspace !== null
    && !pluginSelectionEquals(editPluginSelection.value, seedSelection(workspace.pluginSelection));
  if (selectionChanged) {
    payload.pluginSelection =
      editPluginSelection.value === null ? null : [...editPluginSelection.value];
    // The compare-and-swap token is MANDATORY whenever pluginSelection is set, and is only ever
    // read here — where `workspace` is known non-null, so it can never be silently omitted.
    payload.pluginsRevision = workspace.pluginsRevision;
  }
  // Otherwise `pluginSelection` is ABSENT from the body — the backend's four-state "leave
  // unchanged". That covers a marketplace-only edit, a no-op save, and the whole UI when the
  // gateway cannot filter plugins: none of them may clobber a stored selection.
  submitting.value = true;
  emit('update-workspace', editWorkspaceId.value, payload);
}

/**
 * Surfaces an API error returned by the parent after a create/update emit.
 * The form is kept open (in-flight) until this or closeForm() is called, so the
 * inline error element is still mounted and renders the message.
 */
function showFormError(message: string): void {
  formError.value = message;
  submitting.value = false;
}

defineExpose({ showFormError, closeForm, reseedEditForm });

// --- Outside click / escape ---------------------------------------------

/**
 * Closes the dropdown when a click lands outside it.
 *
 * The `isConnected` bail-out is load-bearing, not defensive noise. A control INSIDE the dropdown
 * that removes itself when clicked — "Use all plugins" is `v-if`'d on the very state its own
 * `@click` clears — is already detached from the document by the time this listener runs: the
 * browser performs a microtask checkpoint between listener invocations, Vue's scheduler flushes
 * there, and the element the user clicked no longer exists. `dropdownRef.contains(target)` then
 * answers `false` for a click that was unambiguously inside, and the whole dropdown collapses,
 * discarding the form.
 *
 * Fixing this at the guard rather than at the two buttons (`@click.stop`) is deliberate: ANY
 * conditionally-rendered control in this dropdown has the same defect latent, and stopping
 * propagation only patches the instances we happen to know about today. A detached node cannot
 * meaningfully be "outside" anything — there is nothing left to measure it against — so declining
 * to close is the only answer this function can give truthfully.
 */
function handleClickOutside(event: MouseEvent): void {
  const target = event.target as Node | null;
  if (target instanceof Node && !target.isConnected) {
    return;
  }
  if (dropdownRef.value && !dropdownRef.value.contains(target)) {
    closeDropdown();
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    closeDropdown();
  }
}

onMounted(() => {
  document.addEventListener('click', handleClickOutside);
  document.addEventListener('keydown', handleKeydown);
  void loadAvailableMarketplaces();
});

onUnmounted(() => {
  document.removeEventListener('click', handleClickOutside);
  document.removeEventListener('keydown', handleKeydown);
});

/**
 * Teardown is bound to the TERMINAL conditions only — never to `isLoading`. Anything transient that
 * closes this dropdown destroys work the user is in the middle of; see the `isLoading` prop doc.
 * If you add a condition here, ask first whether it can flip back to false on its own. If it can,
 * it belongs in `interactionBlocked`, not in this watcher.
 */
watch(
  () => [props.disabled, props.lockedWorkspaceId] as const,
  ([isDisabled, locked]) => {
    if (isDisabled || locked) {
      closeDropdown();
    }
  }
);
</script>

<template>
  <div class="workspace-selector" ref="dropdownRef" data-testid="workspace-selector">
    <span
      v-if="isLocked"
      class="workspace-badge"
      data-testid="workspace-locked-badge"
      :title="`This conversation is locked to ${lockedWorkspace?.name ?? lockedWorkspaceId}`"
    >
      <span class="badge-label">Workspace:</span>
      <span class="badge-name">{{ lockedWorkspace?.name ?? lockedWorkspaceId }}</span>
      <span class="badge-lock" aria-hidden="true">🔒</span>
    </span>
    <template v-else>
      <button
        class="selector-btn"
        :class="{ open: dropdownOpen }"
        data-testid="workspace-selector-button"
        @click="toggleDropdown"
        :disabled="interactionBlocked"
      >
        <span class="workspace-label">Workspace:</span>
        <span class="workspace-name">{{ selectedWorkspace?.name ?? 'Loading...' }}</span>
        <span class="dropdown-arrow">{{ dropdownOpen ? '▲' : '▼' }}</span>
      </button>

      <div v-if="dropdownOpen" class="dropdown-menu">
        <div v-if="gateway" class="section-header" data-testid="workspace-gateway-status">
          {{ gateway.canonicalBaseUrl }} · {{ gateway.appId }}
          <span v-if="!gateway.available"> · unavailable</span>
        </div>
        <!-- List view -->
        <template v-if="formMode === 'none'">
          <div v-if="systemWorkspaces.length > 0" class="menu-section">
            <div class="section-header">System</div>
            <div
              v-for="workspace in systemWorkspaces"
              :key="workspace.id"
              class="menu-row"
            >
              <button
                class="menu-item"
                :class="{ active: workspace.id === selectedWorkspaceId }"
                :data-testid="`workspace-option-${workspace.id}`"
                :disabled="interactionBlocked || workspace.compatibility !== 'compatible'"
                :title="workspace.compatibility === 'incompatible'
                  ? `Unsupported: ${workspace.unsupportedMarketplaces.join(', ')}`
                  : workspace.compatibility === 'unknown' ? 'Gateway compatibility unavailable' : ''"
                @click="handleSelect(workspace.id)"
              >
                <span class="item-name">{{ workspace.name }}</span>
                <span v-if="workspace.id === selectedWorkspaceId" class="check-mark">✓</span>
              </button>
            </div>
          </div>

          <div v-if="userWorkspaces.length > 0" class="menu-section">
            <div class="section-header">Your Workspaces</div>
            <div
              v-for="workspace in userWorkspaces"
              :key="workspace.id"
              class="menu-row"
            >
              <button
                class="menu-item"
                :class="{ active: workspace.id === selectedWorkspaceId }"
                :data-testid="`workspace-option-${workspace.id}`"
                :disabled="interactionBlocked || workspace.compatibility !== 'compatible'"
                :title="workspace.compatibility === 'incompatible'
                  ? `Unsupported: ${workspace.unsupportedMarketplaces.join(', ')}`
                  : workspace.compatibility === 'unknown' ? 'Gateway compatibility unavailable' : ''"
                @click="handleSelect(workspace.id)"
              >
                <span class="item-name">{{ workspace.name }}</span>
                <span v-if="workspace.id === selectedWorkspaceId" class="check-mark">✓</span>
              </button>
              <button
                class="edit-btn"
                :data-testid="`workspace-edit-${workspace.id}`"
                :disabled="interactionBlocked"
                title="Edit marketplaces"
                @click.stop="openEditForm(workspace)"
              >
                ✎
              </button>
            </div>
          </div>

          <div class="menu-divider"></div>

          <button
            class="menu-item manage-item"
            data-testid="workspace-create-open"
            :disabled="interactionBlocked"
            @click.stop="openCreateForm"
          >
            + New workspace
          </button>
        </template>

        <!-- Create form -->
        <form
          v-else-if="formMode === 'create'"
          class="ws-form"
          data-testid="workspace-create-form"
          @submit.prevent="submitCreate"
        >
          <div class="form-title">New workspace</div>
          <label class="field">
            <span class="field-label">Name</span>
            <input
              v-model="createName"
              class="field-input"
              data-testid="workspace-create-name"
              type="text"
              placeholder="My workspace"
            />
          </label>
          <label class="field">
            <span class="field-label">Directory</span>
            <input
              v-model="createDirectory"
              class="field-input"
              data-testid="workspace-create-directory"
              type="text"
              placeholder="my-workspace"
              @input="onDirectoryInput"
            />
          </label>
          <div class="field">
            <div class="field-header">
              <span class="field-label">Marketplaces</span>
              <button
                v-if="pluginFilteringEnabled && createPluginSelection !== null"
                type="button"
                class="link-btn"
                data-testid="workspace-create-plugins-reset"
                title="Go back to enabling every plugin of the selected marketplaces"
                @click="resetCreatePlugins"
              >
                Use all plugins
              </button>
            </div>
            <div class="marketplace-list">
              <div
                v-for="m in availableMarketplaces"
                :key="m.id"
                class="marketplace-group"
              >
                <label class="marketplace-item">
                  <input
                    type="checkbox"
                    :data-testid="`workspace-create-marketplace-${m.id}`"
                    :checked="createMarketplaces.includes(m.id)"
                    :disabled="interactionBlocked"
                    :indeterminate.prop="isMarketplaceIndeterminate(createPluginSelection, createMarketplaces, m)"
                    @change="toggleCreateMarketplace(m.id)"
                  />
                  <span>{{ m.displayName }}</span>
                  <span
                    v-if="pluginFilteringEnabled && m.plugins.length > 0"
                    class="plugin-count"
                  >{{ m.plugins.length }}</span>
                </label>
                <div
                  v-if="pluginFilteringEnabled && createMarketplaces.includes(m.id) && m.error"
                  class="plugin-load-error"
                  :data-testid="`workspace-create-plugins-error-${m.id}`"
                >
                  Plugins could not be listed for this marketplace, so plugin selection is disabled
                  for this workspace until it loads — saving a selection now would drop this
                  marketplace's plugins.
                </div>
                <div
                  v-if="pluginFilteringEnabled && createMarketplaces.includes(m.id) && m.plugins.length > 0"
                  class="plugin-list"
                  :data-testid="`workspace-create-plugins-${m.id}`"
                >
                  <label v-for="p in m.plugins" :key="p" class="plugin-item">
                    <input
                      type="checkbox"
                      data-plugin-checkbox="true"
                      :data-testid="`workspace-create-plugin-${m.id}-${p}`"
                      :checked="isPluginChecked(createPluginSelection, createMarketplaces, m.id, p)"
                      :disabled="createPluginsBlocked"
                      @change="toggleCreatePlugin(m.id, p)"
                    />
                    <span>{{ p }}</span>
                  </label>
                </div>
              </div>
              <p
                v-if="availableMarketplaces.length === 0"
                class="marketplace-empty"
                data-testid="workspace-marketplaces-empty"
              >
                {{ marketplacesUnavailable ? 'Gateway offline — no marketplaces available.' : 'No marketplaces available.' }}
              </p>
            </div>
          </div>
          <div v-if="formError" class="form-error" data-testid="workspace-form-error">
            {{ formError }}
          </div>
          <div class="form-actions">
            <button
              type="button"
              class="btn-secondary"
              data-testid="workspace-create-cancel"
              @click="closeForm"
            >
              Cancel
            </button>
            <button
              type="submit"
              class="btn-primary"
              data-testid="workspace-create-submit"
              :disabled="submitting || interactionBlocked"
            >
              Create
            </button>
          </div>
        </form>

        <!-- Edit form (marketplaces only) -->
        <form
          v-else-if="formMode === 'edit'"
          class="ws-form"
          data-testid="workspace-edit-form"
          @submit.prevent="submitEdit"
        >
          <div class="form-title">Edit workspace</div>
          <label class="field">
            <span class="field-label">Name</span>
            <input
              class="field-input"
              data-testid="workspace-edit-name"
              type="text"
              :value="editWorkspace?.name ?? ''"
              readonly
            />
          </label>
          <label class="field">
            <span class="field-label">Directory</span>
            <input
              class="field-input"
              data-testid="workspace-edit-directory"
              type="text"
              :value="editWorkspace?.directoryRelPath ?? ''"
              readonly
            />
          </label>
          <div class="field">
            <div class="field-header">
              <span class="field-label">Marketplaces</span>
              <button
                v-if="pluginFilteringEnabled && editPluginSelection !== null"
                type="button"
                class="link-btn"
                data-testid="workspace-edit-plugins-reset"
                title="Go back to enabling every plugin of the selected marketplaces"
                @click="resetEditPlugins"
              >
                Use all plugins
              </button>
            </div>
            <div class="marketplace-list">
              <div
                v-for="m in availableMarketplaces"
                :key="m.id"
                class="marketplace-group"
              >
                <label class="marketplace-item">
                  <input
                    type="checkbox"
                    :data-testid="`workspace-edit-marketplace-${m.id}`"
                    :checked="editMarketplaces.includes(m.id)"
                    :disabled="interactionBlocked"
                    :indeterminate.prop="isMarketplaceIndeterminate(editPluginSelection, editMarketplaces, m)"
                    @change="toggleEditMarketplace(m.id)"
                  />
                  <span>{{ m.displayName }}</span>
                  <span
                    v-if="pluginFilteringEnabled && m.plugins.length > 0"
                    class="plugin-count"
                  >{{ m.plugins.length }}</span>
                </label>
                <div
                  v-if="pluginFilteringEnabled && editMarketplaces.includes(m.id) && m.error"
                  class="plugin-load-error"
                  :data-testid="`workspace-edit-plugins-error-${m.id}`"
                >
                  Plugins could not be listed for this marketplace, so plugin selection is disabled
                  for this workspace until it loads — saving a selection now would drop this
                  marketplace's plugins.
                </div>
                <div
                  v-if="pluginFilteringEnabled && editMarketplaces.includes(m.id) && m.plugins.length > 0"
                  class="plugin-list"
                  :data-testid="`workspace-edit-plugins-${m.id}`"
                >
                  <label v-for="p in m.plugins" :key="p" class="plugin-item">
                    <input
                      type="checkbox"
                      data-plugin-checkbox="true"
                      :data-testid="`workspace-edit-plugin-${m.id}-${p}`"
                      :checked="isPluginChecked(editPluginSelection, editMarketplaces, m.id, p)"
                      :disabled="editPluginsBlocked"
                      @change="toggleEditPlugin(m.id, p)"
                    />
                    <span>{{ p }}</span>
                  </label>
                </div>
              </div>
              <p
                v-if="availableMarketplaces.length === 0"
                class="marketplace-empty"
                data-testid="workspace-marketplaces-empty"
              >
                {{ marketplacesUnavailable ? 'Gateway offline — no marketplaces available.' : 'No marketplaces available.' }}
              </p>
            </div>
          </div>
          <div v-if="formError" class="form-error" data-testid="workspace-form-error">
            {{ formError }}
          </div>
          <div class="form-actions">
            <button
              type="button"
              class="btn-secondary"
              data-testid="workspace-edit-cancel"
              @click="closeForm"
            >
              Cancel
            </button>
            <button
              type="submit"
              class="btn-primary"
              data-testid="workspace-edit-submit"
              :disabled="submitting || interactionBlocked"
            >
              Save
            </button>
          </div>
        </form>
      </div>
    </template>
  </div>
</template>

<style scoped>
.workspace-selector {
  position: relative;
}

.selector-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  background: #f8f9fa;
  border: 1px solid #ddd;
  border-radius: 6px;
  font-size: 13px;
  cursor: pointer;
  transition: background 0.2s, border-color 0.2s;
}

.selector-btn:hover:not(:disabled) {
  background: #e9ecef;
}

.selector-btn.open {
  border-color: #0d6efd;
  box-shadow: 0 0 0 2px rgba(13, 110, 253, 0.15);
}

.selector-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.workspace-label {
  color: #666;
}

.workspace-name {
  color: #333;
  font-weight: 500;
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dropdown-arrow {
  color: #666;
  font-size: 10px;
  margin-left: 4px;
}

.dropdown-menu {
  position: absolute;
  top: 100%;
  right: 0;
  margin-top: 4px;
  min-width: 240px;
  background: white;
  border: 1px solid #ddd;
  border-radius: 8px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  z-index: 100;
  overflow: hidden;
  padding: 4px 0;
}

.menu-section {
  padding: 4px 0;
}

.section-header {
  padding: 8px 12px 4px;
  font-size: 11px;
  font-weight: 600;
  color: #888;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.menu-row {
  display: flex;
  align-items: center;
}

.menu-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex: 1;
  min-width: 0;
  padding: 8px 12px;
  background: none;
  border: none;
  font-size: 14px;
  text-align: left;
  cursor: pointer;
  transition: background 0.15s;
}

.menu-item:hover:not(:disabled) {
  background: #f8f9fa;
}

.menu-item:disabled {
  color: #9aa0a6;
  cursor: not-allowed;
}

.menu-item.active {
  background: #e7f1ff;
  color: #0d6efd;
}

.item-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.check-mark {
  color: #0d6efd;
  font-weight: bold;
  flex-shrink: 0;
  margin-left: 8px;
}

.edit-btn {
  flex-shrink: 0;
  padding: 6px 10px;
  background: none;
  border: none;
  color: #666;
  cursor: pointer;
  font-size: 13px;
}

.edit-btn:hover:not(:disabled) {
  color: #0d6efd;
}

.edit-btn:disabled {
  color: #c0c0c0;
  cursor: not-allowed;
}

.menu-divider {
  height: 1px;
  background: #eee;
  margin: 4px 0;
}

.manage-item {
  color: #0d6efd;
  font-weight: 500;
}

.manage-item:hover {
  background: #e7f1ff;
}

.ws-form {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
}

.form-title {
  font-size: 13px;
  font-weight: 600;
  color: #333;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.field-label {
  font-size: 11px;
  font-weight: 600;
  color: #888;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.field-input {
  padding: 6px 8px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 13px;
}

.field-input:read-only {
  background: #f1f3f5;
  color: #666;
}

.field-header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
}

.link-btn {
  padding: 0;
  background: none;
  border: none;
  color: #0d6efd;
  font-size: 11px;
  cursor: pointer;
  text-decoration: underline;
}

.marketplace-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
  /* A workspace can enable several marketplaces, one of which may publish 20+ plugins. Cap the
     whole list and scroll it so the dropdown never grows past the viewport. */
  max-height: 260px;
  overflow-y: auto;
}

.marketplace-group {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.marketplace-item {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  cursor: pointer;
}

.plugin-count {
  margin-left: auto;
  padding: 0 6px;
  background: #eef1f5;
  border-radius: 8px;
  color: #666;
  font-size: 11px;
}

.plugin-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 2px 0 4px 20px;
  padding-left: 8px;
  border-left: 2px solid #eee;
  /* Second cap, per marketplace: a single 22-plugin marketplace scrolls on its own so it cannot
     push its siblings out of reach. */
  max-height: 150px;
  overflow-y: auto;
}

.plugin-item {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: #444;
  cursor: pointer;
}

.plugin-load-error {
  margin: 2px 0 4px 20px;
  padding-left: 8px;
  border-left: 2px solid #f0c36d;
  color: #8a6d3b;
  font-size: 11px;
}

.form-error {
  font-size: 12px;
  color: #b02a37;
}

.form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.btn-secondary,
.btn-primary {
  padding: 6px 12px;
  border-radius: 6px;
  font-size: 13px;
  cursor: pointer;
  border: 1px solid transparent;
}

.btn-secondary {
  background: #f1f3f5;
  border-color: #ddd;
  color: #444;
}

.btn-primary {
  background: #0d6efd;
  color: white;
}

.btn-primary:hover {
  background: #0b5ed7;
}

.workspace-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  background: #eef1f5;
  border: 1px solid #d0d7de;
  border-radius: 6px;
  font-size: 13px;
  color: #444;
}

.badge-label {
  color: #666;
}

.badge-name {
  color: #333;
  font-weight: 500;
}

.badge-lock {
  font-size: 12px;
  opacity: 0.8;
}
</style>
