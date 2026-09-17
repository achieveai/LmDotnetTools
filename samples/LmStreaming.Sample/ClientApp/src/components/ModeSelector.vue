<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, useId, watch } from 'vue';
import type { ChatMode, ChatModeCreateUpdate, ToolDefinition } from '@/types/chatMode';
import ModeManagementModal from './ModeManagementModal.vue';

const props = defineProps<{
  modes: ChatMode[];
  currentModeId: string;
  tools: ToolDefinition[];
  isLoading?: boolean;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  'select-mode': [modeId: string];
  'create-mode': [data: ChatModeCreateUpdate];
  'update-mode': [modeId: string, data: ChatModeCreateUpdate];
  'delete-mode': [modeId: string];
  'copy-mode': [modeId: string, newName: string];
}>();

const dropdownOpen = ref(false);
const modalOpen = ref(false);
const dropdownRef = ref<HTMLElement | null>(null);
const menuId = useId();

const currentMode = computed(() =>
  props.modes.find((m) => m.id === props.currentModeId)
);

const systemModes = computed(() => props.modes.filter((m) => m.isSystemDefined));
const userModes = computed(() => props.modes.filter((m) => !m.isSystemDefined));

function toggleDropdown(): void {
  if (props.disabled || props.isLoading) {
    return;
  }
  dropdownOpen.value = !dropdownOpen.value;
}

function closeDropdown(): void {
  dropdownOpen.value = false;
}

function handleSelectMode(modeId: string): void {
  if (props.disabled) {
    return;
  }
  emit('select-mode', modeId);
  closeDropdown();
}

function openManageModal(): void {
  if (props.disabled) {
    return;
  }
  closeDropdown();
  modalOpen.value = true;
}

function closeModal(): void {
  modalOpen.value = false;
}

function handleCreateMode(data: ChatModeCreateUpdate): void {
  emit('create-mode', data);
}

function handleUpdateMode(modeId: string, data: ChatModeCreateUpdate): void {
  emit('update-mode', modeId, data);
}

function handleDeleteMode(modeId: string): void {
  emit('delete-mode', modeId);
}

function handleCopyMode(modeId: string, newName: string): void {
  emit('copy-mode', modeId, newName);
}

// Close dropdown when clicking outside
function handleClickOutside(event: MouseEvent): void {
  if (dropdownRef.value && !dropdownRef.value.contains(event.target as Node)) {
    closeDropdown();
  }
}

// Close dropdown on escape key
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
  () => props.disabled,
  (isDisabled) => {
    if (isDisabled) {
      closeDropdown();
      modalOpen.value = false;
    }
  }
);
</script>

<template>
  <div class="mode-selector" ref="dropdownRef" data-testid="mode-selector">
    <button
      class="selector-btn"
      :class="{ open: dropdownOpen }"
      data-testid="mode-selector-button"
      :aria-label="`Select mode, current: ${currentMode?.name ?? 'Loading'}`"
      :title="`Select mode, current: ${currentMode?.name ?? 'Loading'}`"
      :aria-expanded="dropdownOpen"
      :aria-controls="menuId"
      @click="toggleDropdown"
      :disabled="isLoading || disabled"
    >
      <svg class="mode-icon" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
        <path d="M3 4h10M5.5 8h5M3 12h10" />
        <circle cx="6" cy="4" r="1" />
        <circle cx="9" cy="8" r="1" />
        <circle cx="7" cy="12" r="1" />
      </svg>
      <span class="mode-name">{{ currentMode?.name ?? 'Loading...' }}</span>
      <span class="dropdown-arrow">{{ dropdownOpen ? '\u25B2' : '\u25BC' }}</span>
    </button>

    <div v-if="dropdownOpen" :id="menuId" class="dropdown-menu">
      <!-- System Modes -->
      <div v-if="systemModes.length > 0" class="menu-section">
        <div class="section-header">System</div>
        <button
          v-for="mode in systemModes"
          :key="mode.id"
          class="menu-item"
          :class="{ active: mode.id === currentModeId }"
          :data-testid="`mode-option-${mode.id}`"
          @click="handleSelectMode(mode.id)"
          :disabled="disabled"
        >
          <span class="item-name">{{ mode.name }}</span>
          <span v-if="mode.id === currentModeId" class="check-mark">✓</span>
        </button>
      </div>

      <!-- User Modes -->
      <div v-if="userModes.length > 0" class="menu-section">
        <div class="section-header">Your Modes</div>
        <button
          v-for="mode in userModes"
          :key="mode.id"
          class="menu-item"
          :class="{ active: mode.id === currentModeId }"
          :data-testid="`mode-option-${mode.id}`"
          @click="handleSelectMode(mode.id)"
          :disabled="disabled"
        >
          <span class="item-name">{{ mode.name }}</span>
          <span v-if="mode.id === currentModeId" class="check-mark">✓</span>
        </button>
      </div>

      <div class="menu-divider"></div>

      <!-- Manage Modes -->
      <button class="menu-item manage-item" @click="openManageModal" :disabled="disabled">
        Manage Modes...
      </button>
    </div>

    <!-- Management Modal -->
    <ModeManagementModal
      v-if="modalOpen"
      :modes="modes"
      :tools="tools"
      :is-loading="isLoading"
      @close="closeModal"
      @create="handleCreateMode"
      @update="handleUpdateMode"
      @delete="handleDeleteMode"
      @copy="handleCopyMode"
    />
  </div>
</template>

<style scoped>
.mode-selector {
  position: relative;
  width: 100%;
  min-width: 0;
}

.selector-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-width: 0;
  padding: 6px 12px;
  background: #f8f9fa;
  border: 1px solid #ddd;
  border-radius: 6px;
  font-size: 13px;
  cursor: pointer;
  transition: background 0.2s, border-color 0.2s;
}

.mode-icon {
  width: 16px;
  height: 16px;
  flex: 0 0 16px;
  fill: white;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.15;
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

.mode-name {
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
  bottom: 100%;
  left: 0;
  max-width: calc(100vw - 32px);
  max-height: min(360px, calc(100vh - 120px));
  margin-bottom: 4px;
  min-width: 200px;
  background: white;
  border: 1px solid #ddd;
  border-radius: 8px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  z-index: 100;
  overflow-x: hidden;
  overflow-y: auto;
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

.menu-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding: 8px 12px;
  background: none;
  border: none;
  font-size: 14px;
  text-align: left;
  cursor: pointer;
  transition: background 0.15s;
}

.menu-item:hover {
  background: #f8f9fa;
}

.menu-item:disabled {
  color: #9aa0a6;
  cursor: not-allowed;
}

.menu-item:disabled:hover {
  background: transparent;
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
</style>
