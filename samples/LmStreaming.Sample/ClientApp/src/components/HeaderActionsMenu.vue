<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue';

const props = defineProps<{
  filesDisabled?: boolean;
  shareDisabled?: boolean;
  clearDisabled?: boolean;
}>();

const emit = defineEmits<{
  openMarketplaces: [];
  openEgress: [];
  openFiles: [];
  openShare: [];
  clear: [];
}>();

const open = ref(false);
const rootEl = ref<HTMLElement | null>(null);
const triggerEl = ref<HTMLButtonElement | null>(null);
const itemEls = ref<HTMLButtonElement[]>([]);

function setItemRef(element: unknown, index: number): void {
  if (element instanceof HTMLButtonElement) itemEls.value[index] = element;
}

function enabledItems(): HTMLButtonElement[] {
  return itemEls.value.filter((item) => item && !item.disabled);
}

function focusItem(position: 'first' | 'last'): void {
  void nextTick(() => {
    const items = enabledItems();
    items[position === 'first' ? 0 : items.length - 1]?.focus();
  });
}

function openMenu(position: 'first' | 'last' = 'first'): void {
  open.value = true;
  focusItem(position);
}

function closeMenu(restoreFocus = false): void {
  open.value = false;
  if (restoreFocus) void nextTick(() => triggerEl.value?.focus());
}

function toggleMenu(): void {
  if (open.value) closeMenu(true);
  else openMenu();
}

function onTriggerKeydown(event: KeyboardEvent): void {
  if (event.key === 'ArrowUp') {
    event.preventDefault();
    openMenu('last');
  } else if (['Enter', ' ', 'ArrowDown'].includes(event.key)) {
    event.preventDefault();
    openMenu('first');
  }
}

function onMenuKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    closeMenu(true);
    return;
  }
  if (event.key === 'Tab') {
    triggerEl.value?.focus();
    closeMenu();
    return;
  }
  if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
  event.preventDefault();
  const items = enabledItems();
  if (items.length === 0) return;
  if (event.key === 'Home') return items[0].focus();
  if (event.key === 'End') return items[items.length - 1].focus();
  const current = items.indexOf(document.activeElement as HTMLButtonElement);
  const delta = event.key === 'ArrowDown' ? 1 : -1;
  items[(current + delta + items.length) % items.length].focus();
}

function activate(event: 'openMarketplaces' | 'openEgress' | 'openFiles' | 'openShare' | 'clear'): void {
  closeMenu(event === 'clear');
  if (event === 'openMarketplaces') emit('openMarketplaces');
  else if (event === 'openEgress') emit('openEgress');
  else if (event === 'openFiles') emit('openFiles');
  else if (event === 'openShare') emit('openShare');
  else emit('clear');
}

function onDocumentClick(event: MouseEvent): void {
  if (open.value && rootEl.value && !rootEl.value.contains(event.target as Node)) closeMenu();
}

function focusTrigger(): void {
  triggerEl.value?.focus();
}

defineExpose({ focusTrigger });

onMounted(() => document.addEventListener('click', onDocumentClick));
onBeforeUnmount(() => document.removeEventListener('click', onDocumentClick));
</script>

<template>
  <div ref="rootEl" class="header-actions-menu">
    <button
      id="header-actions-trigger"
      ref="triggerEl"
      type="button"
      class="header-actions-trigger"
      data-testid="header-actions-menu-button"
      aria-haspopup="menu"
      :aria-expanded="open"
      aria-controls="header-actions-menu"
      @click="toggleMenu"
      @keydown="onTriggerKeydown"
    >
      More
      <span aria-hidden="true">▾</span>
    </button>

    <div
      v-if="open"
      id="header-actions-menu"
      class="header-actions-popup"
      data-testid="header-actions-menu"
      role="menu"
      aria-labelledby="header-actions-trigger"
      @keydown="onMenuKeydown"
    >
      <button :ref="(el) => setItemRef(el, 0)" role="menuitem" tabindex="-1" data-testid="marketplace-button" @click="activate('openMarketplaces')">Marketplaces</button>
      <button :ref="(el) => setItemRef(el, 1)" role="menuitem" tabindex="-1" data-testid="egress-auth-button" @click="activate('openEgress')">Egress auth</button>
      <button :ref="(el) => setItemRef(el, 2)" role="menuitem" tabindex="-1" data-testid="file-browser-button" :disabled="props.filesDisabled" @click="activate('openFiles')">Files</button>
      <button :ref="(el) => setItemRef(el, 3)" role="menuitem" tabindex="-1" data-testid="share-button" :disabled="props.shareDisabled" @click="activate('openShare')">Share</button>
      <div class="header-actions-divider" role="separator" />
      <button :ref="(el) => setItemRef(el, 4)" class="danger" role="menuitem" tabindex="-1" data-testid="clear-button" :disabled="props.clearDisabled" @click="activate('clear')">Clear conversation</button>
    </div>
  </div>
</template>

<style scoped>
.header-actions-menu {
  position: relative;
  flex: none;
}

.header-actions-trigger {
  display: inline-flex;
  height: 34px;
  box-sizing: border-box;
  align-items: center;
  gap: 7px;
  padding: 7px 12px;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  background: #fff;
  color: #394553;
  font-size: 14px;
  line-height: 18px;
  cursor: pointer;
}

.header-actions-trigger:hover,
.header-actions-trigger[aria-expanded='true'] {
  border-color: #aeb7c2;
  background: #eef1f4;
}

.header-actions-trigger:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 2px;
}

.header-actions-popup {
  position: absolute;
  top: calc(100% + 6px);
  right: 0;
  z-index: 120;
  width: max-content;
  min-width: 190px;
  max-width: min(280px, calc(100vw - 24px));
  padding: 6px;
  border: 1px solid #d6dbe1;
  border-radius: 8px;
  background: #fff;
  box-shadow: 0 8px 24px rgb(0 0 0 / 15%);
}

.header-actions-popup button {
  display: block;
  width: 100%;
  padding: 9px 10px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: #303944;
  font-size: 14px;
  text-align: left;
  cursor: pointer;
}

.header-actions-popup button:hover:not(:disabled),
.header-actions-popup button:focus-visible {
  background: #eef3f9;
  outline: none;
}

.header-actions-popup button:disabled {
  color: #9aa1a9;
  cursor: not-allowed;
}

.header-actions-divider {
  height: 1px;
  margin: 5px 4px;
  background: #e4e7eb;
}

.header-actions-popup .danger {
  color: #b4232e;
}

.header-actions-popup .danger:hover:not(:disabled),
.header-actions-popup .danger:focus-visible {
  background: #fff0f1;
}
</style>
