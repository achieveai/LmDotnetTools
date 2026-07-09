<script setup lang="ts">
import { computed, ref } from 'vue';
import type { NotificationDisplayData, NotifyMessage } from '@/types';

/**
 * Presentational pill for an out-of-band notification (async sub-agent completion, sandbox
 * context-discovery, monitors, timers). Distinct from a user bubble. Accepts either the normalized
 * {@link NotificationDisplayData} that `displayItems` produces, or a raw {@link NotifyMessage}
 * (normalized here) so callers/tests can pass whichever they hold.
 */
const props = defineProps<{
  notification: NotificationDisplayData | NotifyMessage;
}>();

/** Normalize the prop to a single shape regardless of whether a raw NotifyMessage was passed. */
const data = computed<NotificationDisplayData>(() => {
  const n = props.notification;
  // Only a raw NotifyMessage carries `$type`; NotificationDisplayData never does.
  if ('$type' in n) {
    return {
      notifyKind: n.notify_kind,
      label: n.label,
      sourceToolName: n.source_tool_name,
      sourceToolCallId: n.source_tool_call_id,
      detail: n.detail,
      text: n.text,
    };
  }
  return n;
});

/** Icon per well-known kind; a bell is the generic fallback for future kinds. */
const icon = computed<string>(() => {
  switch (data.value.notifyKind) {
    case 'context-discovery':
      return '\u{1F4C4}'; // 📄
    case 'subagent-completion':
      return '\u{1F916}'; // 🤖
    default:
      return '\u{1F514}'; // 🔔
  }
});

/** Human-friendly heading per well-known kind; unknown kinds show the raw kind string. */
const kindLabel = computed<string>(() => {
  switch (data.value.notifyKind) {
    case 'context-discovery':
      return 'Context loaded';
    case 'subagent-completion':
      return 'Sub-agent completed';
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

const expanded = ref(false);
function toggle(): void {
  if (hasBody.value) {
    expanded.value = !expanded.value;
  }
}
</script>

<template>
  <div
    class="notification-pill"
    data-testid="notification-pill"
    :data-notify-kind="data.notifyKind"
  >
    <div
      class="notification-header"
      :class="{ clickable: hasBody }"
      @click="toggle"
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
