<script setup lang="ts">
import { computed, ref, watch, nextTick, onMounted, onUnmounted } from 'vue';
import type { DisplayItem } from '@/types';
import TextMessage from './TextMessage.vue';
import CopyMessageButton from './CopyMessageButton.vue';
import MetadataPill from './MetadataPill.vue';
import NotificationPill from './NotificationPill.vue';
import TurnActivity from './TurnActivity.vue';
import PendingMessage from './PendingMessage.vue';
import AssistantTypingIndicator from './AssistantTypingIndicator.vue';
import { logger } from '@/utils/logger';

// #region agent log
const log = logger.forComponent('MessageList');
log.info('MessageList component created/loaded');
// #endregion

// Direct consumers keep the established detailed timeline. ChatLayout and SubAgentTranscript always
// pass the saved preference explicitly, so this default is only a backward-compatible fallback.
const props = withDefaults(defineProps<{
  displayItems: readonly DisplayItem[];
  isLoading?: boolean;
  viewPreference?: 'consumer' | 'developer';
}>(), { viewPreference: 'developer' });

const messageListRef = ref<HTMLDivElement | null>(null);
const activeConversationMinHeight = ref(0);
let resizeObserver: ResizeObserver | null = null;

onMounted(() => {
  if (messageListRef.value) {
    // #region agent log
    log.debug('MessageList mounted', { 
      initialHeight: messageListRef.value.clientHeight,
      scrollHeight: messageListRef.value.scrollHeight,
      scrollTop: messageListRef.value.scrollTop
    });
    // #endregion
    
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

type RenderRow =
  | { kind: 'item'; id: string; item: DisplayItem }
  | { kind: 'activity'; id: string; items: DisplayItem[]; runId: string | null };

const COLLAPSIBLE_NOTIFICATION_KINDS = new Set([
  'agent-message',
  'compaction',
  'context-discovery',
  'subagent-completion',
  'todo-digest',
  'todo-nudge',
]);

function isActivityItem(item: DisplayItem): boolean {
  if (item.type === 'pill') return true;
  if (item.type === 'assistant-message') return item.content.isThinking === true;
  if (item.type === 'notification' && item.notification.agentMessageType === 'DeliveryFailure') {
    return false;
  }
  return item.type === 'notification' && COLLAPSIBLE_NOTIFICATION_KINDS.has(item.notification.notifyKind);
}

function itemRunId(item: DisplayItem): string | null {
  return item.type === 'user-message' ? null : item.runId ?? null;
}

function renderRows(group: MessageGroup): RenderRow[] {
  if (props.viewPreference === 'developer' || group.role === 'user') {
    return group.items.map((item) => ({ kind: 'item', id: item.id, item }));
  }

  const activityByRun = new Map<string, DisplayItem[]>();
  for (const item of group.items) {
    if (!isActivityItem(item)) continue;
    const key = itemRunId(item) || 'legacy';
    const items = activityByRun.get(key) ?? [];
    items.push(item);
    activityByRun.set(key, items);
  }

  const emitted = new Set<string>();
  const rows: RenderRow[] = [];
  for (const item of group.items) {
    if (!isActivityItem(item)) {
      rows.push({ kind: 'item', id: item.id, item });
      continue;
    }
    const key = itemRunId(item) || 'legacy';
    if (emitted.has(key)) continue;
    emitted.add(key);
    rows.push({ kind: 'activity', id: `activity-${group.id}-${key}`, items: activityByRun.get(key)!, runId: itemRunId(item) });
  }
  return rows;
}

const activeRunId = computed<string | null>(() => {
  for (let i = props.displayItems.length - 1; i >= 0; i--) {
    const item = props.displayItems[i];
    if (item.type === 'user-message') return null;
    if (item.type === 'notification' && !item.runId) continue;
    return item.runId ?? null;
  }
  return null;
});

function activityIsLoading(row: Extract<RenderRow, { kind: 'activity' }>, group: MessageGroup): boolean {
  if (!props.isLoading) return false;
  if (row.runId !== null) return row.runId === activeRunId.value;
  const lastGroup = messageGroups.value[messageGroups.value.length - 1];
  return lastGroup?.role === 'assistant' && lastGroup.id === group.id;
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

const showTypingIndicator = computed(() => {
  if (!props.isLoading) return false;
  const groups = messageGroups.value;
  if (groups.length === 0) return false;
  // Show when the last group is a user message (assistant hasn't started responding yet)
  return groups[groups.length - 1].role === 'user';
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

/**
 * The one assistant bubble that is still growing. Its markdown is re-parsed on EVERY streamed
 * delta, so it renders without syntax highlighting and gets highlighted once the run ends -- see
 * `parseMarkdown`'s `highlight` option.
 *
 * Scanned over the FLAT `displayItems`, not `splitGroups.current`: MessageList has two consumers
 * and only one of them has user messages. `SubAgentTranscript` passes an assistant-only
 * transcript with `isLoading` true, which leaves `splitGroups.current` empty (`splitGroups`
 * returns `current: []` when there is no user group at all), so a group-scoped search silently
 * never fires for a child transcript.
 *
 * Walking back from the end, each item type means something different about whether the last
 * assistant bubble is still growing -- these are the semantics `buildDisplayItems` gives them:
 *  - `assistant-message`: the bubble in question. Match.
 *  - `user-message`: the human already sent the NEXT turn, so the assistant text before it is
 *    finished. Stop -- marking it incomplete is what turns a rendered code block monochrome
 *    mid-run.
 *  - `pill`: reasoning/tool content is buffered and only flushed as a pill once something else
 *    follows it, so a TRAILING pill means the assistant moved off the text and onto a tool call.
 *    That text is done and must stay highlighted for however long the tool runs. Stop.
 *  - `notification`: out-of-band (sub-agent completion, agent message, context discovery). It can
 *    land mid-stream and says nothing about the text, so skip past it and keep looking.
 * Anything unrecognised stops the scan: losing the optimization is cheap, a monochrome flicker
 * is not.
 */
const streamingItemId = computed<string | null>(() => {
  if (!props.isLoading) return null;
  for (let i = props.displayItems.length - 1; i >= 0; i--) {
    const item = props.displayItems[i];
    if (item.type === 'assistant-message') return item.id;
    if (item.type === 'notification') continue;
    return null;
  }
  return null;
});

/**
 * Copy and workspace file links belong to the model's answers. A thinking bubble keeps neither, and Copy
 * waits until the bubble has finished streaming. Both template branches (history and active) use these.
 */
function isThinkingBubble(item: DisplayItem): boolean {
  return item.type === 'assistant-message' && item.content.isThinking === true;
}

function offersCopy(item: DisplayItem): boolean {
  return !isThinkingBubble(item) && item.id !== streamingItemId.value;
}

// Track the last user message to scroll to it when it is added (pending or active)
const lastScrolledMessageId = ref<string | null>(null);

// Custom smooth scroll function for specific duration
function smoothScrollTo(element: HTMLElement, to: number, duration: number) {
  const start = element.scrollTop;
  const change = to - start;
  const startTime = performance.now();

  function animate(currentTime: number) {
    const elapsed = currentTime - startTime;
    const progress = Math.min(elapsed / duration, 1);

    // Easing function (easeInOutQuad)
    const ease = progress < 0.5 
      ? 2 * progress * progress 
      : 1 - Math.pow(-2 * progress + 2, 2) / 2;

    element.scrollTop = start + (change * ease);

    if (progress < 1) {
      requestAnimationFrame(animate);
    }
  }

  requestAnimationFrame(animate);
}

watch(
  () => props.viewPreference,
  async () => {
    const list = messageListRef.value;
    if (!list) return;
    const listTop = list.getBoundingClientRect().top;
    const anchors = Array.from(list.querySelectorAll<HTMLElement>('[data-view-anchor]'));
    const anchor = anchors.find((element) => element.getBoundingClientRect().bottom > listTop);
    if (!anchor) return;
    const id = anchor.dataset.viewAnchor;
    const offset = anchor.getBoundingClientRect().top - listTop;

    await nextTick();
    const restored = id
      ? Array.from(list.querySelectorAll<HTMLElement>('[data-view-anchor]')).find(
          (element) => element.dataset.viewAnchor === id
        ) ?? null
      : null;
    if (restored) {
      list.scrollTop += restored.getBoundingClientRect().top - list.getBoundingClientRect().top - offset;
    }
  },
  { flush: 'sync' }
);

// Watch for new user messages and scroll to them at the top
watch(
  () => props.displayItems,
  async (newItems) => {
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
            // Scroll the element to the top of the view with 150ms animation
            smoothScrollTo(messageListRef.value, element.offsetTop, 150);
          }
        });
      });
    }
  },
  { deep: true }
);
</script>

<template>
  <div class="message-list" ref="messageListRef" data-testid="message-list">
    <div v-if="displayItems.length === 0" class="empty-state">
      <p>No messages yet. Send a message to start the conversation.</p>
    </div>
    
    <!-- History Groups -->
    <template v-for="group in splitGroups.history" :key="group.id">
      <div 
        :class="group.role === 'user' ? 'user-message-wrapper' : 'assistant-message-wrapper'"
        :data-message-id="group.role === 'user' ? group.id : undefined"
        :data-view-anchor="group.role === 'user' ? group.id : undefined"
        :data-testid="group.role === 'user' ? 'user-message-group' : 'assistant-message-group'"
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
            <template v-for="row in renderRows(group)" :key="row.id">
              <TurnActivity
                v-if="row.kind === 'activity'"
                :items="row.items"
                :is-loading="activityIsLoading(row, group)"
              />
              <template v-else>
              <template v-if="row.item.type === 'user-message'">
                <PendingMessage v-if="row.item.status === 'pending'" :content="row.item.content" />
                <TextMessage v-else :message="row.item.content" :is-streaming="false" />
              </template>

              <MetadataPill v-else-if="row.item.type === 'pill'" :items="row.item.items" />

              <NotificationPill
                v-else-if="row.item.type === 'notification'"
                :notification="row.item.notification"
              />

              <div
                v-else-if="row.item.type === 'assistant-message'"
                class="text-bubble-row"
                :data-view-anchor="!isThinkingBubble(row.item) ? row.item.id : undefined"
              >
                <div class="text-bubble" data-testid="assistant-text">
                  <TextMessage
                    :message="row.item.content"
                    :is-streaming="false"
                    :is-complete="row.item.id !== streamingItemId"
                    :workspace-links="!isThinkingBubble(row.item)"
                  />
                </div>
                <CopyMessageButton
                  v-if="offersCopy(row.item)"
                  class="bubble-copy"
                  :text="row.item.content.text"
                />
              </div>
              </template>
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
          :data-view-anchor="group.role === 'user' ? group.id : undefined"
          :data-testid="group.role === 'user' ? 'user-message-group' : 'assistant-message-group'"
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
              <template v-for="row in renderRows(group)" :key="row.id">
                <TurnActivity
                  v-if="row.kind === 'activity'"
                  :items="row.items"
                  :is-loading="activityIsLoading(row, group)"
                />
                <template v-else>
                <!-- User message (pending or active) -->
                <template v-if="row.item.type === 'user-message'">
                  <PendingMessage v-if="row.item.status === 'pending'" :content="row.item.content" />
                  <TextMessage v-else :message="row.item.content" :is-streaming="false" />
                </template>
                
                <!-- Assistant message with pill -->
                <MetadataPill v-else-if="row.item.type === 'pill'" :items="row.item.items" />

                <!-- Out-of-band notification (sub-agent completion, context discovery, ...) -->
                <NotificationPill
                  v-else-if="row.item.type === 'notification'"
                  :notification="row.item.notification"
                />

                <!-- Assistant text message -->
                <div
                  v-else-if="row.item.type === 'assistant-message'"
                  class="text-bubble-row"
                  :data-view-anchor="!isThinkingBubble(row.item) ? row.item.id : undefined"
                >
                  <div class="text-bubble" data-testid="assistant-text">
                    <TextMessage
                      :message="row.item.content"
                      :is-streaming="false"
                      :is-complete="row.item.id !== streamingItemId"
                      :workspace-links="!isThinkingBubble(row.item)"
                    />
                  </div>
                  <CopyMessageButton
                    v-if="offersCopy(row.item)"
                    class="bubble-copy"
                    :text="row.item.content.text"
                  />
                </div>
                </template>
              </template>
            </div>
          </div>
        </div>
      </template>

      <AssistantTypingIndicator v-if="showTypingIndicator" />
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
  width: 100%;
}

.user-message-container,
.assistant-message-container {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  width: 100%;
  min-width: 0;
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

/* The row is exactly the bubble's box (a block wrapper around one block child), so the button can be
   pinned to the bubble's corner while staying outside `assistant-text`. */
.text-bubble-row {
  position: relative;
  min-width: 0;
}

/* Copy button: pinned to the bubble's top-right corner, revealed on hover or keyboard focus. It stays
   in the DOM (opacity, not v-show) so Tab can reach it and :focus-within can reveal it. */
.bubble-copy {
  position: absolute;
  top: 6px;
  right: 8px;
  opacity: 0;
  /* While invisible it must not swallow clicks or text selection on the bubble's first line. */
  pointer-events: none;
  transition: opacity 0.12s ease-in-out;
}

.text-bubble-row:hover .bubble-copy,
.text-bubble-row:focus-within .bubble-copy {
  opacity: 1;
  pointer-events: auto;
}

/* Touch screens have no hover: keep it visible there. */
@media (hover: none) {
  .bubble-copy {
    opacity: 1;
    pointer-events: auto;
  }
}

/* User message styling handled in PendingMessage and TextMessage components */

.active-conversation-spacer {
  display: flex;
  flex-direction: column;
  gap: 12px;
  /* justify-content: flex-end; Removed to allow content to start at top */
}
</style>
