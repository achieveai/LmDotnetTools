<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue';
import type { ProviderDescriptor } from '@/types/providers';

const props = defineProps<{
  providers: ProviderDescriptor[];
  selectedProviderId: string | null;
  /**
   * Provider id locked to the current thread (set after the first message). When
   * provided, the selector renders as a read-only badge instead of a dropdown.
   */
  lockedProviderId?: string | null;
  isLoading?: boolean;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  'select-provider': [providerId: string];
}>();

const dropdownOpen = ref(false);
const dropdownRef = ref<HTMLElement | null>(null);

const isLocked = computed(() => !!props.lockedProviderId);

const lockedProvider = computed<ProviderDescriptor | null>(() => {
  if (!props.lockedProviderId) return null;
  return (
    props.providers.find((p) => p.id === props.lockedProviderId) ?? {
      id: props.lockedProviderId,
      displayName: props.lockedProviderId,
      available: false,
    }
  );
});

const selectedProvider = computed<ProviderDescriptor | null>(() =>
  props.providers.find((p) => p.id === props.selectedProviderId) ?? null
);

function toggleDropdown(): void {
  if (props.disabled || props.isLoading || isLocked.value) {
    return;
  }
  dropdownOpen.value = !dropdownOpen.value;
}

function closeDropdown(): void {
  dropdownOpen.value = false;
}

function handleSelect(providerId: string): void {
  if (props.disabled || isLocked.value) {
    return;
  }
  emit('select-provider', providerId);
  closeDropdown();
}

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
  () => [props.disabled, props.lockedProviderId] as const,
  ([isDisabled, locked]) => {
    if (isDisabled || locked) {
      closeDropdown();
    }
  }
);
</script>

<template>
  <div class="provider-selector" ref="dropdownRef" data-testid="provider-selector">
    <span
      v-if="isLocked"
      class="provider-badge"
      data-testid="provider-locked-badge"
      :title="`This conversation is locked to ${lockedProvider?.displayName ?? lockedProviderId}`"
    >
      <span class="badge-label">Provider:</span>
      <span class="badge-name">{{ lockedProvider?.displayName ?? lockedProviderId }}</span>
      <span class="badge-lock" aria-hidden="true">🔒</span>
    </span>
    <template v-else>
      <button
        class="selector-btn"
        :class="{ open: dropdownOpen }"
        data-testid="provider-selector-button"
        @click="toggleDropdown"
        :disabled="isLoading || disabled"
      >
        <span class="provider-label">Provider:</span>
        <span class="provider-name">{{ selectedProvider?.displayName ?? 'Loading...' }}</span>
        <span class="dropdown-arrow">{{ dropdownOpen ? '\u25B2' : '\u25BC' }}</span>
      </button>

      <div v-if="dropdownOpen" class="dropdown-menu">
        <button
          v-for="provider in providers"
          :key="provider.id"
          class="menu-item"
          :class="{ active: provider.id === selectedProviderId, unavailable: !provider.available }"
          :data-testid="`provider-option-${provider.id}`"
          :disabled="disabled || !provider.available"
          @click="handleSelect(provider.id)"
        >
          <span class="item-name">{{ provider.displayName }}</span>
          <span v-if="!provider.available" class="item-status">unavailable</span>
          <span v-else-if="provider.id === selectedProviderId" class="check-mark">✓</span>
        </button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.provider-selector {
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

.provider-label {
  color: #666;
}

.provider-name {
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
  min-width: 200px;
  background: white;
  border: 1px solid #ddd;
  border-radius: 8px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  z-index: 100;
  overflow: hidden;
  padding: 4px 0;
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

.menu-item:hover:not(:disabled) {
  background: #f8f9fa;
}

.menu-item:disabled,
.menu-item.unavailable {
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

.item-status {
  font-size: 11px;
  font-style: italic;
  color: #9aa0a6;
  margin-left: 8px;
  flex-shrink: 0;
}

.check-mark {
  color: #0d6efd;
  font-weight: bold;
  flex-shrink: 0;
  margin-left: 8px;
}

.provider-badge {
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
