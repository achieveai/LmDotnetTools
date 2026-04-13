<script setup lang="ts">
import { ref, computed, watch } from 'vue';
import type { ChatMode, ChatModeCreateUpdate, ToolDefinition } from '@/types/chatMode';
import ToolCheckboxList from './ToolCheckboxList.vue';

const props = defineProps<{
  mode?: ChatMode | null;
  tools: ToolDefinition[];
  isLoading?: boolean;
}>();

const emit = defineEmits<{
  save: [data: ChatModeCreateUpdate];
  cancel: [];
}>();

// Form state
const name = ref('');
const description = ref('');
const systemPrompt = ref('');
const enabledTools = ref<string[] | null>(null);

// Validation
const nameError = ref('');
const systemPromptError = ref('');

const isEditing = computed(() => !!props.mode);
const title = computed(() => (isEditing.value ? 'Edit Mode' : 'Create New Mode'));

// Initialize form when mode changes
watch(
  () => props.mode,
  (newMode) => {
    if (newMode) {
      name.value = newMode.name;
      description.value = newMode.description || '';
      systemPrompt.value = newMode.systemPrompt;
      enabledTools.value = newMode.enabledTools ? [...newMode.enabledTools] : null;
    } else {
      resetForm();
    }
  },
  { immediate: true }
);

function resetForm(): void {
  name.value = '';
  description.value = '';
  systemPrompt.value = '';
  enabledTools.value = null;
  nameError.value = '';
  systemPromptError.value = '';
}

function validate(): boolean {
  let valid = true;

  if (!name.value.trim()) {
    nameError.value = 'Name is required';
    valid = false;
  } else {
    nameError.value = '';
  }

  if (!systemPrompt.value.trim()) {
    systemPromptError.value = 'System prompt is required';
    valid = false;
  } else {
    systemPromptError.value = '';
  }

  return valid;
}

function handleSave(): void {
  if (!validate()) return;

  const data: ChatModeCreateUpdate = {
    name: name.value.trim(),
    description: description.value.trim() || undefined,
    systemPrompt: systemPrompt.value.trim(),
    enabledTools: enabledTools.value ?? undefined,
  };

  emit('save', data);
}

function handleCancel(): void {
  emit('cancel');
}
</script>

<template>
  <div class="mode-editor">
    <h2 class="editor-title">{{ title }}</h2>

    <form @submit.prevent="handleSave" class="editor-form">
      <div class="form-group">
        <label for="mode-name" class="form-label">
          Name <span class="required">*</span>
        </label>
        <input
          id="mode-name"
          v-model="name"
          type="text"
          class="form-input"
          :class="{ error: nameError }"
          placeholder="Enter mode name"
          :disabled="isLoading"
        />
        <span v-if="nameError" class="error-message">{{ nameError }}</span>
      </div>

      <div class="form-group">
        <label for="mode-description" class="form-label">Description</label>
        <textarea
          id="mode-description"
          v-model="description"
          class="form-textarea"
          placeholder="Optional description of what this mode does"
          rows="2"
          :disabled="isLoading"
        ></textarea>
      </div>

      <div class="form-group">
        <label for="mode-prompt" class="form-label">
          System Prompt <span class="required">*</span>
        </label>
        <textarea
          id="mode-prompt"
          v-model="systemPrompt"
          class="form-textarea system-prompt"
          :class="{ error: systemPromptError }"
          placeholder="Enter the system prompt for this mode..."
          rows="6"
          :disabled="isLoading"
        ></textarea>
        <span v-if="systemPromptError" class="error-message">{{ systemPromptError }}</span>
      </div>

      <div class="form-group">
        <label class="form-label">Enabled Tools</label>
        <ToolCheckboxList
          v-model="enabledTools"
          :tools="tools"
          :disabled="isLoading"
        />
      </div>

      <div class="form-actions">
        <button
          type="button"
          class="btn btn-secondary"
          :disabled="isLoading"
          @click="handleCancel"
        >
          Cancel
        </button>
        <button
          type="submit"
          class="btn btn-primary"
          :disabled="isLoading"
        >
          {{ isLoading ? 'Saving...' : 'Save' }}
        </button>
      </div>
    </form>
  </div>
</template>

<style scoped>
.mode-editor {
  padding: 20px;
}

.editor-title {
  margin: 0 0 20px;
  font-size: 20px;
  font-weight: 600;
  color: #333;
}

.editor-form {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.form-label {
  font-size: 14px;
  font-weight: 500;
  color: #333;
}

.required {
  color: #dc3545;
}

.form-input,
.form-textarea {
  padding: 10px 12px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
  font-family: inherit;
  transition: border-color 0.2s, box-shadow 0.2s;
}

.form-input:focus,
.form-textarea:focus {
  outline: none;
  border-color: #0d6efd;
  box-shadow: 0 0 0 2px rgba(13, 110, 253, 0.25);
}

.form-input.error,
.form-textarea.error {
  border-color: #dc3545;
}

.form-input.error:focus,
.form-textarea.error:focus {
  box-shadow: 0 0 0 2px rgba(220, 53, 69, 0.25);
}

.form-textarea {
  resize: vertical;
  min-height: 60px;
}

.system-prompt {
  font-family: 'Monaco', 'Menlo', 'Ubuntu Mono', monospace;
  font-size: 13px;
  line-height: 1.5;
}

.error-message {
  font-size: 12px;
  color: #dc3545;
}

.form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
  padding-top: 12px;
  border-top: 1px solid #eee;
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
</style>
