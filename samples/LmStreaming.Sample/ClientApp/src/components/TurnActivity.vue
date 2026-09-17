<script setup lang="ts">
import { computed, ref, useId } from 'vue';
import type { DisplayItem, ToolCall } from '@/types';
import { isToolsCallMessage } from '@/types';
import { deriveToolPillState } from '@/utils/toolPillState';
import { useToolResult } from '@/composables/useToolResult';
import MetadataPill from './MetadataPill.vue';
import NotificationPill from './NotificationPill.vue';
import TextMessage from './TextMessage.vue';

const props = defineProps<{
  items: DisplayItem[];
  isLoading: boolean;
}>();

const expanded = ref(false);
const detailsId = `turn-activity-${useId()}`;
const { getResult } = useToolResult();

const toolCalls = computed<ToolCall[]>(() =>
  props.items.flatMap((item) =>
    item.type === 'pill'
      ? item.items.flatMap((message) => (isToolsCallMessage(message) ? message.tool_calls : []))
      : []
  )
);

const toolStates = computed(() =>
  toolCalls.value.map((call) => {
    const result = getResult(call);
    return deriveToolPillState({
      functionArgs: call.function_args,
      result: result?.result ?? null,
      hasResult: result !== null,
      isErrorFlag: result?.is_error ?? null,
      isDeferred: result?.is_deferred ?? false,
    });
  })
);

const summary = computed(() => {
  const failed = toolStates.value.filter((state) => state.state === 'error').length;
  const actionLabel = `${toolCalls.value.length} action${toolCalls.value.length === 1 ? '' : 's'}`;
  if (failed > 0) return `${actionLabel} · ${failed} failed`;
  if (toolStates.value.some((state) => state.state === 'awaiting-input')) {
    return 'Waiting for your answer';
  }
  if (props.isLoading) return 'Working…';
  const unresolved = toolStates.value.some(
    (state) => state.state === 'awaiting-result' || state.state === 'streaming-args'
  );
  if (unresolved) return 'Activity';
  if (toolCalls.value.length > 0) return `Completed ${actionLabel}`;

  const notification = [...props.items]
    .reverse()
    .find((item) => item.type === 'notification');
  if (notification?.type === 'notification') {
    switch (notification.notification.notifyKind) {
      case 'subagent-completion':
        return 'Sub-agent completed';
      case 'compaction':
        return 'Context compacted';
      case 'context-discovery':
        return 'Context loaded';
      case 'todo-digest':
        return 'Work updated';
      case 'todo-nudge':
        return 'Work reminder';
    }
  }
  return 'Thought through the response';
});

const hasFailure = computed(() => toolStates.value.some((state) => state.state === 'error'));
</script>

<template>
  <div class="turn-activity" :class="{ 'turn-activity--failed': hasFailure }" data-testid="turn-activity">
    <button
      type="button"
      class="turn-activity__toggle"
      data-testid="turn-activity-toggle"
      :aria-expanded="expanded"
      :aria-controls="detailsId"
      @click="expanded = !expanded"
    >
      <span aria-hidden="true">{{ hasFailure ? '⚠' : expanded ? '▾' : '▸' }}</span>
      <span>{{ summary }}</span>
    </button>
    <div
      v-if="expanded"
      :id="detailsId"
      class="turn-activity__details"
      data-testid="turn-activity-details"
    >
      <template v-for="item in items" :key="item.id">
        <MetadataPill
          v-if="item.type === 'pill'"
          :items="item.items"
          presentation="activity-row"
        />
        <NotificationPill
          v-else-if="item.type === 'notification'"
          :notification="item.notification"
        />
        <TextMessage
          v-else-if="item.type === 'assistant-message'"
          :message="item.content"
          :is-streaming="false"
          :is-complete="true"
          :workspace-links="false"
        />
      </template>
    </div>
  </div>
</template>

<style scoped>
.turn-activity {
  max-width: 100%;
  color: #68727d;
  font-size: 13px;
}

.turn-activity__toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
  padding: 4px 7px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
  text-align: left;
}

.turn-activity__toggle:hover,
.turn-activity__toggle:focus-visible {
  background: #f1f3f5;
  color: #3f4a55;
}

.turn-activity--failed {
  color: #b3261e;
}

.turn-activity__details {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 4px;
}
</style>
