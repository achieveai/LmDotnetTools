<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { ConversationTab } from '@/composables/useConversationTabs';
import { MAIN_TAB_ID } from '@/composables/useConversationTabs';
import { MAIN_TAB_COLOR } from '@/utils/agentColors';

type AgentFilter = 'all' | 'running' | 'attention' | 'completed';
const props = withDefaults(defineProps<{ tabs: ConversationTab[]; activeTabId: string; pendingQuestionAgentIds?: string[] }>(), { pendingQuestionAgentIds: () => [] });
const emit = defineEmits<{ select: [tabId: string] }>();
const root = ref<HTMLElement | null>(null);
const trigger = ref<HTMLButtonElement | null>(null);
const searchInput = ref<HTMLInputElement | null>(null);
const open = ref(false);
const query = ref('');
const filter = ref<AgentFilter>('all');
const highlightedIndex = ref(0);
const agents = computed(() => props.tabs.filter((tab) => tab.id !== MAIN_TAB_ID));
const currentAgent = computed(() => agents.value.find((tab) => tab.id === props.activeTabId) ?? null);
const pendingIds = computed(() => new Set(props.pendingQuestionAgentIds));
const needsAttention = (tab: ConversationTab) => pendingIds.value.has(tab.id) || tab.status === 'error' || tab.status === 'interrupted';
function statusLabel(tab: ConversationTab): string {
  if (pendingIds.value.has(tab.id)) return 'Awaiting answer';
  if (tab.status === 'error' && tab.failureCode) return `Error · ${tab.failureCode}`;
  if (!tab.status) return tab.kind === 'workflow' ? 'Workflow' : 'Agent';
  return tab.status.charAt(0).toUpperCase() + tab.status.slice(1);
}
const filteredAgents = computed(() => {
  const term = query.value.trim().toLocaleLowerCase();
  return agents.value.filter((tab) => {
    if (filter.value === 'running' && tab.status !== 'running') return false;
    if (filter.value === 'attention' && !needsAttention(tab)) return false;
    if (filter.value === 'completed' && tab.status !== 'completed') return false;
    return !term || [tab.label, tab.kind, tab.status, tab.failureCode, statusLabel(tab)].some((value) => value?.toLocaleLowerCase().includes(term));
  });
});
const safeId = (value: string) => value.replace(/[^a-zA-Z0-9_-]/g, '-');
const agentSelectorId = (agentId: string) => `conversation-agent-selector-${safeId(agentId)}`;
const agentViewId = (agentId: string) => `conversation-agent-view-${safeId(agentId)}`;
const optionId = (tab: ConversationTab) => `agent-picker-option-${tab.id.replace(/[^a-zA-Z0-9_-]/g, '-')}`;
const activeDescendant = computed(() => filteredAgents.value[highlightedIndex.value] ? optionId(filteredAgents.value[highlightedIndex.value]) : undefined);
const agentTriggerControls = computed(() => [
  currentAgent.value ? agentViewId(currentAgent.value.id) : null,
  open.value ? 'agent-picker-list' : null,
].filter(Boolean).join(' ') || undefined);
const color = (tab: ConversationTab | null) => tab?.color ?? MAIN_TAB_COLOR;
function showPicker(): void {
  open.value = true;
  query.value = '';
  const selected = filteredAgents.value.findIndex((tab) => tab.id === props.activeTabId);
  highlightedIndex.value = selected >= 0 ? selected : 0;
  void nextTick(() => searchInput.value?.focus());
}
function closePicker(restoreFocus = true): void {
  if (!open.value) return;
  open.value = false;
  if (restoreFocus) void nextTick(() => trigger.value?.focus());
}
function choose(tab: ConversationTab): void { emit('select', tab.id); closePicker(); }
function setFilter(value: AgentFilter): void { filter.value = value; highlightedIndex.value = 0; void nextTick(() => searchInput.value?.focus()); }
function revealHighlighted(): void {
  void nextTick(() => root.value?.querySelectorAll<HTMLElement>('[role="option"]')[highlightedIndex.value]?.scrollIntoView({ block: 'nearest' }));
}
function onSearchKeydown(event: KeyboardEvent): void {
  const length = filteredAgents.value.length;
  if (event.key === 'Escape') { event.preventDefault(); closePicker(); return; }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault();
    if (length) { highlightedIndex.value = (highlightedIndex.value + (event.key === 'ArrowDown' ? 1 : -1) + length) % length; revealHighlighted(); }
  } else if (event.key === 'Home' || event.key === 'End') {
    event.preventDefault();
    if (length) { highlightedIndex.value = event.key === 'Home' ? 0 : length - 1; revealHighlighted(); }
  } else if (event.key === 'Enter') {
    event.preventDefault();
    const tab = filteredAgents.value[highlightedIndex.value];
    if (tab) choose(tab);
  }
}
function onPopoverKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') { event.preventDefault(); closePicker(); }
}
function onPopoverMouseDown(event: MouseEvent): void {
  // Nothing pressed inside the popover may move focus out of the search box. The browser moves focus
  // as mousedown's DEFAULT ACTION, which fires `focusout` while `document.activeElement` is still
  // `document.body`; `onFocusOut` defers to `nextTick`, and the browser runs that microtask before
  // focus lands on the pressed control, so it sees focus outside the nav and closes the popover
  // before the click is ever delivered. The search input keeps its default so caret placement and
  // text selection still work.
  if (event.target !== searchInput.value) event.preventDefault();
}
function onFocusOut(): void {
  void nextTick(() => { if (open.value && !root.value?.contains(document.activeElement)) closePicker(false); });
}
function onDocumentPointerDown(event: PointerEvent): void {
  if (open.value && event.target instanceof Node && !root.value?.contains(event.target)) closePicker(false);
}
function onDocumentFocusIn(event: FocusEvent): void {
  if (open.value && event.target instanceof Node && !root.value?.contains(event.target)) closePicker(false);
}
watch([filteredAgents, query, filter], () => { if (highlightedIndex.value >= filteredAgents.value.length) highlightedIndex.value = 0; });
onMounted(() => { document.addEventListener('pointerdown', onDocumentPointerDown); document.addEventListener('focusin', onDocumentFocusIn); });
onBeforeUnmount(() => { document.removeEventListener('pointerdown', onDocumentPointerDown); document.removeEventListener('focusin', onDocumentFocusIn); });
</script>

<template>
  <nav ref="root" class="conversation-tabs" data-testid="conversation-tabs" aria-label="Conversation views" @focusout="onFocusOut">
    <button id="conversation-main-selector" type="button" class="conversation-anchor" :class="{ active: activeTabId === MAIN_TAB_ID }"
      data-testid="conversation-tab" :data-tab-id="MAIN_TAB_ID" aria-controls="conversation-main-view"
      :aria-current="activeTabId === MAIN_TAB_ID ? 'page' : undefined" @click="emit('select', MAIN_TAB_ID)">
      <span class="conversation-dot" :style="{ background: MAIN_TAB_COLOR }" aria-hidden="true" /><span>Main conversation</span>
    </button>
    <div v-if="agents.length" class="agent-picker">
      <button ref="trigger" :id="currentAgent ? agentSelectorId(currentAgent.id) : undefined" type="button"
        class="conversation-anchor agent-picker__trigger" :class="{ active: currentAgent }"
        data-testid="conversation-tab" :data-tab-id="currentAgent?.id || 'agents'" aria-haspopup="listbox" :aria-expanded="open"
        :aria-controls="agentTriggerControls" :aria-current="currentAgent ? 'page' : undefined"
        @click="open ? closePicker() : showPicker()">
        <span class="conversation-dot" :style="{ background: color(currentAgent) }" aria-hidden="true" />
        <span class="agent-picker__trigger-label">{{ currentAgent?.label || `Agents (${agents.length})` }}</span>
        <span v-if="currentAgent" class="agent-picker__trigger-status">{{ statusLabel(currentAgent) }}</span>
        <span v-if="currentAgent" class="agent-picker__count">Agents {{ agents.length }}</span>
        <svg class="agent-picker__chevron" :class="{ 'agent-picker__chevron--open': open }" viewBox="0 0 16 16"
          aria-hidden="true" focusable="false"><path d="M4 6.25 8 10.25l4-4" /></svg>
      </button>
      <div v-if="open" class="agent-picker__popover" data-testid="agent-picker-popover"
        @keydown.capture="onPopoverKeydown" @mousedown="onPopoverMouseDown">
        <label class="agent-picker__search-label" for="agent-picker-search">Find an agent</label>
        <input id="agent-picker-search" ref="searchInput" v-model="query" class="agent-picker__search" data-testid="agent-picker-search"
          type="search" autocomplete="off" role="combobox" aria-controls="agent-picker-list" aria-autocomplete="list"
          :aria-expanded="open" :aria-activedescendant="activeDescendant" @keydown="onSearchKeydown" />
        <div class="agent-picker__filters" aria-label="Filter agents">
          <button v-for="item in ([['all', 'All'], ['running', 'Running'], ['attention', 'Needs attention'], ['completed', 'Completed']] as const)"
            :key="item[0]" type="button" :class="{ active: filter === item[0] }" :aria-pressed="filter === item[0]"
            :data-testid="`agent-filter-${item[0]}`" @click="setFilter(item[0])">{{ item[1] }}</button>
        </div>
        <ul id="agent-picker-list" class="agent-picker__list" role="listbox" aria-label="Agents">
          <li v-for="(tab, index) in filteredAgents" :id="optionId(tab)" :key="tab.id" role="option"
            :aria-selected="tab.id === activeTabId" data-testid="agent-picker-option" :data-agent-id="tab.id"
            :aria-controls="tab.id === activeTabId ? agentViewId(tab.id) : undefined"
            class="agent-picker__option" :class="{ highlighted: index === highlightedIndex }" :title="tab.label"
            @mouseenter="highlightedIndex = index" @mousedown.prevent @click="choose(tab)">
              <span class="conversation-dot" :style="{ background: color(tab) }" aria-hidden="true" />
              <span class="agent-picker__option-copy"><span class="agent-picker__option-name">{{ tab.label }}</span>
                <span class="agent-picker__option-meta">{{ tab.kind === 'workflow' ? 'Workflow' : 'Agent' }} · {{ statusLabel(tab) }}</span></span>
          </li>
          <li v-if="!filteredAgents.length" class="agent-picker__empty">No agents match.</li>
        </ul>
      </div>
    </div>
  </nav>
</template>

<style scoped>
.conversation-tabs{position:relative;display:flex;align-items:center;gap:5px;min-width:0;padding:5px 10px;border-bottom:1px solid #e0e4e8;background:#fff}.conversation-anchor{display:inline-flex;align-items:center;gap:6px;min-width:0;max-width:min(390px,68vw);padding:6px 9px;border:0;border-radius:7px;background:transparent;color:#5f6874;font:inherit;font-size:13px;cursor:pointer;white-space:nowrap}.conversation-anchor:hover{background:#f3f5f7}.conversation-anchor.active{background:#e9edf1;color:#343a40;font-weight:500}.conversation-anchor:focus-visible,.agent-picker button:focus-visible,.agent-picker input:focus-visible{outline:2px solid #2d6cdf;outline-offset:1px}.conversation-dot{width:7px;height:7px;flex:0 0 auto;border-radius:50%}.agent-picker{position:relative;min-width:0;margin-left:auto}.agent-picker__trigger-label{min-width:0;overflow:hidden;text-overflow:ellipsis}.agent-picker__trigger-status,.agent-picker__count{flex:0 0 auto;color:#76818e;font-size:11px;font-weight:400}.agent-picker__count{padding-left:6px;border-left:1px solid #cdd3da}.agent-picker__chevron{width:13px;height:13px;flex:0 0 13px;color:#78828e;fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:1.5;transition:transform .15s ease}.agent-picker__chevron--open{transform:rotate(180deg)}.agent-picker__popover{position:absolute;z-index:30;top:calc(100% + 6px);right:0;width:min(430px,calc(100vw - 20px));padding:12px;border:1px solid #d9dee5;border-radius:10px;background:#fff;box-shadow:0 14px 34px rgba(35,45,58,.15)}.agent-picker__search-label{display:block;margin-bottom:5px;color:#5f6874;font-size:12px}.agent-picker__search{width:100%;padding:8px 10px;border:1px solid #cbd2dc;border-radius:7px;color:#303b49;font:inherit}.agent-picker__filters{display:flex;gap:5px;margin:10px 0;overflow-x:auto;scrollbar-width:none}.agent-picker__filters::-webkit-scrollbar{display:none}.agent-picker__filters button{flex:0 0 auto;padding:5px 8px;border:1px solid #d7dce3;border-radius:999px;background:#fff;color:#5f6874;font:inherit;font-size:11px;cursor:pointer}.agent-picker__filters button.active{border-color:#b9c7d8;background:#eef3f8;color:#344d69}.agent-picker__list{max-height:min(52vh,390px);margin:0;padding:0;overflow-y:auto;list-style:none}.agent-picker__option{display:flex;align-items:flex-start;gap:9px;width:100%;padding:9px 8px;border-radius:7px;color:#34404d;text-align:left;cursor:pointer}.agent-picker__option.highlighted,.agent-picker__option:hover{background:#f2f5f8}.agent-picker__option .conversation-dot{margin-top:6px}.agent-picker__option-copy{display:flex;min-width:0;flex:1;flex-direction:column;gap:2px}.agent-picker__option-name{overflow-wrap:anywhere;font-size:13px;line-height:1.35}.agent-picker__option-meta{color:#77818d;font-size:11px}.agent-picker__empty{padding:22px 8px;color:#77818d;font-size:13px;text-align:center}@media(max-width:520px){.conversation-tabs{padding-inline:8px}.conversation-anchor{max-width:64vw;padding-inline:7px}.conversation-anchor:first-child{max-width:34vw}.conversation-anchor:first-child span:last-child{overflow:hidden;text-overflow:ellipsis}.agent-picker__trigger-status{display:none}.agent-picker__count{padding-left:0;border-left:0}.agent-picker__popover{right:-2px;width:calc(100vw - 20px)}}
</style>
