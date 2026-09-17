<script setup lang="ts">
import { computed, inject, ref, toRef, useId } from 'vue';
import type { AgentMessageType, NotificationDisplayData } from '@/types';
import {
  AGENT_MESSAGE_NOTIFY_KIND,
  COMPACTION_NOTIFY_KIND,
  GET_CHECKPOINT_STATE,
  type CheckpointStateLookup,
} from '@/composables/messageDisplay';
import { GET_AGENT_COLOR, type AgentColorLookup } from '@/utils/agentColors';
import { GO_TO_AGENT_TAB, type GoToAgentTab } from '@/composables/useConversationTabs';

/**
 * Presentational pill for an out-of-band notification (async sub-agent completion, sandbox
 * context-discovery, monitors, timers). Distinct from a user bubble. Takes the normalized
 * {@link NotificationDisplayData} that `useChat`'s `displayItems` produces — the single normalization
 * site for both new NotifyMessages and legacy context_discovery rows.
 */
const props = defineProps<{
  notification: NotificationDisplayData;
}>();

const data = toRef(props, 'notification');

/** Human-friendly headings for the existing agent-to-agent message types (#244). */
const AGENT_MESSAGE_HEADINGS: Record<AgentMessageType, string> = {
  Question: 'Agent asked',
  DelegateTask: 'Agent delegated',
  TaskUpdate: 'Agent update',
  Steer: 'Agent steered',
  Response: 'Agent replied',
  DeliveryFailure: 'Message undelivered',
};

/** Human-friendly heading per well-known kind; unknown kinds show the raw kind string. */
const kindLabel = computed<string>(() => {
  switch (data.value.notifyKind) {
    case 'context-discovery':
      return 'Context loaded';
    case 'subagent-completion':
      return 'Sub-agent completed';
    case 'descendant-question':
      return 'Question pending';
    case 'client-notification':
      return 'Notification';
    case COMPACTION_NOTIFY_KIND:
      return 'Context compacted';
    case AGENT_MESSAGE_NOTIFY_KIND: {
      const type = data.value.agentMessageType;
      return (type && AGENT_MESSAGE_HEADINGS[type]) || type || 'Agent message';
    }
    default:
      return data.value.notifyKind;
  }
});

/** The primary label shown on the header: the file path for context, else the notification label. */
const primaryLabel = computed<string | null>(() => {
  if (data.value.notifyKind === 'context-discovery') {
    return data.value.contextPath ?? null;
  }
  return data.value.label ?? null;
});

/** Expandable body: the pre-rendered detail if present, otherwise the full envelope text. */
const bodyText = computed<string | null>(() => data.value.detail ?? data.value.text ?? null);
const hasBody = computed<boolean>(() => !!bodyText.value && bodyText.value.trim().length > 0);
const hasSourceMetadata = computed<boolean>(
  () => !!data.value.sourceToolName || !!data.value.sourceToolCallId
);

// #246 (fixed): a descendant-question pill reports a descendant blocked on a browser-hosted
// client tool (e.g. AskUserQuestion). Clicking it jumps the center pane to that descendant's tab
// (where the actual question renders inline) instead of expanding a body — there is nothing useful
// to expand here, the tab IS the detail. This is deliberately a DIFFERENT kind from
// 'client-notification' (NotifyClient's ad-hoc, non-blocking note): the latter's
// source_tool_call_id is always the NotifyClient tool call's own id, never an agent/tab id, so it
// must never be treated as navigable — it stays a plain expandable notification.
const goToAgentTab = inject<GoToAgentTab>(GO_TO_AGENT_TAB, () => {});
const isNavigable = computed<boolean>(
  () => data.value.notifyKind === 'descendant-question' && !!data.value.sourceToolCallId
);
const isExpandable = computed<boolean>(
  () => !isNavigable.value && (hasBody.value || hasSourceMetadata.value)
);
const isClickable = computed<boolean>(() => isExpandable.value || isNavigable.value);

const expanded = ref(false);
const detailId = useId();
function handleHeaderClick(): void {
  if (isNavigable.value) {
    goToAgentTab(data.value.sourceToolCallId!);
    return;
  }
  if (isExpandable.value) {
    expanded.value = !expanded.value;
  }
}

// #721: a compaction divider spans the transcript and, when the context report says its checkpoint
// was rolled back, says so — history is truth, so the divider stays, but the agent no longer uses it.
const isCompaction = computed<boolean>(() => data.value.notifyKind === COMPACTION_NOTIFY_KIND);
const getCheckpointState = inject<CheckpointStateLookup>(GET_CHECKPOINT_STATE, () => null);
const rolledBack = computed<boolean>(
  () => isCompaction.value && !!data.value.checkpointId && getCheckpointState(data.value.checkpointId) === 'RolledBack'
);

// Tint notifications that belong to a known agent: a completion uses the completing agent's id,
// while an agent-message uses the normalized sender id. Other notification kinds are unchanged.
const getAgentColor = inject<AgentColorLookup>(GET_AGENT_COLOR, () => null);
const agentColor = computed<string | null>(() =>
  data.value.notifyKind === 'subagent-completion' || data.value.notifyKind === AGENT_MESSAGE_NOTIFY_KIND
    ? getAgentColor(data.value.sourceToolCallId)
    : null
);
</script>

<template>
  <div
    class="notification-pill"
    :class="{ 'compaction-divider': isCompaction }"
    data-testid="notification-pill"
    :data-notify-kind="data.notifyKind"
    :data-checkpoint-id="data.checkpointId ?? undefined"
    :style="agentColor ? { borderLeftColor: agentColor, borderLeftWidth: '3px' } : undefined"
  >
    <component
      :is="isClickable ? 'button' : 'div'"
      class="notification-header"
      :class="{ clickable: isClickable }"
      :type="isClickable ? 'button' : undefined"
      :aria-expanded="isExpandable ? expanded : undefined"
      :aria-controls="isExpandable ? detailId : undefined"
      @click="handleHeaderClick"
    >
      <svg class="notification-icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
        <template v-if="data.notifyKind === 'context-discovery'">
          <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z" />
          <path d="M14 2v6h6" />
        </template>
        <template v-else-if="data.notifyKind === 'descendant-question'">
          <circle cx="12" cy="12" r="9" />
          <path d="M9.7 9a2.5 2.5 0 1 1 3.7 2.2c-.9.5-1.4 1.1-1.4 2.3M12 17h.01" />
        </template>
        <template v-else-if="data.notifyKind === AGENT_MESSAGE_NOTIFY_KIND">
          <path d="M21 15a4 4 0 0 1-4 4H8l-5 3V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4Z" />
        </template>
        <template v-else-if="data.notifyKind === COMPACTION_NOTIFY_KIND">
          <path d="M4 7h16v13H4zM8 3h8M9 11h6" />
        </template>
        <template v-else-if="data.notifyKind === 'subagent-completion'">
          <path d="M8 9h8M9 14h6M12 3v3M5 6h14a2 2 0 0 1 2 2v10H3V8a2 2 0 0 1 2-2Z" />
        </template>
        <template v-else>
          <path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4" />
        </template>
      </svg>
      <span
        v-if="primaryLabel"
        class="notification-label"
        data-testid="notification-label"
      >{{ primaryLabel }}</span>
      <span class="notification-kind" :class="{ primary: !primaryLabel }">{{ kindLabel }}</span>
      <span
        v-if="data.contextTruncated"
        class="notification-truncated"
        data-testid="notification-truncated"
      >(truncated)</span>
      <span
        v-if="rolledBack"
        class="compaction-badge"
        data-testid="compaction-badge"
      >rolled back</span>
      <svg v-if="isExpandable" class="notification-expand" viewBox="0 0 16 16" aria-hidden="true">
        <path :d="expanded ? 'm4 6 4 4 4-4' : 'm6 4 4 4-4 4'" />
      </svg>
    </component>
    <div
      v-if="expanded && isExpandable"
      :id="detailId"
      class="notification-body"
      data-testid="notification-body"
    >
      <dl v-if="hasSourceMetadata" class="notification-metadata">
        <template v-if="data.sourceToolName">
          <dt>Source</dt>
          <dd data-testid="notification-source">{{ data.sourceToolName }}</dd>
        </template>
        <template v-if="data.sourceToolCallId">
          <dt>Call</dt>
          <dd>{{ data.sourceToolCallId }}</dd>
        </template>
      </dl>
      <pre v-if="hasBody" class="notification-detail">{{ bodyText }}</pre>
    </div>
  </div>
</template>

<style scoped>
.notification-pill {
  display: flex;
  flex-direction: column;
  gap: 4px;
  width: 100%;
  max-width: 100%;
  box-sizing: border-box;
  padding: 2px 0;
  background: transparent;
  border: 0;
  border-left: 3px solid transparent;
  border-radius: 0;
  color: #5f6874;
  font-size: 13px;
}

/* Full-width divider for a compaction checkpoint: dashed rules read as "a line in the history". */
.notification-pill.compaction-divider {
  display: flex;
  width: 100%;
  box-sizing: border-box;
  background: #f8fafc;
  border: 1px dashed #94a3b8;
  border-radius: 8px;
  color: #334155;
}

.compaction-badge {
  padding: 0 6px;
  border-radius: 999px;
  background: #fef3c7;
  color: #92400e;
  font-size: 12px;
  font-weight: 600;
}

.notification-header {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  width: 100%;
  min-width: 0;
  padding: 5px 8px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
  user-select: none;
}

.notification-header.clickable {
  cursor: pointer;
}

.notification-header.clickable:hover,
.notification-header.clickable:focus-visible {
  background: #f1f2f4;
}

.notification-header:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 1px;
}

.notification-icon {
  width: 16px;
  height: 16px;
  flex: 0 0 16px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.4;
  color: #78818d;
}

.notification-kind {
  flex: 0 1 auto;
  min-width: 0;
  color: #7b8490;
  font-size: 12px;
  font-weight: 400;
  overflow-wrap: anywhere;
}

.notification-label {
  flex: 1 1 240px;
  min-width: 0;
  color: #5f6874;
  font-family: inherit;
  font-weight: 400;
  overflow-wrap: anywhere;
}

.notification-kind.primary {
  color: #5f6874;
  font-size: 13px;
}

.notification-truncated {
  color: #9a3412;
  font-size: 12px;
}

.notification-expand {
  width: 14px;
  height: 14px;
  flex: 0 0 14px;
  margin-left: auto;
  fill: none;
  stroke: #78818d;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.5;
}

.notification-body {
  margin: 0 8px 4px 30px;
  padding: 8px 10px;
  background: #f8f9fa;
  border: 1px solid #e1e4e8;
  border-radius: 5px;
  font-size: 12px;
  line-height: 1.4;
  overflow-x: auto;
  color: #343a40;
}

.notification-metadata {
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  gap: 2px 8px;
  margin: 0;
  color: #69727d;
}

.notification-metadata dt {
  font-weight: 600;
}

.notification-metadata dd {
  min-width: 0;
  margin: 0;
  overflow-wrap: anywhere;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
}

.notification-detail {
  margin: 6px 0 0;
  white-space: pre-wrap;
  word-wrap: break-word;
  color: #343a40;
  font: inherit;
}
</style>
