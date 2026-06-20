<script setup lang="ts">
import { onMounted, onBeforeUnmount } from 'vue';
import MarketplaceBrowser from './MarketplaceBrowser.vue';

const emit = defineEmits<{ close: [] }>();

function handleClose(): void {
  emit('close');
}

function handleBackdropClick(event: MouseEvent): void {
  if (event.target === event.currentTarget) {
    handleClose();
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    handleClose();
  }
}

onMounted(() => document.addEventListener('keydown', handleKeydown));
onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown));
</script>

<template>
  <div class="modal-backdrop" data-testid="marketplace-modal" @click="handleBackdropClick">
    <div class="modal-container">
      <div class="modal-header">
        <h2 class="modal-title">Marketplaces</h2>
        <button
          class="close-btn"
          data-testid="marketplace-modal-close"
          title="Close"
          @click="handleClose"
        >
          &times;
        </button>
      </div>
      <div class="modal-content">
        <MarketplaceBrowser />
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
