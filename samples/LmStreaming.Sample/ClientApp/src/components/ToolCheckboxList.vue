<script setup lang="ts">
import { computed, ref } from 'vue';
import type { ToolDefinition } from '@/types/chatMode';

const props = defineProps<{
  tools: ToolDefinition[];
  modelValue: string[] | null;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  'update:modelValue': [value: string[] | null];
}>();

const searchQuery = ref('');

// Filter tools by search query
const filteredTools = computed(() => {
  if (!searchQuery.value) return props.tools;
  const query = searchQuery.value.toLowerCase();
  return props.tools.filter(
    (tool) =>
      tool.name.toLowerCase().includes(query) ||
      tool.description?.toLowerCase().includes(query)
  );
});

// Check if all tools are enabled (modelValue is null)
const allToolsEnabled = computed(() => props.modelValue === null);

// Check if a specific tool is enabled
function isToolEnabled(toolName: string): boolean {
  if (props.modelValue === null) return true;
  return props.modelValue.includes(toolName);
}

// Toggle a specific tool
function toggleTool(toolName: string): void {
  if (props.disabled) return;

  if (props.modelValue === null) {
    // Currently all enabled, switch to all except this one
    emit(
      'update:modelValue',
      props.tools.filter((t) => t.name !== toolName).map((t) => t.name)
    );
  } else if (props.modelValue.includes(toolName)) {
    // Remove tool
    const newValue = props.modelValue.filter((t) => t !== toolName);
    emit('update:modelValue', newValue.length === 0 ? null : newValue);
  } else {
    // Add tool
    const newValue = [...props.modelValue, toolName];
    // If all tools are now selected, switch to null (all enabled)
    if (newValue.length === props.tools.length) {
      emit('update:modelValue', null);
    } else {
      emit('update:modelValue', newValue);
    }
  }
}

// Select all tools
function selectAll(): void {
  if (props.disabled) return;
  emit('update:modelValue', null);
}

// Deselect all tools
function deselectAll(): void {
  if (props.disabled) return;
  emit('update:modelValue', []);
}
</script>

<template>
  <div class="tool-checkbox-list">
    <div class="tool-actions">
      <button
        type="button"
        class="action-btn"
        :disabled="disabled || allToolsEnabled"
        @click="selectAll"
      >
        Select All
      </button>
      <button
        type="button"
        class="action-btn"
        :disabled="disabled || (modelValue !== null && modelValue.length === 0)"
        @click="deselectAll"
      >
        Deselect All
      </button>
    </div>

    <div class="search-box">
      <input
        v-model="searchQuery"
        type="text"
        placeholder="Search tools..."
        class="search-input"
      />
    </div>

    <div v-if="tools.length === 0" class="no-tools">
      No tools available
    </div>

    <div v-else-if="filteredTools.length === 0" class="no-tools">
      No tools match "{{ searchQuery }}"
    </div>

    <ul v-else class="tool-list">
      <li v-for="tool in filteredTools" :key="tool.name" class="tool-item">
        <label class="tool-label" :class="{ disabled }">
          <input
            type="checkbox"
            :checked="isToolEnabled(tool.name)"
            :disabled="disabled"
            @change="toggleTool(tool.name)"
          />
          <div class="tool-info">
            <span class="tool-name">{{ tool.name }}</span>
            <span v-if="tool.description" class="tool-description">
              {{ tool.description }}
            </span>
          </div>
        </label>
      </li>
    </ul>

    <div class="selection-summary">
      <span v-if="allToolsEnabled">All tools enabled</span>
      <span v-else-if="modelValue && modelValue.length > 0">
        {{ modelValue.length }} of {{ tools.length }} tools enabled
      </span>
      <span v-else>No tools enabled</span>
    </div>
  </div>
</template>

<style scoped>
.tool-checkbox-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.tool-actions {
  display: flex;
  gap: 8px;
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

.action-btn:hover:not(:disabled) {
  background: #e9ecef;
}

.action-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.search-box {
  position: relative;
}

.search-input {
  width: 100%;
  padding: 8px 12px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
}

.search-input:focus {
  outline: none;
  border-color: #0d6efd;
  box-shadow: 0 0 0 2px rgba(13, 110, 253, 0.25);
}

.no-tools {
  padding: 16px;
  text-align: center;
  color: #666;
  background: #f8f9fa;
  border-radius: 4px;
}

.tool-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 200px;
  overflow-y: auto;
  border: 1px solid #ddd;
  border-radius: 4px;
}

.tool-item {
  border-bottom: 1px solid #eee;
}

.tool-item:last-child {
  border-bottom: none;
}

.tool-label {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 10px 12px;
  cursor: pointer;
  transition: background 0.2s;
}

.tool-label:hover:not(.disabled) {
  background: #f8f9fa;
}

.tool-label.disabled {
  cursor: not-allowed;
  opacity: 0.7;
}

.tool-label input[type='checkbox'] {
  margin-top: 2px;
  flex-shrink: 0;
}

.tool-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.tool-name {
  font-weight: 500;
  font-size: 14px;
  color: #333;
}

.tool-description {
  font-size: 12px;
  color: #666;
  line-height: 1.4;
}

.selection-summary {
  font-size: 12px;
  color: #666;
  text-align: right;
}
</style>
