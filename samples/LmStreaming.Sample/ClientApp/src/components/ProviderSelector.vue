<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue';
import type { ProviderDescriptor } from '@/types/providers';

const props = defineProps<{
  providers: ProviderDescriptor[];
  selectedProviderId: string | null;
  isLoading?: boolean;
  /**
   * Disables the selector. The parent sets this while a run is streaming (provider is mutable only
   * when the conversation is idle); when idle the dropdown is editable and a pick switches the
   * conversation's provider on the backend.
   */
  disabled?: boolean;
}>();

const emit = defineEmits<{
  'select-provider': [providerId: string];
}>();

const dropdownOpen = ref(false);
const dropdownRef = ref<HTMLElement | null>(null);

const selectedProvider = computed<ProviderDescriptor | null>(() =>
  props.providers.find((p) => p.id === props.selectedProviderId) ?? null
);

/**
 * Renders the dropdown as ungrouped entries first (in server order) followed by one section per
 * partition group (e.g. "Copilot · Anthropic", "Copilot · OpenAI"). Group order follows first
 * appearance in the provider list, so the server controls partition ordering.
 */
interface ProviderGroup {
  label: string | null;
  providers: ProviderDescriptor[];
}

const groupedProviders = computed<ProviderGroup[]>(() => {
  const ungrouped: ProviderDescriptor[] = [];
  const groups = new Map<string, ProviderDescriptor[]>();

  for (const provider of props.providers) {
    const group = provider.group;
    if (!group) {
      ungrouped.push(provider);
      continue;
    }
    const bucket = groups.get(group);
    if (bucket) {
      bucket.push(provider);
    } else {
      groups.set(group, [provider]);
    }
  }

  const result: ProviderGroup[] = [];
  if (ungrouped.length > 0) {
    result.push({ label: null, providers: ungrouped });
  }
  for (const [label, providers] of groups) {
    result.push({ label, providers });
  }
  return result;
});

function toggleDropdown(): void {
  if (props.disabled || props.isLoading) {
    return;
  }
  dropdownOpen.value = !dropdownOpen.value;
}

function closeDropdown(): void {
  dropdownOpen.value = false;
}

function handleSelect(providerId: string): void {
  if (props.disabled) {
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
  () => props.disabled,
  (isDisabled) => {
    if (isDisabled) {
      closeDropdown();
    }
  }
);
</script>

<template>
  <div class="provider-selector" ref="dropdownRef" data-testid="provider-selector">
    <button
      class="selector-btn"
      :class="{ open: dropdownOpen }"
      data-testid="provider-selector-button"
      @click="toggleDropdown"
      :disabled="isLoading || disabled"
    >
      <span class="provider-label">Provider:</span>
      <span class="provider-name">{{ selectedProvider?.displayName ?? 'Loading...' }}</span>
      <span class="dropdown-arrow">{{ dropdownOpen ? '▲' : '▼' }}</span>
    </button>

    <div v-if="dropdownOpen" class="dropdown-menu">
      <template v-for="group in groupedProviders" :key="group.label ?? '__ungrouped__'">
        <div
          v-if="group.label"
          class="menu-group-header"
          :data-testid="`provider-group-${group.label}`"
          role="presentation"
        >
          {{ group.label }}
        </div>
        <button
          v-for="provider in group.providers"
          :key="provider.id"
          class="menu-item"
          :class="{ active: provider.id === selectedProviderId, unavailable: !provider.available }"
          :data-testid="`provider-option-${provider.id}`"
          :disabled="disabled || !provider.available"
          :title="provider.knownLimitation ?? undefined"
          @click="handleSelect(provider.id)"
        >
          <span class="item-name">{{ provider.displayName }}</span>
          <span
            v-if="provider.available && provider.knownLimitation"
            class="item-warning"
            :data-testid="`provider-warning-${provider.id}`"
            aria-label="known limitation"
          >⚠</span>
          <span v-if="!provider.available" class="item-status">unavailable</span>
          <span v-else-if="provider.id === selectedProviderId" class="check-mark">✓</span>
        </button>
      </template>
    </div>
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
  /* Cap the height so a long (dynamically discovered) model list scrolls instead of
     running off-screen. Viewport-relative with a fixed upper bound so it adapts to
     short viewports too. */
  max-height: min(60vh, 320px);
  overflow-y: auto;
  overscroll-behavior: contain;
  background: white;
  border: 1px solid #ddd;
  border-radius: 8px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  z-index: 100;
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

.menu-group-header {
  position: sticky;
  top: 0;
  background: white;
  padding: 6px 12px 2px;
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: #6c757d;
  user-select: none;
}

.menu-group-header:not(:first-child) {
  margin-top: 4px;
  border-top: 1px solid #eee;
  padding-top: 8px;
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

.item-warning {
  font-size: 13px;
  color: #b8860b;
  margin-left: 8px;
  flex-shrink: 0;
  cursor: help;
}

.check-mark {
  color: #0d6efd;
  font-weight: bold;
  flex-shrink: 0;
  margin-left: 8px;
}
</style>
