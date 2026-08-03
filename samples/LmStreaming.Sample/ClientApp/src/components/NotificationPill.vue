<script setup lang="ts">
import { computed, inject, ref, toRef } from 'vue';
import type { AgentMessageType, NotificationDisplayData } from '@/types';
import { AGENT_MESSAGE_NOTIFY_KIND } from '@/composables/messageDisplay';
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

/** Icon per well-known kind; a bell is the generic fallback for future kinds. */
const icon = computed<string>(() => {
  switch (data.value.notifyKind) {
    case 'context-discovery':
      return '\u{1F4C4}'; // 📄
    case 'subagent-completion':
      return '\u{1F916}'; // 🤖
    case 'descendant-question':
      return '❓'; // ❓
    case AGENT_MESSAGE_NOTIFY_KIND:
      return '\u{1F4AC}'; // 💬
    default:
      return '\u{1F514}'; // 🔔
  }
});

/** Human-friendly headings for the existing agent-to-agent message types (#244). */
const AGENT_MESSAGE_HEADINGS: Record<AgentMessageType, string> = {
  Question: 'Agent asked',
  DelegateTask: 'Agent delegated',
  TaskUpdate: 'Agent update',
  Steer: 'Agent steered',
  Response: 'Agent replied',
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
const isClickable = computed<boolean>(() => hasBody.value || isNavigable.value);

const expanded = ref(false);
function handleHeaderClick(): void {
  if (isNavigable.value) {
    goToAgentTab(data.value.sourceToolCallId!);
    return;
  }
  if (hasBody.value) {
    expanded.value = !expanded.value;
  }
}

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
    data-testid="notification-pill"
    :data-notify-kind="data.notifyKind"
    :style="agentColor ? { borderLeftColor: agentColor, borderLeftWidth: '3px' } : undefined"
  >
    <div
      class="notification-header"
      :class="{ clickable: isClickable }"
      @click="handleHeaderClick"
    >
      <span class="notification-icon" aria-hidden="true">{{ icon }}</span>
      <span class="notification-kind">{{ kindLabel }}</span>
      <span
        v-if="data.sourceToolName"
        class="notification-source"
        data-testid="notification-source"
      >&larr; {{ data.sourceToolName }}</span>
      <span
        v-if="primaryLabel"
        class="notification-label"
        data-testid="notification-label"
      >{{ primaryLabel }}</span>
      <span
        v-if="data.contextTruncated"
        class="notification-truncated"
        data-testid="notification-truncated"
      >(truncated)</span>
      <span v-if="hasBody" class="notification-expand" aria-hidden="true">{{ expanded ? '▼' : '▶' }}</span>
    </div>
    <pre v-if="expanded && hasBody" class="notification-body" data-testid="notification-body">{{ bodyText }}</pre>
  </div>
</template>

<style scoped>
.notification-pill {
  display: inline-flex;
  flex-direction: column;
  gap: 6px;
  max-width: 100%;
  padding: 6px 10px;
  background: #eef2ff;
  border: 1px solid #c7d2fe;
  border-radius: 12px;
  color: #3730a3;
  font-size: 13px;
}

.notification-header {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  user-select: none;
}

.notification-header.clickable {
  cursor: pointer;
}

.notification-icon {
  font-size: 15px;
  flex-shrink: 0;
}

.notification-kind {
  font-weight: 600;
}

.notification-source {
  color: #4f46e5;
  font-family: monospace;
  font-size: 12px;
}

.notification-label {
  font-family: monospace;
  color: #4338ca;
}

.notification-truncated {
  color: #9a3412;
  font-size: 12px;
}

.notification-expand {
  color: #6366f1;
  font-size: 10px;
  margin-left: auto;
}

.notification-body {
  margin: 0;
  padding: 8px;
  background: #ffffff;
  border: 1px solid #e0e7ff;
  border-radius: 6px;
  font-size: 12px;
  line-height: 1.4;
  white-space: pre-wrap;
  word-wrap: break-word;
  overflow-x: auto;
  color: #1e1b4b;
}
</style>
