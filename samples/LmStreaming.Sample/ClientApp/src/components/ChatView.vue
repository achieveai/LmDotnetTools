<script setup lang="ts">
import { provide } from 'vue';
import { useChat } from '@/composables/useChat';
import MessageList from './MessageList.vue';
import PendingMessageQueue from './PendingMessageQueue.vue';
import ChatInput from './ChatInput.vue';

const { displayItems, isLoading, isSending, error, cumulativeUsage, pendingMessages, sendMessage, clearMessages, cancelStream, getResultForToolCall } = useChat();

// Provide getResultForToolCall to child components (MessageList -> MetadataPill)
provide('getResultForToolCall', getResultForToolCall);

function handleSend(message: string) {
  sendMessage(message);
}

function handleCancel() {
  cancelStream();
}
</script>

<template>
  <div class="chat-view" data-testid="chat-view">
    <header class="chat-header">
      <h1>LmStreaming Chat</h1>
      <button class="clear-btn" data-testid="clear-button" @click="clearMessages" :disabled="isLoading">
        Clear
      </button>
    </header>

    <MessageList :display-items="displayItems" :is-loading="isLoading" />

    <div v-if="error" class="error-banner" data-testid="error-banner">
      {{ error }}
    </div>

    <div v-if="cumulativeUsage.totalTokens > 0" class="usage-banner">
      Total: {{ cumulativeUsage.totalTokens }} |
      In: {{ cumulativeUsage.promptTokens }} |
      Out: {{ cumulativeUsage.completionTokens }}
      <template v-if="cumulativeUsage.cachedTokens > 0">
        | Cached: {{ cumulativeUsage.cachedTokens }}
      </template>
      <template v-if="cumulativeUsage.cacheCreationTokens > 0">
        | Cache created: {{ cumulativeUsage.cacheCreationTokens }}
      </template>
    </div>

    <PendingMessageQueue :pending-messages="pendingMessages" />

    <ChatInput
      :disabled="isSending && !isLoading"
      :streaming="isLoading"
      @send="handleSend"
      @cancel="handleCancel"
    />
  </div>
</template>

<style scoped>
.chat-view {
  display: flex;
  flex-direction: column;
  height: 100vh;
  max-width: 800px;
  margin: 0 auto;
  background: #fff;
  box-shadow: 0 0 10px rgba(0, 0, 0, 0.1);
}

.chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px;
  border-bottom: 1px solid #e0e0e0;
  background: #f8f9fa;
}

.chat-header h1 {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
}

.clear-btn {
  padding: 8px 16px;
  background: #dc3545;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
  transition: background 0.2s;
}

.clear-btn:hover:not(:disabled) {
  background: #c82333;
}

.clear-btn:disabled {
  background: #ccc;
  cursor: not-allowed;
}

.error-banner {
  padding: 12px 16px;
  background: #f8d7da;
  color: #721c24;
  border-top: 1px solid #f5c6cb;
}

.usage-banner {
  padding: 8px 16px;
  background: #d4edda;
  color: #155724;
  border-top: 1px solid #c3e6cb;
  font-size: 12px;
}
</style>
