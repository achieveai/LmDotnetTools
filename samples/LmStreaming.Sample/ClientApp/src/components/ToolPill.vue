<script setup lang="ts">
import { computed, inject, ref } from 'vue';
import type { Component } from 'vue';
import type { ToolCall } from '@/types';
import type { ToolCallState } from '@/utils/toolTypes';
import { resolveRenderer, deriveToolPillState } from '@/utils';
import { useToolResult } from '@/composables/useToolResult';
import {
  GET_AGENT_COLOR,
  GET_AGENT_ROUTING,
  resolveAgentIdFromCall,
  type AgentColorLookup,
  type AgentRoutingLookup,
} from '@/utils/agentColors';
import CodeBlockRich from '@/components/tools/CodeBlockRich.vue';
import DiffRich from '@/components/tools/DiffRich.vue';
import TerminalRich from '@/components/tools/TerminalRich.vue';
import MatchesRich from '@/components/tools/MatchesRich.vue';
import WeatherRich from '@/components/tools/WeatherRich.vue';
import QuestionRich from '@/components/tools/QuestionRich.vue';
import { normalizeToolName } from '@/utils/toolName';

const props = withDefaults(defineProps<{
  toolCall: ToolCall;
  presentation?: 'card' | 'activity-row';
}>(), { presentation: 'card' });

const { getResult } = useToolResult();

const expanded = ref(false);
function toggle() {
  expanded.value = !expanded.value;
}

const resultMsg = computed(() => getResult(props.toolCall));
const renderer = computed(() => resolveRenderer(props.toolCall.function_name));

const view = computed(() =>
  deriveToolPillState({
    functionArgs: props.toolCall.function_args,
    result: resultMsg.value?.result ?? null,
    hasResult: resultMsg.value !== null,
    isErrorFlag: resultMsg.value?.is_error ?? null,
    isDeferred: resultMsg.value?.is_deferred ?? false,
  })
);

const summary = computed(() => {
  try {
    return renderer.value.summarize(view.value.parsedArgs, view.value.resultText, view.value);
  } catch {
    return '';
  }
});

const normalizedToolName = computed(() => normalizeToolName(props.toolCall.function_name));

function stringArg(...keys: string[]): string {
  for (const key of keys) {
    const value = view.value.parsedArgs?.[key];
    if (typeof value === 'string' && value.trim()) return value.trim();
  }
  return '';
}

function humanizeToolName(name: string): string {
  const spaced = name.replace(/[_-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : 'Tool activity';
}

const activityDescription = computed(() => {
  const state = view.value.state;
  const succeeded = state === 'success';
  const failed = state === 'error';
  const detail = summary.value;
  const family = renderer.value.family;
  const toolName = normalizedToolName.value;

  if (toolName === 'sendmessage') {
    const target = stringArg('target', 'agent_id');
    return failed
      ? `Failed to send message${target ? ` to ${target}` : ''}`
      : `${succeeded ? 'Sent' : 'Sending'} message${target ? ` to ${target}` : ''}`;
  }
  if (toolName === 'agent') {
    const target = stringArg('subagent_type', 'name');
    return failed
      ? `Failed to start${target ? ` ${target}` : ' agent'}`
      : `${succeeded ? 'Started' : 'Starting'}${target ? ` ${target}` : ' agent'}`;
  }
  if (toolName.includes('checkagent') || toolName === 'getagents') {
    return `${failed ? 'Failed to check' : succeeded ? 'Checked' : 'Checking'} agent status`;
  }
  if (toolName === 'view_image') {
    return failed ? 'Failed to view an image' : succeeded ? 'Viewed an image' : 'Viewing an image';
  }

  switch (family) {
    case 'read': {
      const path = stringArg('file_path');
      return `${failed ? 'Failed to read' : succeeded ? 'Read' : 'Reading'} ${path || 'a file'}`;
    }
    case 'write': {
      const path = stringArg('file_path') || 'file';
      return `${failed ? 'Failed to write' : succeeded ? 'Wrote' : 'Writing'} ${path}`;
    }
    case 'edit': {
      const path = stringArg('file_path') || 'file';
      const stats = detail.includes('·') ? detail.slice(detail.indexOf('·') + 1).trim() : '';
      return `${failed ? 'Failed to update' : succeeded ? 'Updated' : 'Updating'} ${path}${stats ? ` · ${stats}` : ''}`;
    }
    case 'shell':
      return `${failed ? 'Command failed' : succeeded ? 'Ran' : 'Running'}${detail ? ` ${detail}` : ' command'}`;
    case 'grep':
    case 'glob':
      return `${failed ? 'Search failed' : succeeded ? 'Searched for' : 'Searching for'}${detail ? ` ${detail}` : ''}`;
    case 'math':
      return `${failed ? 'Calculation failed' : succeeded ? 'Calculated' : 'Calculating'}${detail ? ` ${detail}` : ''}`;
    case 'web':
      return `${failed ? 'Web request failed' : succeeded ? 'Opened' : 'Opening'}${detail ? ` ${detail}` : ' web resource'}`;
    default: {
      const label = humanizeToolName(props.toolCall.function_name || toolName);
      return `${failed ? `${label} failed` : label}${detail ? ` · ${detail}` : ''}`;
    }
  }
});

const activityIconPath = computed(() => {
  switch (renderer.value.family) {
    case 'read': return 'M3 2.5h6l3 3v8H3z M9 2.5v3h3';
    case 'write': return 'M3 13h2.5L13 5.5 10.5 3 3 10.5z M9.5 4l2.5 2.5';
    case 'edit': return 'M3 13h2.5L13 5.5 10.5 3 3 10.5z';
    case 'shell': return 'M2.5 3.5h11v9h-11z M4.5 6l2 2-2 2 M8 10h3';
    case 'grep':
    case 'glob': return 'M7 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M10 10l3 3';
    case 'agent': return 'M8 2.5v2 M4 6h8v6H4z M2.5 8v2 M13.5 8v2 M6 8h.01 M10 8h.01';
    case 'math': return 'M3 4h10 M8 2v4 M4 9h3 M10 8l3 3 M13 8l-3 3';
    case 'web': return 'M8 2.5a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11z M2.5 8h11 M8 2.5c1.5 1.5 2.2 3.3 2.2 5.5S9.5 12 8 13.5C6.5 12 5.8 10.2 5.8 8S6.5 4 8 2.5z';
    default: return 'M8 2.5v2 M8 11.5v2 M2.5 8h2 M11.5 8h2 M4.1 4.1l1.4 1.4 M10.5 10.5l1.4 1.4 M11.9 4.1l-1.4 1.4 M5.5 10.5l-1.4 1.4 M8 6a2 2 0 1 0 0 4 2 2 0 0 0 0-4z';
  }
});

/** EXACT raw result string — the frozen `.tool-call-result` contract (never pretty-printed). */
const rawResult = computed(() => resultMsg.value?.result ?? '');

// Tint a sub-agent CALL pill (Agent / SendMessage / CheckAgent) with the target sub-agent's assigned
// color so it visually links to that agent's tab. Exact-agentId only — a call we can't resolve to a
// known agent stays uncolored (unchanged appearance). Purely additive style; testids are untouched.
const getAgentColor = inject<AgentColorLookup>(GET_AGENT_COLOR, () => null);
const agentColor = computed<string | null>(() => {
  if (renderer.value.family !== 'agent') return null;
  const agentId = resolveAgentIdFromCall(view.value.parsedArgs, rawResult.value);
  return agentId ? getAgentColor(agentId) : null;
});

const getAgentRouting = inject<AgentRoutingLookup>(GET_AGENT_ROUTING, () => null);
const agentRouting = computed(() => {
  if (renderer.value.family !== 'agent') return null;
  return getAgentRouting(view.value.parsedArgs, rawResult.value);
});
const selectionSource = computed(() =>
  agentRouting.value?.modelSelectionSource?.replace(/-/g, ' ') ?? 'unavailable'
);

const argEntries = computed(() =>
  view.value.parsedArgs ? Object.entries(view.value.parsedArgs) : []
);

/** family → rich component. Families without an entry render the generic body. */
const richMap: Partial<Record<string, Component>> = {
  read: CodeBlockRich,
  write: CodeBlockRich,
  edit: DiffRich,
  shell: TerminalRich,
  task: TerminalRich,
  grep: MatchesRich,
  weather: WeatherRich,
  question: QuestionRich,
};
/**
 * True only when THIS pill's live form was moved out to `PendingQuestionDock`.
 *
 * Deliberately narrower than `view.state === 'awaiting-input'`, which `deriveToolPillState` sets
 * for ANY deferred result — a block-mode `Wait` from `WaitToolProvider` reaches that state too.
 * `findPendingQuestions` docks only the `question` family, so gating on the broader state would
 * point a waiting `Wait` pill at a dock that does not exist.
 */
const isDockedQuestion = computed(
  () => renderer.value.family === 'question' && view.value.isDeferred
);

const richComponent = computed<Component | null>(() => {
  // A question still awaiting an answer is rendered by PendingQuestionDock, above the chat input
  // (answering is a client capability, and inside this pill the form was routinely hidden). The
  // pill keeps the RESOLVED read-only Q&A as history -- so exactly one live `question-form` exists
  // at a time, which also keeps Playwright's strict-mode selectors unambiguous.
  if (isDockedQuestion.value) return null;
  return richMap[renderer.value.family] ?? null;
});

/** Single source of truth for the visible glyph AND the screen-reader label per state. */
const STATUS: Record<ToolCallState, { icon: string; label: string }> = {
  success: { icon: '✓', label: 'succeeded' },
  error: { icon: '⚠', label: 'failed' },
  'awaiting-result': { icon: '◌', label: 'running' },
  'streaming-args': { icon: '…', label: 'receiving' },
  'awaiting-input': { icon: '❓', label: 'waiting for your answer' },
};
const statusIcon = computed(() => STATUS[view.value.state].icon);
const statusLabel = computed(() => STATUS[view.value.state].label);
const displayedStatusLabel = computed(() =>
  props.presentation === 'activity-row' && view.value.state === 'awaiting-input'
    ? 'awaiting input'
    : statusLabel.value
);

function fmtVal(v: unknown): string {
  return typeof v === 'string' ? v : JSON.stringify(v);
}

const copied = ref(false);
async function copyResult() {
  const text = rawResult.value;
  try {
    await navigator?.clipboard?.writeText?.(text);
    copied.value = true;
    setTimeout(() => (copied.value = false), 1200);
  } catch {
    /* clipboard unavailable (e.g. headless) — non-fatal */
  }
}
</script>

<template>
  <div
    class="tool-pill"
    :class="[
      `f-${renderer.family}`,
      `st-${view.state}`,
      { 'has-agent-color': agentColor, 'tool-pill--activity-row': presentation === 'activity-row' },
    ]"
    :style="agentColor && presentation === 'card' ? { borderLeftColor: agentColor, borderLeftWidth: '3px' } : undefined"
    data-testid="tool-call-pill"
    :data-tool-name="toolCall.function_name || undefined"
  >
    <button type="button" class="tool-pill__header" :aria-expanded="expanded" @click="toggle">
      <svg
        v-if="presentation === 'activity-row'"
        class="tool-pill__activity-icon"
        :class="{ pulsing: !view.hasResult }"
        viewBox="0 0 16 16"
        aria-hidden="true"
        focusable="false"
      >
        <path :d="activityIconPath" />
      </svg>
      <span v-else class="tool-pill__icon" :class="{ pulsing: !view.hasResult }" aria-hidden="true">{{
        renderer.icon
      }}</span>
      <span class="sr-only">{{ renderer.iconAlt }}</span>
      <span v-if="presentation === 'activity-row'" class="tool-pill__activity-description">
        {{ activityDescription }}
        <span v-if="view.isBackground" class="tool-pill__activity-qualifier">Running in background</span>
        <span v-else-if="view.state === 'awaiting-input'" class="tool-pill__activity-qualifier">Waiting for input</span>
      </span>
      <template v-else>
        <span class="tool-pill__title">{{ toolCall.function_name || 'tool' }}</span>
        <span class="tool-pill__summary">{{ summary }}</span>
        <span v-if="view.isBackground" class="tool-pill__chip">background</span>
      </template>
      <span class="tool-pill__status" :class="`st-${view.state}`" :title="displayedStatusLabel">
        <span class="sr-only">{{ displayedStatusLabel }}</span>
        <span aria-hidden="true">{{ presentation === 'card' ? statusIcon : view.state === 'error' ? '!' : view.state === 'awaiting-input' ? '?' : view.hasResult ? '●' : '◌' }}</span>
      </span>
      <span v-if="presentation === 'card'" class="tool-pill__chevron" aria-hidden="true">{{ expanded ? '▾' : '▸' }}</span>
    </button>

    <div v-if="expanded" class="tool-pill__body">
      <div
        v-if="presentation === 'activity-row'"
        class="tool-pill__technical-name"
        data-testid="tool-technical-name"
      >
        <code>{{ toolCall.function_name || 'tool' }}</code>
      </div>
      <section
        v-if="renderer.family === 'agent'"
        class="agent-routing"
        data-testid="agent-controller-request"
      >
        <strong>Controller requested</strong>
        <span>model: {{ fmtVal(view.parsedArgs?.model) }}</span>
        <span>model intelligence: {{ fmtVal(view.parsedArgs?.modelIntelligence) }}</span>
      </section>

      <section
        v-if="agentRouting"
        class="agent-routing agent-routing--effective"
        data-testid="agent-effective-routing"
      >
        <strong>Effective child routing</strong>
        <span>model: {{ agentRouting.effectiveModelId ?? 'unavailable' }}</span>
        <span>
          model intelligence: {{ agentRouting.effectiveModelIntelligence ?? 'not tier-based' }}
        </span>
        <span>source: {{ selectionSource }}</span>
      </section>

      <dl v-if="argEntries.length" class="kv">
        <template v-for="[k, v] in argEntries" :key="k">
          <dt>{{ k }}</dt>
          <dd>{{ fmtVal(v) }}</dd>
        </template>
      </dl>

      <p
        v-if="isDockedQuestion"
        class="tool-pill__dock-pointer"
        data-testid="question-dock-pointer"
      >
        This question is waiting for you — answer it just above the message box.
      </p>

      <component
        :is="richComponent"
        v-if="richComponent"
        class="tool-rich"
        :view="view"
        :tool-call="toolCall"
      />

      <p v-if="view.isError && view.errorText" class="tool-pill__error">{{ view.errorText }}</p>

      <div v-if="view.hasResult" class="tool-pill__result">
        <button type="button" class="tool-pill__copy" @click="copyResult">
          {{ copied ? 'Copied' : 'Copy' }}
        </button>
        <pre class="tool-call-result">{{ rawResult }}</pre>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* Layout containment (re-homed from MetadataPill): min-width:0 lets the flex row shrink,
   overflow-x:auto keeps wide expanded content from blowing out the pill (past overflow bug).
   position:relative makes the pill the containing block for its own absolutely-positioned
   .sr-only accessibility labels — without it those labels resolve to the initial containing
   block (all ancestors are position:static), escape .chat-layout's overflow clip, and lengthen
   the document into a spurious whole-page scrollbar instead of scrolling inside .message-list. */
.tool-pill {
  background: #fff;
  border-radius: 8px;
  padding: 2px;
  border: 1px solid transparent;
  min-width: 0;
  position: relative;
}

.tool-pill:hover {
  border-color: #d0d0d0;
}

.tool-pill--activity-row {
  padding: 0;
  border: 0;
  border-radius: 0;
  background: transparent;
}

.tool-pill--activity-row:hover {
  border-color: transparent;
}

.tool-pill__header {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 10px;
  background: transparent;
  border: none;
  font: inherit;
  font-size: 14px;
  text-align: left;
  cursor: pointer;
  border-radius: 8px;
}

.tool-pill__header:hover {
  background: #f8f8f8;
}

.tool-pill--activity-row .tool-pill__header {
  min-height: 30px;
  gap: 8px;
  padding: 3px 2px;
  border-radius: 4px;
  color: #68727d;
  font-size: 14px;
  font-weight: 400;
}

.tool-pill--activity-row .tool-pill__header:hover,
.tool-pill--activity-row .tool-pill__header:focus-visible {
  background: #f5f6f7;
  color: #48525c;
}

.tool-pill--activity-row .tool-pill__header:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 2px;
}

.tool-pill__activity-icon {
  width: 16px;
  height: 16px;
  flex: 0 0 20px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.15;
}

.tool-pill__activity-icon.pulsing {
  animation: tool-pill-pulse 2s ease-in-out infinite;
}

.tool-pill__activity-description {
  min-width: 0;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-pill__activity-qualifier {
  margin-left: 7px;
  color: #858d95;
  font-size: 12px;
}

.tool-pill__icon {
  font-size: 16px;
  flex-shrink: 0;
}

.tool-pill__icon.pulsing {
  animation: tool-pill-pulse 2s ease-in-out infinite;
}

@keyframes tool-pill-pulse {
  0%,
  100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: 0.6;
    transform: scale(0.95);
  }
}

@media (prefers-reduced-motion: reduce) {
  .tool-pill__icon.pulsing,
  .tool-pill__activity-icon.pulsing {
    animation: none;
  }
}

.tool-pill__title {
  font-weight: 600;
  color: #333;
  flex-shrink: 0;
}

.tool-pill__summary {
  color: #666;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-pill__chip {
  flex-shrink: 0;
  font-size: 11px;
  font-weight: 600;
  color: #8a6d3b;
  background: #fcf3d9;
  border-radius: 10px;
  padding: 1px 8px;
}

.tool-pill__status {
  flex-shrink: 0;
  font-weight: 700;
}

.tool-pill__status.st-success {
  color: #2e7d32;
}

.tool-pill__status.st-error {
  color: #d32f2f;
}

.tool-pill__status.st-awaiting-result,
.tool-pill__status.st-streaming-args {
  color: #999;
}

.tool-pill__status.st-awaiting-input {
  color: #b8860b;
}

.tool-pill--activity-row .tool-pill__status {
  width: 14px;
  color: #9aa1a8;
  font-size: 8px;
  font-weight: 600;
  text-align: center;
}

.tool-pill--activity-row .tool-pill__status.st-success {
  color: #87918a;
}

.tool-pill--activity-row .tool-pill__status.st-error {
  color: #b3261e;
  font-size: 13px;
  font-weight: 700;
}

.tool-pill--activity-row .tool-pill__status.st-awaiting-input {
  color: #9a7210;
}

.tool-pill__chevron {
  color: #999;
  font-size: 10px;
  flex-shrink: 0;
  margin-left: auto;
}

.tool-pill__body {
  margin-top: 8px;
  padding: 8px 10px;
  border-top: 1px solid #e0e0e0;
  overflow-x: auto;
}

.tool-pill--activity-row .tool-pill__body {
  margin: 5px 0 0 28px;
  padding: 8px 10px;
  border: 1px solid #e3e6e8;
  border-radius: 6px;
  background: #fff;
}

.tool-pill__technical-name {
  margin: 0 0 8px;
  color: #7a838c;
  font-size: 11px;
}

.tool-pill__technical-name code {
  color: #515b65;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
}

.agent-routing {
  display: grid;
  gap: 2px;
  margin: 0 0 8px;
  padding: 7px 9px;
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  color: #555;
  font-size: 12px;
}

.agent-routing--effective {
  border-color: #9bc7a0;
  background: #f2faf3;
  color: #245b2b;
}

.kv {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 2px 10px;
  margin: 0 0 8px;
  font-size: 12px;
}

.kv dt {
  font-weight: 600;
  color: #666;
}

.kv dd {
  margin: 0;
  color: #333;
  overflow-wrap: anywhere;
}

.tool-pill__error {
  margin: 0 0 8px;
  color: #d32f2f;
  font-size: 13px;
  font-style: italic;
}

.tool-pill__dock-pointer {
  margin: 0 0 8px;
  color: #8a5a00;
  font-size: 12px;
}

.tool-call-result {
  margin: 0;
  padding: 8px;
  background: #f8f9fa;
  border-radius: 4px;
  font-size: 12px;
  line-height: 1.4;
  white-space: pre-wrap;
  word-wrap: break-word;
  max-height: 320px;
  overflow: auto;
  color: #555;
}

.tool-pill__result {
  position: relative;
}

.tool-pill__copy {
  position: absolute;
  top: 4px;
  right: 4px;
  font-size: 11px;
  padding: 1px 8px;
  border: 1px solid #d0d0d0;
  border-radius: 6px;
  background: #fff;
  color: #555;
  cursor: pointer;
}

.tool-pill__copy:hover {
  background: #f0f0f0;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}
</style>
