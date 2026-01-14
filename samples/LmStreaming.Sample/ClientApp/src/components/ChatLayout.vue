<script setup lang="ts">
import { ref, onMounted, provide } from 'vue';
import { useConversations } from '@/composables/useConversations';
import { useChat } from '@/composables/useChat';
import { updateConversationMetadata } from '@/api/conversationsApi';
import ConversationSidebar from './ConversationSidebar.vue';
import MessageList from './MessageList.vue';
import PendingMessageQueue from './PendingMessageQueue.vue';
import ChatInput from './ChatInput.vue';

const {
  conversations,
  currentThreadId,
  isLoading: conversationsLoading,
  loadConversations,
  createNewConversation,
  selectConversation,
  removeConversation,
  addOrUpdateConversation,
} = useConversations();

const {
  displayItems,
  isLoading: chatLoading,
  isSending,
  error,
  usage,
  pendingMessages,
  sendMessage,
  clearMessages,
  disconnectWebSocket,
  setThreadId,
  loadMessagesFromBackend,
  getResultForToolCall,
} = useChat();

// Provide getResultForToolCall to child components
provide('getResultForToolCall', getResultForToolCall);

const sidebarCollapsed = ref(false);

// Load conversations on mount
onMounted(async () => {
  await loadConversations();

  // If there are existing conversations, select the most recent one
  if (conversations.value.length > 0) {
    await handleSelectConversation(conversations.value[0].threadId);
  }
});

// Handle creating a new chat
async function handleNewChat(): Promise<void> {
  // Disconnect current WebSocket
  await disconnectWebSocket();
  clearMessages();

  // Create new thread (without adding to sidebar yet)
  const newThreadId = createNewConversation();
  setThreadId(newThreadId);
}

// Handle selecting an existing conversation
async function handleSelectConversation(threadId: string): Promise<void> {
  if (threadId === currentThreadId.value) return;

  // Disconnect current WebSocket
  await disconnectWebSocket();
  clearMessages();

  // Switch to selected conversation
  selectConversation(threadId);
  setThreadId(threadId);

  // Load existing messages
  try {
    await loadMessagesFromBackend(threadId);
  } catch (e) {
    console.error('Failed to load messages:', e);
  }
}

// Handle deleting a conversation
async function handleDeleteConversation(threadId: string): Promise<void> {
  try {
    await removeConversation(threadId);

    if (threadId === currentThreadId.value) {
      // If we deleted the current conversation, start a new one or select another
      if (conversations.value.length > 0) {
        await handleSelectConversation(conversations.value[0].threadId);
      } else {
        handleNewChat();
      }
    }
  } catch (e) {
    console.error('Failed to delete conversation:', e);
  }
}

// Handle sending a message
async function handleSend(text: string): Promise<void> {
  const isNewConversation = !conversations.value.find(
    (c) => c.threadId === currentThreadId.value
  );

  await sendMessage(text);

  // If this is a new conversation (first message), add it to the sidebar
  if (isNewConversation && currentThreadId.value) {
    const title = text.substring(0, 50);
    const preview = text.substring(0, 100);

    // Add to local sidebar immediately
    addOrUpdateConversation({
      threadId: currentThreadId.value,
      title,
      preview,
      lastUpdated: Date.now(),
    });

    // Update backend metadata asynchronously
    try {
      console.log('[ChatLayout] Calling updateConversationMetadata', { threadId: currentThreadId.value, title, preview });
      await updateConversationMetadata(currentThreadId.value, { title, preview });
      console.log('[ChatLayout] Metadata updated successfully');
    } catch (e) {
      console.error('Failed to update conversation metadata:', e);
    }
  }
}

// Handle toggling sidebar collapse
function handleToggleCollapse(): void {
  sidebarCollapsed.value = !sidebarCollapsed.value;
}

// Watch for mobile screen and auto-collapse
function checkMobile(): void {
  if (window.innerWidth <= 768) {
    sidebarCollapsed.value = true;
  }
}

onMounted(() => {
  checkMobile();
  window.addEventListener('resize', checkMobile);
});
</script>

<template>
  <div class="chat-layout">
    <ConversationSidebar
      :conversations="conversations"
      :current-thread-id="currentThreadId"
      :is-loading="conversationsLoading"
      :is-collapsed="sidebarCollapsed"
      @new-chat="handleNewChat"
      @select-conversation="handleSelectConversation"
      @delete-conversation="handleDeleteConversation"
      @toggle-collapse="handleToggleCollapse"
    />

    <main class="chat-main">
      <div class="chat-view">
        <header class="chat-header">
          <button
            v-if="sidebarCollapsed"
            class="menu-btn"
            @click="handleToggleCollapse"
            title="Open sidebar"
          >
            =
          </button>
          <h1>LmStreaming Chat</h1>
          <button
            class="clear-btn"
            @click="clearMessages"
            :disabled="chatLoading"
          >
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

        <PendingMessageQueue :pending-messages="pendingMessages" />

        <ChatInput :disabled="isSending" @send="handleSend" />
      </div>
    </main>
  </div>
</template>

<style scoped>
.chat-layout {
  display: flex;
  height: 100vh;
  overflow: hidden;
}

.chat-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.chat-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  max-width: 900px;
  margin: 0 auto;
  width: 100%;
  background: #fff;
}

.chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px;
  border-bottom: 1px solid #e0e0e0;
  background: #f8f9fa;
  gap: 12px;
}

.menu-btn {
  width: 32px;
  height: 32px;
  padding: 0;
  background: transparent;
  border: 1px solid #ccc;
  border-radius: 4px;
  cursor: pointer;
  font-size: 16px;
  color: #666;
  flex-shrink: 0;
}

.menu-btn:hover {
  background: #e9ecef;
}

.chat-header h1 {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
  flex: 1;
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
  font-size: 13px;
}
</style>
