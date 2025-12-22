<script setup lang="ts">
import { computed } from 'vue';
import type { DisplayItem } from '@/types';
import TextMessage from './TextMessage.vue';
import MetadataPill from './MetadataPill.vue';
import PendingMessage from './PendingMessage.vue';

const props = defineProps<{
  displayItems: readonly DisplayItem[];
}>();

/**
 * Group consecutive display items from the same role together
 * Each group will share a single avatar
 */
interface MessageGroup {
  id: string;
  role: 'user' | 'assistant';
  items: DisplayItem[];
  status?: 'pending' | 'active' | 'completed';
}

const messageGroups = computed<MessageGroup[]>(() => {
  const groups: MessageGroup[] = [];
  
  for (const item of props.displayItems) {
    const role = item.type === 'user-message' ? 'user' : 'assistant';
    const status = item.type === 'user-message' ? item.status : undefined;
    const lastGroup = groups[groups.length - 1];
    
    // Check if we can add to the existing group
    // Keep pending messages separate, group other messages by role
    if (lastGroup && lastGroup.role === role && status !== 'pending') {
      lastGroup.items.push(item);
    } else {
      // Create a new group
      groups.push({
        id: item.id,
        role,
        items: [item],
        status,
      });
    }
  }
  
  return groups;
});
</script>

<template>
  <div class="message-list">
    <div v-if="displayItems.length === 0" class="empty-state">
      <p>No messages yet. Send a message to start the conversation.</p>
    </div>
    
    <!-- Render message groups with single avatar per group -->
    <template v-for="group in messageGroups" :key="group.id">
      <div 
        :class="group.role === 'user' ? 'user-message-wrapper' : 'assistant-message-wrapper'"
      >
        <div 
          :class="group.role === 'user' ? 'user-message-container' : 'assistant-message-container'"
        >
          <!-- Avatar (only once per group) -->
          <div 
            :class="group.role === 'user' ? 'user-avatar' : 'assistant-avatar'"
            class="group-avatar"
          >
            {{ group.role === 'user' ? '&#x1F464;' : '&#x1F916;' }}
          </div>
          
          <!-- Content area for all items in the group -->
          <div :class="group.role === 'user' ? 'user-content' : 'assistant-content'">
            <template v-for="item in group.items" :key="item.id">
              <!-- User message (pending or active) -->
              <template v-if="item.type === 'user-message'">
                <PendingMessage v-if="item.status === 'pending'" :content="item.content" />
                <TextMessage v-else :message="item.content" :is-streaming="false" />
              </template>
              
              <!-- Assistant message with pill -->
              <MetadataPill v-else-if="item.type === 'pill'" :items="item.items" />
              
              <!-- Assistant text message -->
              <div v-else-if="item.type === 'assistant-message'" class="text-bubble">
                <TextMessage :message="item.content" :is-streaming="false" />
              </div>
            </template>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.message-list {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  padding: 16px;
  gap: 12px;
}

.empty-state {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #666;
  font-size: 14px;
}

.user-message-wrapper,
.assistant-message-wrapper {
  display: flex;
  max-width: 85%;
}

.user-message-wrapper {
  margin-left: auto;
}

.assistant-message-wrapper {
  margin-right: auto;
}

.user-message-container,
.assistant-message-container {
  display: flex;
  gap: 12px;
  align-items: flex-start;
}

.user-message-container {
  flex-direction: row-reverse;
}

.user-avatar,
.assistant-avatar {
  width: 40px;
  height: 40px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 20px;
  flex-shrink: 0;
}

.user-avatar {
  background: #1976d2;
}

.assistant-avatar {
  background: #6c757d;
}

.user-content,
.assistant-content {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.group-avatar {
  align-self: flex-start;
  position: sticky;
  top: 8px;
}

.text-bubble {
  background: #ffffff;
  border: 1px solid #e0e0e0;
  border-radius: 16px 16px 16px 4px;
  padding: 12px 16px;
}

/* User message styling handled in PendingMessage and TextMessage components */
</style>
