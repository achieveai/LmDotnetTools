<script setup lang="ts">
import { provide } from 'vue';
import { useChat } from '@/composables/useChat';
import MessageList from './MessageList.vue';
import ChatInput from './ChatInput.vue';

const { displayItems, isLoading, error, usage, sendMessage, clearMessages, getResultForToolCall } = useChat();

// Provide getResultForToolCall to child components (MessageList -> MetadataPill)
provide('getResultForToolCall', getResultForToolCall);

function handleSend(message: string) {
  sendMessage(message);
}
</script>

<template>
  <div class="chat-view">
    <header class="chat-header">
      <h1>LmStreaming Chat</h1>
      <button class="clear-btn" @click="clearMessages" :disabled="isLoading">
        Clear
      </button>
    </header>

    <MessageList :display-items="displayItems" />

    <div v-if="error" class="error-banner">
      {{ error }}
    </div>

    <div v-if="usage" class="usage-banner">
      Tokens: {{ usage.usage.inputTokens ?? usage.usage.prompt_tokens ?? 0 }} in /
      {{ usage.usage.outputTokens ?? usage.usage.completion_tokens ?? 0 }} out
    </div>

    <ChatInput :disabled="isLoading" @send="handleSend" />
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
