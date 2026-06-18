<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue';
import type { Workspace, WorkspaceCreate, WorkspaceUpdate } from '@/types/workspace';
import { availableMarketplaces } from '@/types/workspace';

const props = defineProps<{
  workspaces: Workspace[];
  selectedWorkspaceId: string | null;
  /**
   * Workspace id locked to the current thread (set after the first message).
   * When provided, the selector renders as a read-only badge instead of a
   * dropdown.
   */
  lockedWorkspaceId?: string | null;
  isLoading?: boolean;
  disabled?: boolean;
}>();

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

// Create form state
const createName = ref('');
const createDirectory = ref('');
const directoryTouched = ref(false);
const createMarketplaces = ref<string[]>([]);

// Edit form state
const editWorkspaceId = ref<string | null>(null);
const editMarketplaces = ref<string[]>([]);

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
  if (props.disabled || props.isLoading || isLocked.value) {
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
}

function handleSelect(workspaceId: string): void {
  if (props.disabled || isLocked.value) {
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
  if (props.disabled) return;
  formMode.value = 'create';
  formError.value = null;
  createName.value = '';
  createDirectory.value = '';
  directoryTouched.value = false;
  createMarketplaces.value = [];
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
  const idx = createMarketplaces.value.indexOf(id);
  if (idx >= 0) {
    createMarketplaces.value.splice(idx, 1);
  } else {
    createMarketplaces.value.push(id);
  }
}

function submitCreate(): void {
  formError.value = null;
  const name = createName.value.trim();
  if (!name) {
    formError.value = 'Name is required';
    return;
  }
  const directory = createDirectory.value.trim();
  emit('create-workspace', {
    name,
    directoryRelPath: directory || undefined,
    marketplaces: [...createMarketplaces.value],
  });
  closeForm();
}

// --- Edit form -----------------------------------------------------------

function openEditForm(workspace: Workspace): void {
  if (props.disabled || workspace.isSystemDefined) return;
  formMode.value = 'edit';
  formError.value = null;
  editWorkspaceId.value = workspace.id;
  editMarketplaces.value = [...workspace.marketplaces];
}

function toggleEditMarketplace(id: string): void {
  const idx = editMarketplaces.value.indexOf(id);
  if (idx >= 0) {
    editMarketplaces.value.splice(idx, 1);
  } else {
    editMarketplaces.value.push(id);
  }
}

function submitEdit(): void {
  formError.value = null;
  if (!editWorkspaceId.value) return;
  emit('update-workspace', editWorkspaceId.value, {
    marketplaces: [...editMarketplaces.value],
  });
  closeForm();
}

/**
 * Allows the parent to surface an API error returned after a create/update
 * emit (the form has already closed by then; reopen-less inline display).
 */
function showFormError(message: string): void {
  formError.value = message;
}

defineExpose({ showFormError });

// --- Outside click / escape ---------------------------------------------

function handleClickOutside(event: MouseEvent): void {
  if (dropdownRef.value && !dropdownRef.value.contains(event.target as Node)) {
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
});

onUnmounted(() => {
  document.removeEventListener('click', handleClickOutside);
  document.removeEventListener('keydown', handleKeydown);
});

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
        :disabled="isLoading || disabled"
      >
        <span class="workspace-label">Workspace:</span>
        <span class="workspace-name">{{ selectedWorkspace?.name ?? 'Loading...' }}</span>
        <span class="dropdown-arrow">{{ dropdownOpen ? '▲' : '▼' }}</span>
      </button>

      <div v-if="dropdownOpen" class="dropdown-menu">
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
                :disabled="disabled"
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
                :disabled="disabled"
                @click="handleSelect(workspace.id)"
              >
                <span class="item-name">{{ workspace.name }}</span>
                <span v-if="workspace.id === selectedWorkspaceId" class="check-mark">✓</span>
              </button>
              <button
                class="edit-btn"
                :data-testid="`workspace-edit-${workspace.id}`"
                :disabled="disabled"
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
            :disabled="disabled"
            @click="openCreateForm"
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
            <span class="field-label">Marketplaces</span>
            <div class="marketplace-list">
              <label
                v-for="m in availableMarketplaces"
                :key="m.id"
                class="marketplace-item"
              >
                <input
                  type="checkbox"
                  :data-testid="`workspace-create-marketplace-${m.id}`"
                  :checked="createMarketplaces.includes(m.id)"
                  @change="toggleCreateMarketplace(m.id)"
                />
                <span>{{ m.displayName }}</span>
              </label>
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
            <span class="field-label">Marketplaces</span>
            <div class="marketplace-list">
              <label
                v-for="m in availableMarketplaces"
                :key="m.id"
                class="marketplace-item"
              >
                <input
                  type="checkbox"
                  :data-testid="`workspace-edit-marketplace-${m.id}`"
                  :checked="editMarketplaces.includes(m.id)"
                  @change="toggleEditMarketplace(m.id)"
                />
                <span>{{ m.displayName }}</span>
              </label>
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

.marketplace-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.marketplace-item {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  cursor: pointer;
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
