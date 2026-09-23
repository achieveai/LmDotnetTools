<script setup lang="ts">
import { computed, ref, useId } from 'vue';
import type { DisplayItem, ToolCall } from '@/types';
import { isToolsCallMessage } from '@/types';
import { deriveToolPillState } from '@/utils/toolPillState';
import { describeRunningTool, summarizeToolCall } from '@/utils/toolActivity';
import { useToolResult } from '@/composables/useToolResult';
import { isQuestionAwaitingAnswer } from '@/utils/pendingQuestions';
import MetadataPill from './MetadataPill.vue';
import NotificationPill from './NotificationPill.vue';
import TextMessage from './TextMessage.vue';

const props = defineProps<{
  items: DisplayItem[];
  isLoading: boolean;
}>();

const expanded = ref(false);
const detailsId = `turn-activity-${useId()}`;
const { getResult, isQuestionAnswered } = useToolResult();

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
      isDeferred: isQuestionAwaitingAnswer(result, isQuestionAnswered),
    });
  })
);

/**
 * What the run is doing RIGHT NOW: the LAST tool call with no result yet, described with the same
 * wording the expanded row uses ("reading src/foo.ts", "running npm test"). The last one — not the
 * first — because that is the call the user is actually waiting on; earlier ones already finished
 * or were superseded on screen. Empty while nothing is in flight (the model is writing text).
 */
const runningToolPhrase = computed(() => {
  for (let i = toolStates.value.length - 1; i >= 0; i--) {
    const view = toolStates.value[i];
    if (view.state !== 'awaiting-result' && view.state !== 'streaming-args') continue;
    const name = toolCalls.value[i].function_name;
    return describeRunningTool(name, view, summarizeToolCall(name, view));
  }
  return '';
});

const summary = computed(() => {
  const failed = toolStates.value.filter((state) => state.state === 'error').length;
  const actionLabel = `${toolCalls.value.length} action${toolCalls.value.length === 1 ? '' : 's'}`;
  if (failed > 0) return `${actionLabel} · ${failed} failed`;
  if (toolStates.value.some((state) => state.state === 'awaiting-input')) {
    return 'Waiting for your answer';
  }
  if (props.isLoading) {
    return runningToolPhrase.value ? `Working: ${runningToolPhrase.value}` : 'Working…';
  }
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
      <span class="turn-activity__label">{{ summary }}</span>
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

/* A live tool phrase can carry a long path or command; clip it to one line exactly as the
   expanded activity row does (.tool-pill__activity-description), so the toggle never wraps. */
.turn-activity__label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
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
