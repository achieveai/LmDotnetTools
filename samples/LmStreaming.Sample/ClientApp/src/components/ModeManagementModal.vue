<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount, nextTick } from 'vue';
import type { ChatMode, ChatModeCreateUpdate, ToolDefinition } from '@/types/chatMode';
import ModeEditor from './ModeEditor.vue';

const props = defineProps<{
  modes: ChatMode[];
  tools: ToolDefinition[];
  isLoading?: boolean;
}>();

const emit = defineEmits<{
  close: [];
  create: [data: ChatModeCreateUpdate];
  update: [modeId: string, data: ChatModeCreateUpdate];
  delete: [modeId: string];
  copy: [modeId: string, newName: string];
}>();

type View = 'list' | 'create' | 'edit';

const currentView = ref<View>('list');
const editingMode = ref<ChatMode | null>(null);
const copyDialogVisible = ref(false);
const copySourceMode = ref<ChatMode | null>(null);
const copyNewName = ref('');
const copyNameInput = ref<HTMLInputElement | null>(null);
const deleteConfirmMode = ref<ChatMode | null>(null);
const modeEditorRef = ref<InstanceType<typeof ModeEditor> | null>(null);
/**
 * A create/update is in flight: `handleSave` no longer switches back to the list view itself (it
 * used to, unconditionally, before the parent's `await createMode/updateMode` even settled — which
 * is exactly what made surfacing a server-side validation error impossible, since the editor was
 * already unmounted by the time the rejection arrived). The caller now closes the form on success
 * ({@link closeForm}) or shows the error and keeps it open on failure ({@link showFormError}).
 */
const submitting = ref(false);

/**
 * Free-text filter over the list. The catalogue is already 8 system modes plus however many the
 * user has made, and the only way to reach one was to scroll past all the others.
 */
const searchQuery = ref('');

/** Descriptions render clamped; these are the rows the user has asked to see in full. */
const expandedDescriptions = ref<Record<string, boolean>>({});

/**
 * Longer than this and the clamp is likely to bite, so the row earns a Show more control. A length
 * threshold rather than a measured overflow: it needs no layout, so it behaves the same in a test
 * as in a browser.
 */
const DESCRIPTION_CLAMP_CHARS = 96;

function matchesQuery(mode: ChatMode): boolean {
  const query = searchQuery.value.trim().toLowerCase();
  if (!query) return true;
  return (
    mode.name.toLowerCase().includes(query) ||
    (mode.description?.toLowerCase().includes(query) ?? false)
  );
}

const systemModes = computed(() =>
  props.modes.filter((m) => m.isSystemDefined).filter(matchesQuery)
);
const userModes = computed(() =>
  props.modes.filter((m) => !m.isSystemDefined).filter(matchesQuery)
);

const userModeTotal = computed(() => props.modes.filter((m) => !m.isSystemDefined).length);
const isFiltering = computed(() => searchQuery.value.trim().length > 0);
const noMatches = computed(
  () => isFiltering.value && systemModes.value.length === 0 && userModes.value.length === 0
);

function getToolCount(mode: ChatMode): string {
  if (!mode.enabledTools) return 'All tools';
  if (mode.enabledTools.length === 0) return 'No tools';
  return `${mode.enabledTools.length} tool${mode.enabledTools.length !== 1 ? 's' : ''}`;
}

function isDescriptionLong(mode: ChatMode): boolean {
  return (mode.description?.length ?? 0) > DESCRIPTION_CLAMP_CHARS;
}

function isDescriptionExpanded(mode: ChatMode): boolean {
  return !!expandedDescriptions.value[mode.id];
}

function toggleDescription(mode: ChatMode): void {
  expandedDescriptions.value = {
    ...expandedDescriptions.value,
    [mode.id]: !expandedDescriptions.value[mode.id],
  };
}

function handleCreateNew(): void {
  editingMode.value = null;
  currentView.value = 'create';
}

function handleEdit(mode: ChatMode): void {
  editingMode.value = mode;
  currentView.value = 'edit';
}

function handleCopy(mode: ChatMode): void {
  copySourceMode.value = mode;
  copyNewName.value = `${mode.name} (Copy)`;
  copyDialogVisible.value = true;
  void nextTick(() => copyNameInput.value?.select());
}

function handleConfirmCopy(): void {
  if (copySourceMode.value && copyNewName.value.trim()) {
    emit('copy', copySourceMode.value.id, copyNewName.value.trim());
    copyDialogVisible.value = false;
    copySourceMode.value = null;
    copyNewName.value = '';
  }
}

function handleCancelCopy(): void {
  copyDialogVisible.value = false;
  copySourceMode.value = null;
  copyNewName.value = '';
}

function handleDelete(mode: ChatMode): void {
  deleteConfirmMode.value = mode;
}

function handleConfirmDelete(): void {
  if (deleteConfirmMode.value) {
    emit('delete', deleteConfirmMode.value.id);
    deleteConfirmMode.value = null;
  }
}

function handleCancelDelete(): void {
  deleteConfirmMode.value = null;
}

function handleSave(data: ChatModeCreateUpdate): void {
  if (submitting.value) return;
  submitting.value = true;
  if (currentView.value === 'edit' && editingMode.value) {
    emit('update', editingMode.value.id, data);
  } else {
    emit('create', data);
  }
  // View intentionally stays put until the parent calls closeForm() or showFormError() — see
  // `submitting`'s doc comment above.
}

function handleCancelEdit(): void {
  currentView.value = 'list';
  editingMode.value = null;
  submitting.value = false;
}

/** Called by the parent once the awaited create/update actually succeeded. */
function closeForm(): void {
  submitting.value = false;
  currentView.value = 'list';
  editingMode.value = null;
}

/**
 * Called by the parent when the awaited create/update failed with something worth showing inline
 * (currently `InvalidEnvError`). The view is left exactly where it was — create or edit — so the
 * user's entered data (env rows included) is not lost, mirroring `WorkspaceSelector.showFormError`.
 */
function showFormError(message: string): void {
  submitting.value = false;
  modeEditorRef.value?.showFormError(message);
}

defineExpose({ closeForm, showFormError });

function handleClose(): void {
  emit('close');
}

function handleBackdropClick(event: MouseEvent): void {
  if (event.target === event.currentTarget) {
    handleClose();
  }
}

/**
 * Escape closes the innermost thing that is open. This modal does not go through `BaseModal`, which
 * is where the rest of the app's Escape handling lives, so it had none at all.
 */
function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return;
  event.stopPropagation();
  if (copyDialogVisible.value) {
    handleCancelCopy();
  } else if (deleteConfirmMode.value) {
    handleCancelDelete();
  } else if (currentView.value !== 'list') {
    handleCancelEdit();
  } else {
    handleClose();
  }
}

onMounted(() => document.addEventListener('keydown', handleKeydown));
onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown));
</script>

<template>
  <div class="modal-backdrop" @click="handleBackdropClick">
    <div
      class="modal-container"
      data-testid="mode-management-modal"
      role="dialog"
      aria-modal="true"
      :aria-labelledby="currentView === 'list' ? 'mode-management-title' : undefined"
      :aria-label="currentView === 'list' ? undefined : 'Mode editor'"
    >
      <!--
        In the editor views the title used to be an empty string, which still cost a full header
        band with nothing in it but the X. That band now carries the way back to the list.
      -->
      <div class="modal-header" :class="{ compact: currentView !== 'list' }">
        <button
          v-if="currentView !== 'list'"
          class="back-btn"
          type="button"
          @click="handleCancelEdit"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
            <path
              d="M11.5 5 6.5 10l5 5"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
          All modes
        </button>

        <div v-else class="header-text">
          <h2 id="mode-management-title" class="modal-title">Manage Modes</h2>
          <p class="modal-subtitle">
            A mode pairs a system prompt with the tools a conversation may use.
          </p>
        </div>

        <button class="close-btn" type="button" aria-label="Close" title="Close" @click="handleClose">
          <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
            <path
              d="m5.5 5.5 9 9m0-9-9 9"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linecap="round"
            />
          </svg>
        </button>
      </div>

      <!-- List View -->
      <template v-if="currentView === 'list'">
        <div class="modal-toolbar">
          <div class="search-box">
            <span class="search-icon" aria-hidden="true">
              <svg viewBox="0 0 20 20" focusable="false">
                <circle cx="9" cy="9" r="5.2" fill="none" stroke="currentColor" stroke-width="1.6" />
                <path
                  d="m12.9 12.9 3.6 3.6"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="1.6"
                  stroke-linecap="round"
                />
              </svg>
            </span>
            <input
              v-model="searchQuery"
              type="search"
              class="search-input"
              data-testid="mode-search"
              placeholder="Search modes by name or description..."
              aria-label="Search modes"
            />
          </div>
        </div>

        <!--
          The one scroll region in the list view. `min-height: 0` is the whole fix for the modal
          growing past the viewport: a flex child defaults to `min-height: auto`, so `flex: 1` alone
          never shrank it and `overflow-y: auto` had nothing to scroll.
        -->
        <div class="modal-content">
          <div v-if="noMatches" class="no-modes">No modes match "{{ searchQuery }}".</div>

          <div v-else class="mode-sections">
            <!-- System Modes -->
            <section
              v-if="systemModes.length > 0"
              class="mode-section"
              data-testid="mode-section-system"
            >
              <header class="section-header">
                <h3 class="section-title">System Modes</h3>
                <span class="section-count">{{ systemModes.length }}</span>
                <p class="section-description">Built-in, cannot be modified</p>
              </header>
              <ul class="mode-list">
                <li
                  v-for="mode in systemModes"
                  :key="mode.id"
                  class="mode-item"
                  :data-testid="`mode-item-${mode.id}`"
                >
                  <div class="mode-info">
                    <div class="mode-title-row">
                      <span class="mode-name">{{ mode.name }}</span>
                      <span class="mode-tools">{{ getToolCount(mode) }}</span>
                    </div>
                    <template v-if="mode.description">
                      <p
                        class="mode-description"
                        :class="{ expanded: isDescriptionExpanded(mode) }"
                        :title="mode.description"
                      >
                        {{ mode.description }}
                      </p>
                      <button
                        v-if="isDescriptionLong(mode)"
                        type="button"
                        class="description-toggle"
                        :data-testid="`mode-description-toggle-${mode.id}`"
                        :aria-expanded="isDescriptionExpanded(mode)"
                        @click="toggleDescription(mode)"
                      >
                        {{ isDescriptionExpanded(mode) ? 'Show less' : 'Show more' }}
                      </button>
                    </template>
                  </div>
                  <div class="mode-actions">
                    <button
                      class="action-btn"
                      type="button"
                      :data-testid="`mode-copy-${mode.id}`"
                      :aria-label="`Create a copy of ${mode.name}`"
                      title="Create a copy"
                      @click="handleCopy(mode)"
                    >
                      <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
                        <rect
                          x="7"
                          y="7"
                          width="9"
                          height="9"
                          rx="2"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="1.5"
                        />
                        <path
                          d="M13 4.5H6A1.5 1.5 0 0 0 4.5 6v7"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="1.5"
                          stroke-linecap="round"
                        />
                      </svg>
                    </button>
                  </div>
                </li>
              </ul>
            </section>

            <!-- User Modes -->
            <section class="mode-section" data-testid="mode-section-user">
              <header class="section-header">
                <h3 class="section-title">Your Modes</h3>
                <span class="section-count">{{ userModes.length }}</span>
                <p class="section-description">Custom modes you've created</p>
              </header>
              <ul v-if="userModes.length > 0" class="mode-list">
                <li
                  v-for="mode in userModes"
                  :key="mode.id"
                  class="mode-item"
                  :data-testid="`mode-item-${mode.id}`"
                >
                  <div class="mode-info">
                    <div class="mode-title-row">
                      <span class="mode-name">{{ mode.name }}</span>
                      <span class="mode-tools">{{ getToolCount(mode) }}</span>
                    </div>
                    <template v-if="mode.description">
                      <p
                        class="mode-description"
                        :class="{ expanded: isDescriptionExpanded(mode) }"
                        :title="mode.description"
                      >
                        {{ mode.description }}
                      </p>
                      <button
                        v-if="isDescriptionLong(mode)"
                        type="button"
                        class="description-toggle"
                        :data-testid="`mode-description-toggle-${mode.id}`"
                        :aria-expanded="isDescriptionExpanded(mode)"
                        @click="toggleDescription(mode)"
                      >
                        {{ isDescriptionExpanded(mode) ? 'Show less' : 'Show more' }}
                      </button>
                    </template>
                  </div>
                  <div class="mode-actions">
                    <button
                      class="action-btn"
                      type="button"
                      :data-testid="`mode-edit-${mode.id}`"
                      :aria-label="`Edit ${mode.name}`"
                      title="Edit mode"
                      @click="handleEdit(mode)"
                    >
                      <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
                        <path
                          d="M13.4 3.9a1.7 1.7 0 0 1 2.4 2.4L7.6 14.5l-3.2.8.8-3.2Z"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="1.5"
                          stroke-linejoin="round"
                        />
                      </svg>
                    </button>
                    <button
                      class="action-btn"
                      type="button"
                      :data-testid="`mode-copy-${mode.id}`"
                      :aria-label="`Create a copy of ${mode.name}`"
                      title="Create a copy"
                      @click="handleCopy(mode)"
                    >
                      <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
                        <rect
                          x="7"
                          y="7"
                          width="9"
                          height="9"
                          rx="2"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="1.5"
                        />
                        <path
                          d="M13 4.5H6A1.5 1.5 0 0 0 4.5 6v7"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="1.5"
                          stroke-linecap="round"
                        />
                      </svg>
                    </button>
                    <!-- Destructive action, kept off the end of the row behind a rule. -->
                    <span class="action-divider" aria-hidden="true"></span>
                    <button
                      class="action-btn danger"
                      type="button"
                      :data-testid="`mode-delete-${mode.id}`"
                      :aria-label="`Delete ${mode.name}`"
                      title="Delete mode"
                      @click="handleDelete(mode)"
                    >
                      <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
                        <path
                          d="M4.8 6.2h10.4M8.2 6.2V4.8h3.6v1.4M6.2 6.2l.7 9h6.2l.7-9"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="1.5"
                          stroke-linecap="round"
                          stroke-linejoin="round"
                        />
                      </svg>
                    </button>
                  </div>
                </li>
              </ul>
              <div v-else-if="isFiltering && userModeTotal > 0" class="no-modes">
                None of your modes match "{{ searchQuery }}".
              </div>
              <div v-else class="no-modes">No custom modes yet. Create one to get started!</div>
            </section>
          </div>
        </div>

        <div class="modal-footer">
          <button class="btn btn-primary" type="button" data-testid="mode-create-new" @click="handleCreateNew">
            <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
              <path
                d="M10 4.8v10.4M4.8 10h10.4"
                fill="none"
                stroke="currentColor"
                stroke-width="1.7"
                stroke-linecap="round"
              />
            </svg>
            Create New Mode
          </button>
        </div>
      </template>

      <!-- Create/Edit View: the editor owns the height and does its own scrolling. -->
      <div v-else class="modal-editor-host">
        <ModeEditor
          ref="modeEditorRef"
          :mode="editingMode"
          :tools="tools"
          :is-loading="isLoading || submitting"
          @save="handleSave"
          @cancel="handleCancelEdit"
        />
      </div>

      <!-- Copy Dialog -->
      <div v-if="copyDialogVisible" class="dialog-overlay" @click.self="handleCancelCopy">
        <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="mode-copy-title">
          <h3 id="mode-copy-title" class="dialog-title">Copy Mode</h3>
          <p class="dialog-text">Create a copy of "{{ copySourceMode?.name }}"</p>
          <div class="form-group">
            <label for="copy-name" class="form-label">New Name</label>
            <input
              id="copy-name"
              ref="copyNameInput"
              v-model="copyNewName"
              type="text"
              class="form-input"
              data-testid="mode-copy-name"
              placeholder="Enter name for the copy"
              @keydown.enter.prevent="handleConfirmCopy"
            />
          </div>
          <div class="dialog-actions">
            <button class="btn btn-secondary" type="button" @click="handleCancelCopy">Cancel</button>
            <button
              class="btn btn-primary"
              type="button"
              data-testid="mode-copy-confirm"
              :disabled="!copyNewName.trim()"
              @click="handleConfirmCopy"
            >
              Copy
            </button>
          </div>
        </div>
      </div>

      <!-- Delete Confirmation Dialog -->
      <div v-if="deleteConfirmMode" class="dialog-overlay" @click.self="handleCancelDelete">
        <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="mode-delete-title">
          <div class="dialog-heading">
            <span class="dialog-icon danger" aria-hidden="true">
              <svg viewBox="0 0 20 20" focusable="false">
                <path
                  d="M10 3.3 2.9 16h14.2Z"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="1.5"
                  stroke-linejoin="round"
                />
                <path
                  d="M10 8.2v3.3"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="1.6"
                  stroke-linecap="round"
                />
                <circle cx="10" cy="13.7" r="0.9" fill="currentColor" />
              </svg>
            </span>
            <h3 id="mode-delete-title" class="dialog-title">Delete Mode</h3>
          </div>
          <p class="dialog-text">
            Are you sure you want to delete "{{ deleteConfirmMode.name }}"? This action cannot be
            undone.
          </p>
          <div class="dialog-actions">
            <button class="btn btn-secondary" type="button" @click="handleCancelDelete">
              Cancel
            </button>
            <button class="btn btn-danger" type="button" @click="handleConfirmDelete">Delete</button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.modal-backdrop {
  /* Palette shared with the newer shell components (HeaderActionsMenu, ConversationInspector). */
  --mm-border: #d6dbe1;
  --mm-border-soft: #e2e6eb;
  --mm-surface: #f7f8fa;
  --mm-text: #303944;
  --mm-muted: #64748b;
  --mm-accent: #2d6cdf;
  --mm-accent-soft: #eef3f9;
  --mm-danger: #b4232e;
  --mm-danger-soft: #fff0f1;

  position: fixed;
  inset: 0;
  background: rgb(15 23 42 / 45%);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 24px;
  color: var(--mm-text);
}

.modal-container {
  background: #fff;
  border: 1px solid var(--mm-border);
  border-radius: 12px;
  box-shadow: 0 20px 50px rgb(15 23 42 / 22%);
  width: 100%;
  max-width: 640px;
  max-height: min(88vh, 760px);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.modal-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex: none;
  padding: 14px 18px 12px;
  border-bottom: 1px solid var(--mm-border-soft);
}

.header-text {
  min-width: 0;
}

.modal-title {
  margin: 0;
  font-size: 17px;
  font-weight: 600;
}

.modal-header.compact {
  align-items: center;
  padding-block: 9px;
}

.back-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 6px 10px 6px 6px;
  border: 1px solid transparent;
  border-radius: 6px;
  background: transparent;
  color: #475569;
  font-size: 13px;
  font-family: inherit;
  font-weight: 500;
  cursor: pointer;
}

.back-btn svg {
  width: 17px;
  height: 17px;
}

.back-btn:hover {
  background: var(--mm-accent-soft);
  border-color: var(--mm-border);
  color: var(--mm-accent);
}

.back-btn:focus-visible {
  outline: 2px solid var(--mm-accent);
  outline-offset: 2px;
}

.modal-subtitle {
  margin: 3px 0 0;
  font-size: 12.5px;
  line-height: 1.45;
  color: var(--mm-muted);
}

.close-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: none;
  width: 32px;
  height: 32px;
  padding: 0;
  background: #fff;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  color: #394553;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}

.close-btn svg {
  width: 18px;
  height: 18px;
}

.close-btn:hover {
  background: #eef1f4;
  border-color: #aeb7c2;
}

.close-btn:focus-visible {
  outline: 2px solid var(--mm-accent);
  outline-offset: 2px;
}

.modal-toolbar {
  flex: none;
  padding: 12px 18px;
  border-bottom: 1px solid var(--mm-border-soft);
  background: var(--mm-surface);
}

.search-box {
  position: relative;
  display: flex;
  align-items: center;
}

.search-icon {
  position: absolute;
  left: 10px;
  display: inline-flex;
  color: var(--mm-muted);
  pointer-events: none;
}

.search-icon svg {
  width: 16px;
  height: 16px;
}

.search-input {
  width: 100%;
  padding: 8px 12px 8px 32px;
  border: 1px solid var(--mm-border);
  border-radius: 6px;
  font-size: 13.5px;
  font-family: inherit;
  color: var(--mm-text);
  background: #fff;
}

.search-input:focus {
  outline: none;
  border-color: var(--mm-accent);
  box-shadow: 0 0 0 3px rgb(45 108 223 / 18%);
}

.modal-content {
  flex: 1;
  /* Without this the flex child refuses to shrink and the list runs off the bottom of the modal. */
  min-height: 0;
  overflow-y: auto;
  background: var(--mm-surface);
}

.modal-editor-host {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.mode-sections {
  padding: 0 0 8px;
}

.mode-section + .mode-section {
  margin-top: 4px;
}

/* Sticky so "System Modes" / "Your Modes" stay put while their rows scroll under them. */
.section-header {
  position: sticky;
  top: 0;
  z-index: 2;
  display: flex;
  align-items: baseline;
  gap: 8px;
  padding: 10px 18px 8px;
  background: var(--mm-surface);
  border-bottom: 1px solid var(--mm-border-soft);
}

.section-title {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--mm-muted);
}

.section-count {
  padding: 1px 7px;
  border-radius: 999px;
  background: #e6eaef;
  font-size: 11px;
  font-weight: 600;
  color: var(--mm-muted);
  font-variant-numeric: tabular-nums;
}

.section-description {
  margin: 0 0 0 auto;
  font-size: 11.5px;
  color: var(--mm-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mode-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.mode-item {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 14px;
  padding: 11px 18px;
  background: #fff;
  border-bottom: 1px solid var(--mm-border-soft);
  transition: background 0.15s;
}

.mode-item:hover {
  background: var(--mm-accent-soft);
}

.mode-info {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.mode-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.mode-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--mm-text);
}

/*
 * Two lines by default instead of one hard-truncated line. For the custom modes the distinguishing
 * half of the sentence was exactly the half being cut, which is why eight different modes all read
 * the same. `title` carries the whole string, and Show more drops the clamp entirely.
 */
.mode-description {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.45;
  color: var(--mm-muted);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.mode-description.expanded {
  display: block;
  overflow: visible;
}

.description-toggle {
  align-self: flex-start;
  padding: 0;
  border: 0;
  background: none;
  color: var(--mm-accent);
  font-size: 12px;
  font-family: inherit;
  cursor: pointer;
}

.description-toggle:hover {
  text-decoration: underline;
}

.description-toggle:focus-visible {
  outline: 2px solid var(--mm-accent);
  outline-offset: 2px;
  border-radius: 3px;
}

.mode-tools {
  flex: none;
  font-size: 11px;
  color: var(--mm-muted);
  background: var(--mm-surface);
  border: 1px solid var(--mm-border-soft);
  padding: 1px 8px;
  border-radius: 999px;
  white-space: nowrap;
}

.mode-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.action-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  padding: 0;
  background: #fff;
  border: 1px solid var(--mm-border);
  border-radius: 6px;
  color: #475569;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s, color 0.15s;
}

.action-btn svg {
  width: 17px;
  height: 17px;
}

.action-btn:hover {
  background: var(--mm-accent-soft);
  border-color: var(--mm-accent);
  color: var(--mm-accent);
}

.action-btn:focus-visible {
  outline: 2px solid var(--mm-accent);
  outline-offset: 2px;
}

.action-divider {
  width: 1px;
  height: 18px;
  margin: 0 3px;
  background: var(--mm-border);
}

.action-btn.danger:hover {
  background: var(--mm-danger-soft);
  border-color: #e6b7bb;
  color: var(--mm-danger);
}

.no-modes {
  margin: 12px 18px;
  padding: 22px;
  text-align: center;
  font-size: 13px;
  color: var(--mm-muted);
  background: #fff;
  border: 1px dashed var(--mm-border);
  border-radius: 8px;
}

.modal-footer {
  flex: none;
  padding: 12px 18px;
  border-top: 1px solid var(--mm-border-soft);
  display: flex;
  justify-content: flex-end;
  background: #fff;
}

.btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 7px;
  padding: 9px 16px;
  border: 1px solid transparent;
  border-radius: 6px;
  font-size: 13.5px;
  font-weight: 500;
  font-family: inherit;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s, opacity 0.15s;
}

.btn svg {
  width: 16px;
  height: 16px;
}

.btn:focus-visible {
  outline: 2px solid var(--mm-accent);
  outline-offset: 2px;
}

.btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.btn-primary {
  background: var(--mm-accent);
  color: #fff;
}

.btn-primary:hover:not(:disabled) {
  background: #2559b8;
}

.btn-secondary {
  background: #fff;
  border-color: var(--mm-border);
  color: var(--mm-text);
}

.btn-secondary:hover:not(:disabled) {
  background: var(--mm-surface);
  border-color: #aeb7c2;
}

.btn-danger {
  background: var(--mm-danger);
  color: #fff;
}

.btn-danger:hover:not(:disabled) {
  background: #991c26;
}

/* Dialog styles */
.dialog-overlay {
  position: fixed;
  inset: 0;
  background: rgb(15 23 42 / 40%);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1100;
  padding: 20px;
}

.dialog {
  background: #fff;
  border: 1px solid var(--mm-border);
  border-radius: 12px;
  box-shadow: 0 16px 40px rgb(15 23 42 / 25%);
  padding: 20px;
  width: 100%;
  max-width: 420px;
}

.dialog-heading {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 10px;
}

.dialog-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
}

.dialog-icon svg {
  width: 19px;
  height: 19px;
}

.dialog-icon.danger {
  background: var(--mm-danger-soft);
  color: var(--mm-danger);
}

.dialog-title {
  margin: 0 0 10px;
  font-size: 16px;
  font-weight: 600;
}

.dialog-heading .dialog-title {
  margin: 0;
}

.dialog-text {
  margin: 0 0 18px;
  font-size: 13.5px;
  color: var(--mm-muted);
  line-height: 1.5;
}

.dialog .form-group {
  margin-bottom: 18px;
}

.dialog .form-label {
  display: block;
  margin-bottom: 6px;
  font-size: 13px;
  font-weight: 500;
  color: var(--mm-text);
}

.dialog .form-input {
  width: 100%;
  padding: 9px 11px;
  border: 1px solid var(--mm-border);
  border-radius: 6px;
  font-size: 13.5px;
  font-family: inherit;
  color: var(--mm-text);
}

.dialog .form-input:focus {
  outline: none;
  border-color: var(--mm-accent);
  box-shadow: 0 0 0 3px rgb(45 108 223 / 18%);
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}

@media (max-width: 768px) {
  .modal-backdrop {
    padding: 0;
  }

  .modal-container {
    max-width: 100%;
    max-height: 100%;
    height: 100%;
    border: 0;
    border-radius: 0;
  }

  .modal-header,
  .modal-toolbar,
  .modal-footer {
    padding-inline: 14px;
  }

  .section-header,
  .mode-item {
    padding-inline: 14px;
  }

  .section-description {
    display: none;
  }

  .mode-item {
    flex-direction: column;
    align-items: stretch;
    gap: 10px;
  }

  .mode-actions {
    justify-content: flex-end;
  }

  .modal-footer .btn {
    width: 100%;
  }
}
</style>
