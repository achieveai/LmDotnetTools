<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { SubAgentSummary } from '@/api/subAgentsApi';
import type { TodoTask } from '@/types/todo';
import { countTodoTasks } from '@/utils/todoBoard';
import TodoBoardPanel from './TodoBoardPanel.vue';
import SubAgentListPanel from './SubAgentListPanel.vue';

type InspectorSection = 'work' | 'agents';

const props = defineProps<{
  open: boolean;
  activeSection: InspectorSection;
  tasks: TodoTask[];
  hasWork: boolean;
  children: SubAgentSummary[];
  activeConversationTabId: string;
}>();

const emit = defineEmits<{
  close: [];
  selectSection: [section: InspectorSection];
  openArtifact: [path: string];
  selectAgent: [agentId: string, closeDrawer: boolean];
}>();

const OVERLAY_MAX_WIDTH = 1100;
const rootEl = ref<HTMLElement | null>(null);
const workTab = ref<HTMLButtonElement | null>(null);
const agentsTab = ref<HTMLButtonElement | null>(null);
const overlay = ref(false);

const workCounts = computed(() => countTodoTasks(props.tasks));

function syncOverlay(): void {
  const wasOverlay = overlay.value;
  overlay.value = window.innerWidth <= OVERLAY_MAX_WIDTH;
  if (!wasOverlay && overlay.value && props.open && !rootEl.value?.contains(document.activeElement)) {
    void nextTick(() => (props.activeSection === 'work' ? workTab.value : agentsTab.value)?.focus());
  }
}

function selectSection(section: InspectorSection): void {
  emit('selectSection', section);
  void nextTick(() => (section === 'work' ? workTab.value : agentsTab.value)?.focus());
}

function onTabKeydown(event: KeyboardEvent): void {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
  event.preventDefault();
  if (event.key === 'Home') return selectSection('work');
  if (event.key === 'End') return selectSection('agents');
  selectSection(props.activeSection === 'work' ? 'agents' : 'work');
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    emit('close');
    return;
  }
  if (!overlay.value || event.key !== 'Tab' || !rootEl.value) return;
  const focusable = Array.from(
    rootEl.value.querySelectorAll<HTMLElement>('button:not([disabled]), [href], input:not([disabled]), [tabindex]:not([tabindex="-1"])')
  ).filter((element) => element.tabIndex >= 0 && !element.closest('[hidden]'));
  if (focusable.length === 0) return;
  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
}

watch(
  () => props.open,
  (open) => {
    if (open) void nextTick(() => (props.activeSection === 'work' ? workTab.value : agentsTab.value)?.focus());
  }
);

onMounted(() => {
  syncOverlay();
  window.addEventListener('resize', syncOverlay);
});

onBeforeUnmount(() => window.removeEventListener('resize', syncOverlay));
</script>

<template>
  <template v-if="props.open">
    <div
      v-if="overlay"
      class="inspector-backdrop"
      data-testid="conversation-inspector-backdrop"
      tabindex="-1"
      aria-hidden="true"
      @click="emit('close')"
    />
    <aside
      id="conversation-inspector"
      ref="rootEl"
      :class="['conversation-inspector', { overlay }]"
      data-testid="conversation-inspector"
      :role="overlay ? 'dialog' : undefined"
      :aria-modal="overlay ? 'true' : undefined"
      aria-labelledby="conversation-inspector-title"
      @keydown="onKeydown"
    >
      <header class="inspector-header">
        <h2 id="conversation-inspector-title">Work &amp; agents</h2>
        <button
          class="inspector-close"
          aria-label="Close Work and agents"
          title="Close Work and agents"
          @click="emit('close')"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
            <rect x="2.5" y="3" width="15" height="14" rx="2" />
            <path d="M12.5 3v14" />
          </svg>
        </button>
      </header>
      <div class="inspector-tabs" role="tablist" aria-label="Conversation details" @keydown="onTabKeydown">
        <button
          id="inspector-tab-work"
          ref="workTab"
          role="tab"
          :aria-selected="props.activeSection === 'work'"
          aria-controls="inspector-panel-work"
          :tabindex="props.activeSection === 'work' ? 0 : -1"
          @click="selectSection('work')"
        >Work <span v-if="props.hasWork">{{ workCounts.done }}/{{ workCounts.total }}</span></button>
        <button
          id="inspector-tab-agents"
          ref="agentsTab"
          role="tab"
          :aria-selected="props.activeSection === 'agents'"
          aria-controls="inspector-panel-agents"
          :tabindex="props.activeSection === 'agents' ? 0 : -1"
          @click="selectSection('agents')"
        >Agents <span>{{ props.children.length }}</span></button>
      </div>
      <section
        v-if="props.activeSection === 'work'"
        id="inspector-panel-work"
        class="inspector-content"
        role="tabpanel"
        aria-labelledby="inspector-tab-work"
      >
        <TodoBoardPanel v-if="props.hasWork" embedded :tasks="props.tasks" @open-artifact="emit('openArtifact', $event)" />
        <p v-else class="inspector-empty">No work yet.</p>
      </section>
      <section
        v-else
        id="inspector-panel-agents"
        class="inspector-content"
        role="tabpanel"
        aria-labelledby="inspector-tab-agents"
      >
        <SubAgentListPanel embedded :children="props.children" :active-tab-id="props.activeConversationTabId" @select="emit('selectAgent', $event, overlay)" />
      </section>
    </aside>
  </template>
</template>

<style scoped>
.conversation-inspector {
  position: relative;
  width: 320px;
  min-width: 300px;
  height: 100%;
  display: flex;
  flex-direction: column;
  background: #f8f9fa;
  border-left: 1px solid #dfe3e7;
  z-index: 20;
}

.conversation-inspector.overlay {
  position: fixed;
  top: 0;
  right: 0;
  bottom: 0;
  width: min(92vw, 340px);
  min-width: 0;
  z-index: 101;
  box-shadow: -8px 0 24px rgba(0, 0, 0, 0.18);
}

.inspector-backdrop {
  position: fixed;
  inset: 0;
  border: 0;
  background: rgba(0, 0, 0, 0.28);
  z-index: 100;
}

.inspector-header {
  display: flex;
  min-height: 54px;
  box-sizing: border-box;
  align-items: center;
  justify-content: space-between;
  padding: 12px 54px 8px 14px;
}

.inspector-header h2 {
  margin: 0;
  font-size: 16px;
}

.inspector-close {
  position: fixed;
  top: 12px;
  right: 12px;
  display: inline-flex;
  width: 34px;
  height: 34px;
  align-items: center;
  justify-content: center;
  padding: 0;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  background: #fff;
  color: #394553;
  cursor: pointer;
  transition: background 0.2s, border-color 0.2s;
}

.inspector-close svg {
  width: 18px;
  height: 18px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.5;
}

.inspector-close:hover {
  border-color: #aeb7c2;
  background: #eef1f4;
}

.inspector-close:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 2px;
}

@media (max-width: 520px) {
  .inspector-close {
    top: 10px;
  }
}

.inspector-tabs {
  display: flex;
  padding: 0 10px;
  border-bottom: 1px solid #dfe3e7;
}

.inspector-tabs button {
  flex: 1;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  padding: 9px 6px;
  color: #5b626a;
  cursor: pointer;
}

.inspector-tabs button[aria-selected='true'] {
  color: #0b5ed7;
  border-bottom-color: #0b5ed7;
  font-weight: 600;
}

.inspector-tabs span {
  margin-left: 4px;
  color: #6c757d;
  font-size: 11px;
}

.inspector-content {
  flex: 1;
  min-height: 0;
  display: flex;
  overflow: hidden;
}

.inspector-empty {
  width: 100%;
  margin: 0;
  padding: 24px 16px;
  text-align: center;
  color: #6c757d;
  font-size: 13px;
}
</style>
