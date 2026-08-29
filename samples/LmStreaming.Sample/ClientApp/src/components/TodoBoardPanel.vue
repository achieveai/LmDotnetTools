<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { TodoStatus, type TodoTask } from '@/types/todo';
import {
  TODO_STATUS_GLYPH,
  TODO_STATUS_LABEL,
  artifactFileName,
  countTodoTasks,
  findActiveTaskId,
  flattenTodoTasks,
  latestNote,
  type TodoRow,
} from '@/utils/todoBoard';

/**
 * Right-side WORK BOARD for a conversation (#583, PR 3): summary tiles on top, then one dense line
 * per task. Read-only — nothing here writes back to the agent.
 *
 * Stateless/presentational, exactly like `SubAgentListPanel`: it takes the board as a prop (owned by
 * `ChatLayout`'s `useTodoBoard`) and derives its rows through the same pure helpers the composable
 * uses, so the tile numbers and the rendered rows cannot disagree.
 *
 * `ChatLayout` mounts this ONLY when a board exists, so an empty `tasks` here means a board that was
 * emptied while you watched, not a conversation that never had one.
 *
 * There is deliberately NO loading state. `ChatLayout`'s mount gate is `tasks.length > 0`, and the
 * only way the board loads is through the thread watcher, which resets `tasks` first — so the panel
 * is unmounted for the whole duration of every load it could report. A loading branch here could
 * never render in the composed app, and a prop nothing can set is worse than none: it reads as
 * coverage that does not exist. Render-nothing is the design (see doc §11).
 */
const props = defineProps<{
  tasks: TodoTask[];
}>();

/**
 * A chip click bubbles the workspace-relative path up to ChatLayout, which owns the preview modal
 * — the panel stays stateless and never learns the thread id the preview endpoint needs.
 */
const emit = defineEmits<{ openArtifact: [path: string] }>();

const expanded = ref(true);
const removedExpanded = ref(false);

function toggle(): void {
  expanded.value = !expanded.value;
}

const counts = computed(() => countTodoTasks(props.tasks));
const allRows = computed(() => flattenTodoTasks(props.tasks));
/** Removed rows leave the main list entirely; they live behind their own accordion. */
const liveRows = computed(() => allRows.value.filter((r) => r.status !== TodoStatus.Removed));
const removedRows = computed(() => allRows.value.filter((r) => r.status === TodoStatus.Removed));
const activeTaskId = computed(() => findActiveTaskId(props.tasks));

const progressPercent = computed(() =>
  counts.value.total === 0 ? 0 : Math.round((counts.value.done / counts.value.total) * 100)
);

function glyph(row: TodoRow): string {
  return TODO_STATUS_GLYPH[row.status];
}

function label(row: TodoRow): string {
  return TODO_STATUS_LABEL[row.status];
}

/** Indents by tree depth so the parent/child shape is visible without a second layout pass. */
function indentStyle(row: TodoRow): Record<string, string> {
  return row.depth <= 0 ? {} : { paddingLeft: `${14 + row.depth * 14}px` };
}

/**
 * The note sub-line shows on the ACTIVE row only. Every row carrying its latest note would turn a
 * glanceable board into a wall of text — the whole point of the dense-row shape is that a run's
 * state reads in one look.
 */
function noteFor(row: TodoRow): string | null {
  return row.id === activeTaskId.value ? latestNote(row) : null;
}

// ---------------------------------------------------------------------------------------------
// Autoscroll to the active row, suppressed for 5s after the reader scrolls by hand.
// ---------------------------------------------------------------------------------------------

const MANUAL_SCROLL_SUPPRESSION_MS = 5000;
/** A programmatic scroll fires a `scroll` event too; anything within this window of one is ours. */
const AUTO_SCROLL_ECHO_MS = 100;

const listEl = ref<HTMLElement | null>(null);
let lastManualScrollAt = 0;
let lastAutoScrollAt = 0;

function onListScroll(): void {
  if (Date.now() - lastAutoScrollAt < AUTO_SCROLL_ECHO_MS) return;
  lastManualScrollAt = Date.now();
}

async function scrollActiveIntoView(): Promise<void> {
  const id = activeTaskId.value;
  if (!id) return;
  if (Date.now() - lastManualScrollAt < MANUAL_SCROLL_SUPPRESSION_MS) return;

  await nextTick();
  const list = listEl.value;
  if (!list) return;
  const row = list.querySelector(`[data-testid="todo-row"][data-task-id="${id}"]`);
  // jsdom (and any non-layout host) does not implement scrollIntoView; guard rather than throw.
  if (!row || typeof (row as HTMLElement).scrollIntoView !== 'function') return;
  lastAutoScrollAt = Date.now();
  (row as HTMLElement).scrollIntoView({ block: 'nearest' });
}

watch(activeTaskId, () => {
  void scrollActiveIntoView();
});
</script>

<template>
  <aside class="todo-panel-container" data-testid="todo-panel-container">
    <button
      class="todo-toggle"
      data-testid="todo-panel-toggle"
      :title="expanded ? 'Collapse the work board' : 'Expand the work board'"
      @click="toggle"
    >
      Work ({{ counts.done }}/{{ counts.total }})
      <span class="todo-toggle-caret">{{ expanded ? '▸' : '◂' }}</span>
    </button>

    <div v-if="expanded" class="todo-panel" data-testid="todo-panel">
      <div class="todo-summary">
        <div class="todo-tile" data-testid="todo-tile-completed">
          <span class="todo-tile-count done">{{ counts.done }}</span>
          <span class="todo-tile-label">done</span>
        </div>
        <div class="todo-tile" data-testid="todo-tile-in-progress">
          <span class="todo-tile-count active">{{ counts.inProgress }}</span>
          <span class="todo-tile-label">active</span>
        </div>
        <div class="todo-tile" data-testid="todo-tile-not-started">
          <span class="todo-tile-count pending">{{ counts.pending }}</span>
          <span class="todo-tile-label">todo</span>
        </div>
      </div>

      <div class="todo-progress" data-testid="todo-progress" :data-percent="progressPercent">
        <div class="todo-progress-track">
          <div class="todo-progress-fill" :style="{ width: `${progressPercent}%` }"></div>
        </div>
        <span class="todo-progress-label">{{ counts.done }}/{{ counts.total }}</span>
      </div>

      <!-- Reachable when every row on the board is Removed: the mount gate counts all tasks, this
           list shows only the live ones. -->
      <div v-if="liveRows.length === 0" class="todo-message" data-testid="todo-empty">
        No tasks yet.
      </div>

      <ul
        v-else
        ref="listEl"
        class="todo-list"
        data-testid="todo-list"
        @scroll="onListScroll"
      >
        <li
          v-for="row in liveRows"
          :key="row.id"
          class="todo-row"
          :class="[`status-${row.status}`, { active: row.id === activeTaskId }]"
          data-testid="todo-row"
          :data-task-id="row.id"
          :data-status="row.status"
        >
          <div class="todo-line" :style="indentStyle(row)">
            <span class="todo-glyph" aria-hidden="true">{{ glyph(row) }}</span>
            <span class="todo-id">{{ row.id }}</span>
            <span class="todo-title">{{ row.title }}</span>
            <span class="todo-pill" data-testid="todo-row-pill">{{ label(row) }}</span>
          </div>
          <div v-if="noteFor(row)" class="todo-note" data-testid="todo-row-note">
            {{ noteFor(row) }}
          </div>
          <!-- Artifact chips (#583, PR 5): every row shows its attached files, unlike the note
               sub-line, because a chip is the row's evidence and reference material — hiding it on
               inactive rows would hide exactly the tasks a reviewer wants to open. -->
          <div v-if="row.artifacts.length > 0" class="todo-artifacts">
            <button
              v-for="artifact in row.artifacts"
              :key="artifact"
              class="todo-artifact-chip"
              data-testid="todo-artifact-chip"
              :data-artifact-path="artifact"
              :title="artifact"
              @click="emit('openArtifact', artifact)"
            >
              <span class="todo-artifact-glyph" aria-hidden="true">▪</span>
              <span class="todo-artifact-name" data-testid="todo-artifact-name">{{
                artifactFileName(artifact)
              }}</span>
            </button>
          </div>
        </li>
      </ul>

      <div v-if="removedRows.length > 0" class="todo-removed">
        <button
          class="todo-removed-toggle"
          data-testid="todo-removed-accordion"
          @click="removedExpanded = !removedExpanded"
        >
          {{ removedExpanded ? '▾' : '▸' }} {{ removedRows.length }} removed
        </button>
        <ul v-if="removedExpanded" class="todo-list todo-list-removed" data-testid="todo-removed-list">
          <li
            v-for="row in removedRows"
            :key="row.id"
            class="todo-row status-Removed"
            data-testid="todo-row"
            :data-task-id="row.id"
            :data-status="row.status"
          >
            <div class="todo-line" :style="indentStyle(row)">
              <span class="todo-glyph" aria-hidden="true">{{ glyph(row) }}</span>
              <span class="todo-id">{{ row.id }}</span>
              <span class="todo-title">{{ row.title }}</span>
            </div>
          </li>
        </ul>
      </div>
    </div>
  </aside>
</template>

<style scoped>
/* Palette and metrics copied from SubAgentListPanel so the two right-edge panels read as one rail.
   The app has no theme tokens and no dark mode (verified: no :root custom properties anywhere in
   src/), so colours are literals here exactly as they are in every other component. */
.todo-panel-container {
  display: flex;
  flex-direction: column;
  border-left: 1px solid #e0e0e0;
  background: #f8f9fa;
  min-width: 48px;
}

.todo-toggle {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 12px 14px;
  background: transparent;
  border: none;
  border-bottom: 1px solid #e0e0e0;
  cursor: pointer;
  font-size: 14px;
  font-weight: 500;
  color: #212529;
  white-space: nowrap;
}

.todo-toggle:hover {
  background: #e9ecef;
}

.todo-toggle-caret {
  color: #666;
  font-size: 12px;
}

.todo-panel {
  width: 260px;
  min-width: 260px;
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* Tiles: the three numbers you read first. Big figure, small label under it. */
.todo-summary {
  display: flex;
  padding: 10px 8px 6px;
  gap: 4px;
}

.todo-tile {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 1px;
  padding: 4px 0;
  border-radius: 6px;
  background: #fff;
  border: 1px solid #e9ecef;
}

.todo-tile-count {
  font-size: 18px;
  font-weight: 600;
  line-height: 1.1;
  font-variant-numeric: tabular-nums;
}

.todo-tile-count.done {
  color: #198754;
}

.todo-tile-count.active {
  color: #007bff;
}

.todo-tile-count.pending {
  color: #6c757d;
}

.todo-tile-label {
  font-size: 10px;
  color: #6c757d;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.todo-progress {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 2px 12px 10px;
}

.todo-progress-track {
  flex: 1;
  height: 5px;
  border-radius: 3px;
  background: #e0e0e0;
  overflow: hidden;
}

.todo-progress-fill {
  height: 100%;
  background: #198754;
  transition: width 200ms ease;
}

.todo-progress-label {
  font-size: 11px;
  color: #6c757d;
  font-variant-numeric: tabular-nums;
}

.todo-message {
  padding: 16px;
  text-align: center;
  color: #666;
  font-size: 13px;
}

.todo-list {
  list-style: none;
  padding: 0;
  margin: 0;
  overflow-y: auto;
  flex: 1;
  border-top: 1px solid #e0e0e0;
}

.todo-row {
  border-bottom: 1px solid #eef0f2;
}

.todo-row.active {
  background: #eaf3fd;
  border-left: 3px solid #007bff;
}

.todo-line {
  display: flex;
  align-items: baseline;
  gap: 6px;
  padding: 6px 12px 6px 14px;
}

.todo-row.active .todo-line {
  padding-left: 11px;
}

.todo-glyph {
  font-size: 11px;
  color: #6c757d;
  width: 12px;
  flex: none;
}

.status-InProgress .todo-glyph {
  color: #007bff;
  animation: todo-pulse 1.4s ease-in-out infinite;
}

.status-Completed .todo-glyph {
  color: #198754;
}

@keyframes todo-pulse {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.35;
  }
}

@media (prefers-reduced-motion: reduce) {
  .status-InProgress .todo-glyph {
    animation: none;
  }
}

.todo-id {
  font-size: 11px;
  color: #adb5bd;
  font-variant-numeric: tabular-nums;
  flex: none;
}

.todo-title {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  color: #212529;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.status-Completed .todo-title {
  color: #6c757d;
}

.status-Removed .todo-title {
  color: #adb5bd;
  text-decoration: line-through;
}

.todo-pill {
  flex: none;
  font-size: 10px;
  line-height: 1.5;
  padding: 0 6px;
  border-radius: 8px;
  background: #e9ecef;
  color: #6c757d;
  text-transform: lowercase;
}

.status-InProgress .todo-pill {
  background: #d4e5f7;
  color: #0b5ed7;
}

.status-Completed .todo-pill {
  background: #d9f0e3;
  color: #146c43;
}

/* Artifact chips: small filename pills under the row line, full path on the tooltip. */
.todo-artifacts {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  padding: 0 12px 6px 32px;
}

.todo-artifact-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  padding: 1px 8px;
  border: 1px solid #d8dde3;
  border-radius: 8px;
  background: #fff;
  color: #495057;
  font-size: 10px;
  line-height: 1.6;
  cursor: pointer;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.todo-artifact-chip:hover {
  background: #e9ecef;
  border-color: #c4ccd4;
}

.todo-artifact-glyph {
  color: #6c757d;
  font-size: 8px;
}

/* One line, clipped. A note that needs more than a line belongs in the transcript, not here. */
.todo-note {
  padding: 0 12px 6px 32px;
  font-size: 11px;
  color: #6c757d;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.todo-removed {
  border-top: 1px solid #e0e0e0;
  flex: none;
  max-height: 40%;
  display: flex;
  flex-direction: column;
}

.todo-removed-toggle {
  width: 100%;
  text-align: left;
  padding: 8px 14px;
  background: transparent;
  border: none;
  cursor: pointer;
  font-size: 11px;
  color: #6c757d;
}

.todo-removed-toggle:hover {
  background: #e9ecef;
}

.todo-list-removed {
  border-top: none;
}
</style>
