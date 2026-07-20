<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, useId } from 'vue';

defineProps<{
  title: string;
  /** Base data-testid: the backdrop uses this value; the close button uses `${dataTestId}-close`. */
  dataTestId: string;
}>();

const emit = defineEmits<{ close: [] }>();

// Unique id so aria-labelledby resolves to THIS modal's title even with multiple modals mounted.
const titleId = useId();
const containerRef = ref<HTMLElement | null>(null);
// The element focused before the modal opened, restored on unmount so focus returns where it was.
let previouslyFocused: HTMLElement | null = null;

function handleClose(): void {
  emit('close');
}

function handleBackdropClick(event: MouseEvent): void {
  // Only a click on the backdrop itself (not a bubbled click from inner content) closes.
  if (event.target === event.currentTarget) {
    handleClose();
  }
}

/** All tabbable elements inside the container, in DOM order. */
function focusableElements(): HTMLElement[] {
  const container = containerRef.value;
  if (!container) {
    return [];
  }
  const selector =
    'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';
  // Exclude anything inside an [inert] subtree: a nested confirmation (e.g. the file browser's
  // delete/overwrite dialog) marks the background content inert, and the trap must then confine Tab to
  // the confirmation rather than cycling back through the inert controls behind it.
  return Array.from(container.querySelectorAll<HTMLElement>(selector)).filter(
    (el) => !el.closest('[inert]')
  );
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    handleClose();
    return;
  }
  if (event.key !== 'Tab') {
    return;
  }
  // Focus trap: wrap Tab from the last focusable to the first, and Shift+Tab from first to last.
  const focusable = focusableElements();
  if (focusable.length === 0) {
    event.preventDefault();
    containerRef.value?.focus();
    return;
  }
  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  const active = document.activeElement as HTMLElement | null;
  if (event.shiftKey) {
    if (active === first || !containerRef.value?.contains(active)) {
      event.preventDefault();
      last.focus();
    }
  } else {
    if (active === last || !containerRef.value?.contains(active)) {
      event.preventDefault();
      first.focus();
    }
  }
}

onMounted(() => {
  previouslyFocused = document.activeElement as HTMLElement | null;
  document.addEventListener('keydown', handleKeydown);
  // Move focus into the dialog so keyboard users start inside it and the trap has an anchor.
  containerRef.value?.focus();
});

onBeforeUnmount(() => {
  document.removeEventListener('keydown', handleKeydown);
  previouslyFocused?.focus?.();
});
</script>

<template>
  <div class="modal-backdrop" :data-testid="dataTestId" @click="handleBackdropClick">
    <div
      ref="containerRef"
      class="modal-container"
      role="dialog"
      aria-modal="true"
      :aria-labelledby="titleId"
      tabindex="-1"
    >
      <div class="modal-header">
        <h2 :id="titleId" class="modal-title">{{ title }}</h2>
        <button
          class="close-btn"
          :data-testid="`${dataTestId}-close`"
          title="Close"
          @click="handleClose"
        >
          &times;
        </button>
      </div>
      <div class="modal-content">
        <slot />
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
  max-width: 640px;
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
</style>
