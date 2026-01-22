<script setup lang="ts">
import { ref, computed } from 'vue';
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
const deleteConfirmMode = ref<ChatMode | null>(null);

const systemModes = computed(() => props.modes.filter((m) => m.isSystemDefined));
const userModes = computed(() => props.modes.filter((m) => !m.isSystemDefined));

function getToolCount(mode: ChatMode): string {
  if (!mode.enabledTools) return 'All tools';
  if (mode.enabledTools.length === 0) return 'No tools';
  return `${mode.enabledTools.length} tool${mode.enabledTools.length !== 1 ? 's' : ''}`;
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
  if (currentView.value === 'edit' && editingMode.value) {
    emit('update', editingMode.value.id, data);
  } else {
    emit('create', data);
  }
  currentView.value = 'list';
  editingMode.value = null;
}

function handleCancelEdit(): void {
  currentView.value = 'list';
  editingMode.value = null;
}

function handleClose(): void {
  emit('close');
}

function handleBackdropClick(event: MouseEvent): void {
  if (event.target === event.currentTarget) {
    handleClose();
  }
}
</script>

<template>
  <div class="modal-backdrop" @click="handleBackdropClick">
    <div class="modal-container">
      <div class="modal-header">
        <h2 class="modal-title">
          {{ currentView === 'list' ? 'Manage Modes' : '' }}
        </h2>
        <button class="close-btn" @click="handleClose" title="Close">
          &times;
        </button>
      </div>

      <div class="modal-content">
        <!-- List View -->
        <template v-if="currentView === 'list'">
          <div class="mode-sections">
            <!-- System Modes -->
            <section class="mode-section">
              <h3 class="section-title">System Modes</h3>
              <p class="section-description">Built-in modes that cannot be modified</p>
              <ul class="mode-list">
                <li v-for="mode in systemModes" :key="mode.id" class="mode-item">
                  <div class="mode-info">
                    <span class="mode-name">{{ mode.name }}</span>
                    <span v-if="mode.description" class="mode-description">
                      {{ mode.description }}
                    </span>
                    <span class="mode-tools">{{ getToolCount(mode) }}</span>
                  </div>
                  <div class="mode-actions">
                    <button
                      class="action-btn"
                      @click="handleCopy(mode)"
                      title="Create a copy"
                    >
                      Copy
                    </button>
                  </div>
                </li>
              </ul>
            </section>

            <!-- User Modes -->
            <section class="mode-section">
              <h3 class="section-title">Your Modes</h3>
              <p class="section-description">Custom modes you've created</p>
              <ul v-if="userModes.length > 0" class="mode-list">
                <li v-for="mode in userModes" :key="mode.id" class="mode-item">
                  <div class="mode-info">
                    <span class="mode-name">{{ mode.name }}</span>
                    <span v-if="mode.description" class="mode-description">
                      {{ mode.description }}
                    </span>
                    <span class="mode-tools">{{ getToolCount(mode) }}</span>
                  </div>
                  <div class="mode-actions">
                    <button
                      class="action-btn"
                      @click="handleEdit(mode)"
                      title="Edit mode"
                    >
                      Edit
                    </button>
                    <button
                      class="action-btn"
                      @click="handleCopy(mode)"
                      title="Create a copy"
                    >
                      Copy
                    </button>
                    <button
                      class="action-btn danger"
                      @click="handleDelete(mode)"
                      title="Delete mode"
                    >
                      Delete
                    </button>
                  </div>
                </li>
              </ul>
              <div v-else class="no-modes">
                No custom modes yet. Create one to get started!
              </div>
            </section>
          </div>

          <div class="modal-footer">
            <button class="btn btn-primary" @click="handleCreateNew">
              Create New Mode
            </button>
          </div>
        </template>

        <!-- Create/Edit View -->
        <template v-else>
          <ModeEditor
            :mode="editingMode"
            :tools="tools"
            :is-loading="isLoading"
            @save="handleSave"
            @cancel="handleCancelEdit"
          />
        </template>
      </div>

      <!-- Copy Dialog -->
      <div v-if="copyDialogVisible" class="dialog-overlay" @click.self="handleCancelCopy">
        <div class="dialog">
          <h3 class="dialog-title">Copy Mode</h3>
          <p class="dialog-text">
            Create a copy of "{{ copySourceMode?.name }}"
          </p>
          <div class="form-group">
            <label for="copy-name" class="form-label">New Name</label>
            <input
              id="copy-name"
              v-model="copyNewName"
              type="text"
              class="form-input"
              placeholder="Enter name for the copy"
            />
          </div>
          <div class="dialog-actions">
            <button class="btn btn-secondary" @click="handleCancelCopy">
              Cancel
            </button>
            <button
              class="btn btn-primary"
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
        <div class="dialog">
          <h3 class="dialog-title">Delete Mode</h3>
          <p class="dialog-text">
            Are you sure you want to delete "{{ deleteConfirmMode.name }}"?
            This action cannot be undone.
          </p>
          <div class="dialog-actions">
            <button class="btn btn-secondary" @click="handleCancelDelete">
              Cancel
            </button>
            <button class="btn btn-danger" @click="handleConfirmDelete">
              Delete
            </button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.modal-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 20px;
}

.modal-container {
  background: white;
  border-radius: 12px;
  box-shadow: 0 20px 50px rgba(0, 0, 0, 0.2);
  width: 100%;
  max-width: 600px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px;
  border-bottom: 1px solid #eee;
}

.modal-title {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: #333;
}

.close-btn {
  width: 32px;
  height: 32px;
  padding: 0;
  background: transparent;
  border: none;
  border-radius: 4px;
  font-size: 24px;
  line-height: 1;
  color: #666;
  cursor: pointer;
  transition: background 0.2s, color 0.2s;
}

.close-btn:hover {
  background: #f8f9fa;
  color: #333;
}

.modal-content {
  flex: 1;
  overflow-y: auto;
  padding: 0;
}

.mode-sections {
  padding: 20px;
}

.mode-section {
  margin-bottom: 24px;
}

.mode-section:last-child {
  margin-bottom: 0;
}

.section-title {
  margin: 0 0 4px;
  font-size: 14px;
  font-weight: 600;
  color: #333;
}

.section-description {
  margin: 0 0 12px;
  font-size: 12px;
  color: #666;
}

.mode-list {
  list-style: none;
  margin: 0;
  padding: 0;
  border: 1px solid #ddd;
  border-radius: 8px;
}

.mode-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-bottom: 1px solid #eee;
  gap: 16px;
}

.mode-item:last-child {
  border-bottom: none;
}

.mode-info {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.mode-name {
  font-weight: 500;
  color: #333;
}

.mode-description {
  font-size: 12px;
  color: #666;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mode-tools {
  font-size: 11px;
  color: #888;
  background: #f0f0f0;
  padding: 2px 8px;
  border-radius: 10px;
  display: inline-block;
  margin-top: 4px;
  width: fit-content;
}

.mode-actions {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
}

.action-btn {
  padding: 6px 12px;
  background: #f8f9fa;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 12px;
  cursor: pointer;
  transition: background 0.2s;
}

.action-btn:hover {
  background: #e9ecef;
}

.action-btn.danger {
  color: #dc3545;
}

.action-btn.danger:hover {
  background: #f8d7da;
}

.no-modes {
  padding: 24px;
  text-align: center;
  color: #666;
  background: #f8f9fa;
  border-radius: 8px;
}

.modal-footer {
  padding: 16px 20px;
  border-top: 1px solid #eee;
  display: flex;
  justify-content: flex-end;
}

.btn {
  padding: 10px 20px;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.2s, opacity 0.2s;
}

.btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.btn-primary {
  background: #0d6efd;
  color: white;
}

.btn-primary:hover:not(:disabled) {
  background: #0b5ed7;
}

.btn-secondary {
  background: #6c757d;
  color: white;
}

.btn-secondary:hover:not(:disabled) {
  background: #5a6268;
}

.btn-danger {
  background: #dc3545;
  color: white;
}

.btn-danger:hover:not(:disabled) {
  background: #c82333;
}

/* Dialog styles */
.dialog-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.4);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1100;
}

.dialog {
  background: white;
  border-radius: 12px;
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.2);
  padding: 24px;
  width: 90%;
  max-width: 400px;
}

.dialog-title {
  margin: 0 0 12px;
  font-size: 18px;
  font-weight: 600;
  color: #333;
}

.dialog-text {
  margin: 0 0 20px;
  color: #666;
  line-height: 1.5;
}

.dialog .form-group {
  margin-bottom: 20px;
}

.dialog .form-label {
  display: block;
  margin-bottom: 6px;
  font-size: 14px;
  font-weight: 500;
  color: #333;
}

.dialog .form-input {
  width: 100%;
  padding: 10px 12px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
}

.dialog .form-input:focus {
  outline: none;
  border-color: #0d6efd;
  box-shadow: 0 0 0 2px rgba(13, 110, 253, 0.25);
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
}

@media (max-width: 768px) {
  .modal-container {
    max-width: 100%;
    max-height: 100%;
    border-radius: 0;
  }

  .modal-backdrop {
    padding: 0;
  }

  .mode-item {
    flex-direction: column;
    align-items: flex-start;
  }

  .mode-actions {
    margin-top: 8px;
  }
}
</style>
