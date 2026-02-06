<script setup lang="ts">
import type { ConversationSummary } from '@/types/conversations';

defineProps<{
  conversations: ConversationSummary[];
  currentThreadId: string | null;
  isLoading: boolean;
  isCollapsed: boolean;
}>();

const emit = defineEmits<{
  newChat: [];
  selectConversation: [threadId: string];
  deleteConversation: [threadId: string];
  toggleCollapse: [];
}>();

function formatDate(timestamp: number): string {
  const date = new Date(timestamp);
  const now = new Date();
  const diff = now.getTime() - date.getTime();
  const oneDay = 24 * 60 * 60 * 1000;

  if (diff < oneDay) {
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  } else if (diff < 7 * oneDay) {
    return date.toLocaleDateString([], { weekday: 'short' });
  } else {
    return date.toLocaleDateString([], { month: 'short', day: 'numeric' });
  }
}

function truncateText(text: string | undefined, maxLength: number): string {
  if (!text) return '';
  if (text.length <= maxLength) return text;
  return text.substring(0, maxLength) + '...';
}

function handleDelete(event: Event, threadId: string): void {
  event.stopPropagation();
  if (confirm('Are you sure you want to delete this conversation?')) {
    emit('deleteConversation', threadId);
  }
}
</script>

<template>
  <aside :class="['conversation-sidebar', { collapsed: isCollapsed }]">
    <div class="sidebar-header">
      <button
        class="toggle-btn"
        @click="emit('toggleCollapse')"
        :title="isCollapsed ? 'Expand sidebar' : 'Collapse sidebar'"
      >
        {{ isCollapsed ? '>' : '<' }}
      </button>
      <button
        :class="['new-chat-btn', { hidden: isCollapsed }]"
        @click="emit('newChat')"
        :tabindex="isCollapsed ? -1 : 0"
      >
        + New Chat
      </button>
    </div>

    <div
      :class="['sidebar-content', { hidden: isCollapsed }]"
      :aria-hidden="isCollapsed"
      :inert="isCollapsed ? '' : null"
    >
      <div v-if="isLoading" class="loading">
        Loading conversations...
      </div>

      <div v-else-if="conversations.length === 0" class="empty-state">
        No conversations yet.
        <br />
        Click "New Chat" to start.
      </div>

      <ul v-else class="conversation-list">
        <li
          v-for="conv in conversations"
          :key="conv.threadId"
          :class="['conversation-item', { active: conv.threadId === currentThreadId }]"
          @click="emit('selectConversation', conv.threadId)"
        >
          <div class="conversation-content">
            <div class="conversation-title">
              {{ truncateText(conv.title, 30) }}
            </div>
            <div v-if="conv.preview" class="conversation-preview">
              {{ truncateText(conv.preview, 50) }}
            </div>
            <div class="conversation-date">
              {{ formatDate(conv.lastUpdated) }}
            </div>
          </div>
          <button
            class="delete-btn"
            @click="handleDelete($event, conv.threadId)"
            title="Delete conversation"
          >
            X
          </button>
        </li>
      </ul>
    </div>
  </aside>
</template>

<style scoped>
.conversation-sidebar {
  width: 280px;
  min-width: 280px;
  border-right: 1px solid #e0e0e0;
  display: flex;
  flex-direction: column;
  background: #f8f9fa;
  transition: width 0.25s cubic-bezier(0.4, 0, 0.2, 1),
    min-width 0.25s cubic-bezier(0.4, 0, 0.2, 1);
  will-change: width, min-width;
  contain: layout style;
}

.conversation-sidebar.collapsed {
  width: 48px;
  min-width: 48px;
}

.sidebar-header {
  padding: 12px;
  border-bottom: 1px solid #e0e0e0;
  display: flex;
  gap: 8px;
  align-items: center;
  overflow: hidden;
}

.toggle-btn {
  width: 24px;
  height: 24px;
  padding: 0;
  background: transparent;
  border: 1px solid #ccc;
  border-radius: 4px;
  cursor: pointer;
  font-size: 12px;
  color: #666;
  flex-shrink: 0;
}

.toggle-btn:hover {
  background: #e9ecef;
}

.new-chat-btn {
  flex: 1;
  padding: 10px 12px;
  background: #007bff;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  white-space: nowrap;
  overflow: hidden;
  opacity: 1;
  transform: translateX(0);
  transition:
    opacity 0.2s cubic-bezier(0.4, 0, 0.2, 1),
    transform 0.2s cubic-bezier(0.4, 0, 0.2, 1),
    background 0.15s;
}

.new-chat-btn.hidden {
  opacity: 0;
  transform: translateX(-10px);
  pointer-events: none;
}

.new-chat-btn:hover:not(.hidden) {
  background: #0056b3;
}

.sidebar-content {
  flex: 1;
  overflow-y: auto;
  opacity: 1;
  transition: opacity 0.2s cubic-bezier(0.4, 0, 0.2, 1);
}

.sidebar-content.hidden {
  opacity: 0;
  pointer-events: none;
  overflow: hidden;
  visibility: hidden;
}

.loading,
.empty-state {
  padding: 20px;
  text-align: center;
  color: #666;
  font-size: 14px;
}

.conversation-list {
  list-style: none;
  padding: 0;
  margin: 0;
}

.conversation-item {
  padding: 12px 16px;
  border-bottom: 1px solid #e0e0e0;
  cursor: pointer;
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: 8px;
  transition: background 0.1s;
}

.conversation-item:hover {
  background: #e9ecef;
}

.conversation-item.active {
  background: #d4e5f7;
  border-left: 3px solid #007bff;
  padding-left: 13px;
}

.conversation-content {
  flex: 1;
  min-width: 0;
}

.conversation-title {
  font-weight: 500;
  font-size: 14px;
  margin-bottom: 4px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  color: #212529;
}

.conversation-preview {
  font-size: 12px;
  color: #6c757d;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  margin-bottom: 4px;
}

.conversation-date {
  font-size: 11px;
  color: #adb5bd;
}

.delete-btn {
  opacity: 0;
  width: 20px;
  height: 20px;
  padding: 0;
  background: #dc3545;
  color: white;
  border: none;
  border-radius: 4px;
  font-size: 10px;
  font-weight: bold;
  cursor: pointer;
  transition: opacity 0.15s;
  flex-shrink: 0;
}

.conversation-item:hover .delete-btn {
  opacity: 1;
}

.delete-btn:hover {
  background: #c82333;
}

/* Responsive styles */
@media (max-width: 768px) {
  .conversation-sidebar {
    position: fixed;
    left: 0;
    top: 0;
    bottom: 0;
    z-index: 100;
    box-shadow: 2px 0 8px rgba(0, 0, 0, 0.15);
  }

  .conversation-sidebar.collapsed {
    width: 0;
    min-width: 0;
    border-right: none;
  }

  .conversation-sidebar.collapsed .sidebar-header {
    display: none;
  }
}
</style>
