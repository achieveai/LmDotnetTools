<script setup lang="ts">
import { computed, ref, watch, nextTick, onMounted, onUnmounted } from 'vue';
import type { DisplayItem } from '@/types';
import TextMessage from './TextMessage.vue';
import MetadataPill from './MetadataPill.vue';
import PendingMessage from './PendingMessage.vue';

const props = defineProps<{
  displayItems: readonly DisplayItem[];
}>();

const messageListRef = ref<HTMLDivElement | null>(null);
const activeConversationMinHeight = ref(0);
let resizeObserver: ResizeObserver | null = null;

onMounted(() => {
  if (messageListRef.value) {
    resizeObserver = new ResizeObserver((entries) => {
      for (const entry of entries) {
        // Use contentRect.height to get height excluding padding
        activeConversationMinHeight.value = entry.contentRect.height;
      }
    });
    resizeObserver.observe(messageListRef.value);
  }
});

onUnmounted(() => {
  if (resizeObserver) {
    resizeObserver.disconnect();
  }
});

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

const splitGroups = computed(() => {
  const groups = messageGroups.value;
  let lastUserIndex = -1;
  
  // Find the last user group
  for (let i = groups.length - 1; i >= 0; i--) {
    if (groups[i].role === 'user') {
      lastUserIndex = i;
      break;
    }
  }

  if (lastUserIndex === -1) {
    return { history: groups, current: [] };
  }

  return {
    history: groups.slice(0, lastUserIndex),
    current: groups.slice(lastUserIndex)
  };
});

// Track the last user message to scroll to it when it is added (pending or active)
const lastScrolledMessageId = ref<string | null>(null);
const lastMessageCount = ref(0);

// Watch for new user messages and scroll to them at the top
watch(
  () => props.displayItems,
  async (newItems, oldItems) => {
    // Find the last user message
    let lastUserMsg: DisplayItem | null = null;
    for (let i = newItems.length - 1; i >= 0; i--) {
      const item = newItems[i];
      if (item.type === 'user-message') {
        lastUserMsg = item;
        break;
      }
    }
    
    // Check if we have a new user message
    const hasNewUserMessage = lastUserMsg && lastUserMsg.id !== lastScrolledMessageId.value;
    
    // Check if we have new assistant messages (streaming response)
    const hasNewMessages = newItems.length > lastMessageCount.value;
    
    if (hasNewUserMessage) {
      // New user message: scroll to position it at the top
      lastScrolledMessageId.value = lastUserMsg!.id;
      
      // Wait for DOM update
      await nextTick();
      
      // Use requestAnimationFrame to ensure layout is settled and height calculations are correct
      // Double rAF ensures we are in the next paint frame
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          if (!messageListRef.value) return;
          
          const element = messageListRef.value.querySelector(`[data-message-id="${lastUserMsg!.id}"]`) as HTMLElement;
          if (element) {
            // Scroll the element to the top of the view
            element.scrollIntoView({ 
              behavior: 'smooth', 
              block: 'start',
              inline: 'nearest'
            });
          }
        });
      });
    } 
    
    lastMessageCount.value = newItems.length;
  },
  { deep: true }
);
</script>

<template>
  <div class="message-list" ref="messageListRef">
    <div v-if="displayItems.length === 0" class="empty-state">
      <p>No messages yet. Send a message to start the conversation.</p>
    </div>
    
    <!-- History Groups -->
    <template v-for="group in splitGroups.history" :key="group.id">
      <div 
        :class="group.role === 'user' ? 'user-message-wrapper' : 'assistant-message-wrapper'"
        :data-message-id="group.role === 'user' ? group.id : undefined"
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

    <!-- Active Conversation (Last User Msg + subsequent) -->
    <div 
      v-if="splitGroups.current.length > 0" 
      class="active-conversation-spacer"
      :style="{ minHeight: `${activeConversationMinHeight}px` }"
    >
      <template v-for="group in splitGroups.current" :key="group.id">
        <div 
          :class="group.role === 'user' ? 'user-message-wrapper' : 'assistant-message-wrapper'"
          :data-message-id="group.role === 'user' ? group.id : undefined"
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

.active-conversation-spacer {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
</style>
