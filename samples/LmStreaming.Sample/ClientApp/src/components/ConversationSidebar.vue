<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import type { ConversationSortMode, ConversationSummary } from '@/types/conversations';
import { CONVERSATION_SORT_MODES } from '@/types/conversations';

const props = defineProps<{
  conversations: ConversationSummary[];
  currentThreadId: string | null;
  isLoading: boolean;
  isLoadingMore: boolean;
  sortMode: ConversationSortMode;
  isCollapsed: boolean;
}>();

const emit = defineEmits<{
  newChat: [];
  selectConversation: [threadId: string];
  deleteConversation: [threadId: string];
  toggleCollapse: [];
  loadMore: [];
  changeSortMode: [mode: ConversationSortMode];
}>();

/**
 * How close to the bottom of the scroll container counts as "near the bottom". Sized to fire while
 * roughly one conversation row is still below the fold, so the next page is usually in hand before
 * the user reaches the end.
 */
const LOAD_MORE_THRESHOLD_PX = 120;

const scrollRef = ref<HTMLElement | null>(null);
const sortMenuOpen = ref(false);
const sortDropdownRef = ref<HTMLElement | null>(null);

const sortModes = CONVERSATION_SORT_MODES;

const currentSortLabel = computed(
  () => sortModes.find((m) => m.id === props.sortMode)?.label ?? ''
);

/**
 * Asks the parent for the next page whenever the list is scrolled near its end.
 *
 * Fires freely — the parent's loader is a no-op while a page is in flight and once the list is
 * exhausted, so this handler deliberately keeps no state of its own.
 */
function handleScroll(): void {
  const el = scrollRef.value;
  if (!el) return;
  if (el.scrollHeight - el.scrollTop - el.clientHeight <= LOAD_MORE_THRESHOLD_PX) {
    emit('loadMore');
  }
}

function toggleSortMenu(): void {
  sortMenuOpen.value = !sortMenuOpen.value;
}

function closeSortMenu(): void {
  sortMenuOpen.value = false;
}

function handleSelectSortMode(mode: ConversationSortMode): void {
  closeSortMenu();
  if (mode !== props.sortMode) {
    emit('changeSortMode', mode);
  }
}

// Close the sort menu when clicking outside it. The wrapper stays mounted even while the sidebar is
// collapsed (it is dimmed, not removed) so this handler always has a live element to test against.
function handleClickOutside(event: MouseEvent): void {
  if (sortDropdownRef.value && !sortDropdownRef.value.contains(event.target as Node)) {
    closeSortMenu();
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    closeSortMenu();
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

    <div :class="['sidebar-sort', { hidden: isCollapsed }]" ref="sortDropdownRef">
      <button
        class="sort-btn"
        :class="{ open: sortMenuOpen }"
        data-testid="sort-mode-button"
        :tabindex="isCollapsed ? -1 : 0"
        @click="toggleSortMenu"
      >
        <span class="sort-label">Sort:</span>
        <span class="sort-name">{{ currentSortLabel }}</span>
        <span class="dropdown-arrow">{{ sortMenuOpen ? '▲' : '▼' }}</span>
      </button>

      <div v-if="sortMenuOpen" class="dropdown-menu">
        <button
          v-for="mode in sortModes"
          :key="mode.id"
          class="menu-item"
          :class="{ active: mode.id === sortMode }"
          :data-testid="`sort-mode-option-${mode.id}`"
          @click="handleSelectSortMode(mode.id)"
        >
          <span class="item-name">{{ mode.label }}</span>
          <span v-if="mode.id === sortMode" class="check-mark">✓</span>
        </button>
      </div>
    </div>

    <div
      :class="['sidebar-content', { hidden: isCollapsed }]"
      :aria-hidden="isCollapsed"
      :inert="isCollapsed || undefined"
      ref="scrollRef"
      @scroll="handleScroll"
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
          data-testid="conversation-item"
          :data-thread-id="conv.threadId"
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

        <!-- Only ever shown while a page is actually in flight: once the list is exhausted the
             parent stops loading, and the bottom of the list is simply the bottom of the list. -->
        <li
          v-if="isLoadingMore"
          class="loading-more"
          data-testid="conversations-loading-more"
        >
          Loading more...
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

.sidebar-sort {
  position: relative;
  padding: 8px 12px;
  border-bottom: 1px solid #e0e0e0;
  opacity: 1;
  transition: opacity 0.2s cubic-bezier(0.4, 0, 0.2, 1);
}

/* Dimmed rather than removed: a control that unmounts itself takes the click-outside handler's
   reference with it. */
.sidebar-sort.hidden {
  opacity: 0;
  pointer-events: none;
  overflow: hidden;
  visibility: hidden;
}

.sort-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 6px 10px;
  background: #f8f9fa;
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  font-size: 12px;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}

.sort-btn:hover {
  background: #e9ecef;
}

.sort-btn.open {
  border-color: #007bff;
  box-shadow: 0 0 0 2px rgba(0, 123, 255, 0.15);
}

.sort-label {
  color: #666;
}

.sort-name {
  flex: 1;
  min-width: 0;
  text-align: left;
  color: #212529;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dropdown-arrow {
  color: #adb5bd;
  font-size: 9px;
}

.dropdown-menu {
  position: absolute;
  top: 100%;
  left: 12px;
  right: 12px;
  margin-top: 2px;
  background: white;
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  z-index: 100;
  overflow: hidden;
}

.menu-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding: 8px 10px;
  background: none;
  border: none;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background 0.15s;
}

.menu-item:hover {
  background: #f8f9fa;
}

.menu-item.active {
  background: #e7f1ff;
  color: #007bff;
}

.item-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.check-mark {
  color: #007bff;
  font-weight: bold;
  flex-shrink: 0;
  margin-left: 8px;
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

.loading-more {
  padding: 12px 16px;
  text-align: center;
  font-size: 12px;
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

  .conversation-sidebar.collapsed .sidebar-header,
  .conversation-sidebar.collapsed .sidebar-sort {
    display: none;
  }
}
</style>
