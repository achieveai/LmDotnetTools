<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  ref,
  watch,
} from "vue";
import type { SubAgentSummary } from "@/api/subAgentsApi";
import type { TodoTask } from "@/types/todo";
import { countTodoTasks } from "@/utils/todoBoard";
import TodoBoardPanel from "./TodoBoardPanel.vue";
import SubAgentListPanel from "./SubAgentListPanel.vue";
import FileBrowser from "./FileBrowser.vue";
import PanelSplitter from "./PanelSplitter.vue";

type InspectorSection = "work" | "files" | "agents";
type PreviewTab = { id: string; label: string; path: string };
const props = withDefaults(
  defineProps<{
    open: boolean;
    activeSection?: InspectorSection;
    tasks: TodoTask[];
    hasWork: boolean;
    children: SubAgentSummary[];
    activeConversationTabId: string;
    externalCloseControlId?: string;
    /**
     * Thread whose workspace the Files section lists. This is the STARTED-conversation id
     * (ChatLayout's `subAgentParentThreadId`), the same one the embedded preview is gated on — a file
     * opened from a thread the preview refuses to mount for could never be shown.
     */
    filesThreadId?: string | null;
    desktopWidth?: number;
    previewTabs?: PreviewTab[];
    activePreviewId?: string | null;
    previewHeight?: number;
    previewMinHeight?: number;
  previewMaxHeight?: number;
  previewDefaultHeight?: number;
    expanded?: boolean;
  }>(),
  {
    activeSection: "work",
    filesThreadId: null,
    desktopWidth: 320,
    previewTabs: () => [],
    activePreviewId: null,
    previewHeight: 420,
    previewMinHeight: 180,
  previewMaxHeight: 700,
  previewDefaultHeight: 420,
    expanded: false,
  },
);
const emit = defineEmits<{
  close: [];
  selectSection: [section: InspectorSection];
  openArtifact: [path: string];
  selectAgent: [agentId: string, closeDrawer: boolean];
  selectPreview: [id: string];
  closePreview: [id: string];
  "update:previewHeight": [height: number];
}>();

const rootEl = ref<HTMLElement | null>(null);
const workButton = ref<HTMLButtonElement | null>(null);
const overlay = ref(false);
const shortViewport = ref(false);
const viewportWidth = ref(typeof window === "undefined" ? 1200 : window.innerWidth);
const workOpen = ref(true);
const agentsOpen = ref(true);
const filesButton = ref<HTMLButtonElement | null>(null);
// Files starts COLLAPSED and mounts lazily: the inspector auto-opens in developer view for every
// conversation, so an eagerly mounted browser would GET /files for conversations nobody looks at.
const filesOpen = ref(false);
// Once mounted it STAYS mounted behind v-show, so collapsing the section keeps the path, the filter,
// the scroll position and any in-flight upload rather than aborting them.
const filesMounted = ref(false);
const resizing = ref(false);
const workCounts = computed(() => countTodoTasks(props.tasks));
const hasPreview = computed(() => props.previewTabs.length > 0);
// F-002 (#784): the tabs share one panel, so its accessible name must track whichever tab is active
// rather than always pointing at the first — otherwise a screen reader announces the wrong file.
const activePreviewIndex = computed(() =>
  props.previewTabs.findIndex((tab) => tab.id === props.activePreviewId),
);
const activePreviewTabId = computed(() =>
  activePreviewIndex.value === -1 ? undefined : `preview-tab-${activePreviewIndex.value}`,
);
const inspectorStyle = computed(() => ({
  "--inspector-width": `${props.desktopWidth}px`,
  "--preview-height": `${props.previewHeight}px`,
  width:
    overlay.value && props.expanded && viewportWidth.value > 768
      ? `${props.desktopWidth}px`
      : undefined,
}));
function syncOverlay(): void {
  const wasOverlay = overlay.value;
  viewportWidth.value = window.innerWidth;
  overlay.value = window.innerWidth <= 1100;
  shortViewport.value = window.innerHeight <= 620;
  if (
    !wasOverlay &&
    overlay.value &&
    props.open &&
    !rootEl.value?.contains(document.activeElement)
  )
    void nextTick(() => workButton.value?.focus());
}
function toggleSection(section: InspectorSection): void {
  if (section === "work") workOpen.value = !workOpen.value;
  else if (section === "files") setFilesOpen(!filesOpen.value);
  else agentsOpen.value = !agentsOpen.value;
  emit("selectSection", section);
}
function setFilesOpen(open: boolean): void {
  filesOpen.value = open;
  if (open) filesMounted.value = true;
}
/**
 * Opens `section` and moves focus to its disclosure. The "More > Files" header item calls this
 * through the parent so choosing Files lands the user on the Files section rather than on a modal.
 */
function revealSection(section: InspectorSection): void {
  if (section === "work") workOpen.value = true;
  else if (section === "files") setFilesOpen(true);
  else agentsOpen.value = true;
  void nextTick(() =>
    (section === "files" ? filesButton.value : rootEl.value?.querySelector<HTMLButtonElement>(`#inspector-tab-${section}`))?.focus(),
  );
}
defineExpose({ revealSection });
function isNestedDialog(event: KeyboardEvent): boolean {
  const dialog = (event.target as Element | null)?.closest?.('[role="dialog"]');
  return !!dialog && dialog !== rootEl.value;
}
function focusable(): HTMLElement[] {
  return rootEl.value
    ? Array.from(
        rootEl.value.querySelectorAll<HTMLElement>(
          'button:not([disabled]), [href], input:not([disabled]), [tabindex]:not([tabindex="-1"])',
        ),
      ).filter((el) => el.tabIndex >= 0 && !el.closest("[hidden], [inert]"))
    : [];
}
function handleKeys(event: KeyboardEvent): void {
  if (isNestedDialog(event)) return;
  if (event.key === "Escape") {
    if (isNestedDialog(event)) return;
    event.preventDefault();
    emit("close");
    return;
  }
  if (!overlay.value || event.key !== "Tab") return;
  const items = focusable(),
    first = items[0],
    last = items[items.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last?.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first?.focus();
  }
}
function onPreviewTabKeydown(event: KeyboardEvent, index: number): void {
  if (
    !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key) ||
    !props.previewTabs.length
  )
    return;
  event.preventDefault();
  let next =
    event.key === "Home"
      ? 0
      : event.key === "End"
        ? props.previewTabs.length - 1
        : (index +
            (event.key === "ArrowRight" ? 1 : -1) +
            props.previewTabs.length) %
          props.previewTabs.length;
  emit("selectPreview", props.previewTabs[next].id);
  void nextTick(() =>
    rootEl.value
      ?.querySelectorAll<HTMLElement>("[data-preview-id]")
      [next]?.focus(),
  );
}
function onRootKeydown(event: KeyboardEvent): void {
  if (!props.externalCloseControlId || !overlay.value) handleKeys(event);
}
function onDocumentKeydown(event: KeyboardEvent): void {
  if (
    !props.open ||
    !overlay.value ||
    !props.externalCloseControlId ||
    isNestedDialog(event)
  )
    return;
  const external = document.getElementById(props.externalCloseControlId);
  if (
    !external ||
    (!rootEl.value?.contains(event.target as Node) && event.target !== external)
  )
    return;
  if (event.key === "Escape") {
    event.preventDefault();
    emit("close");
    return;
  }
  if (event.key !== "Tab") return;
  const items = focusable(),
    first = items[0],
    last = items[items.length - 1];
  if (event.target === external) {
    event.preventDefault();
    (event.shiftKey ? last : first)?.focus();
  } else if (
    (event.target === first && event.shiftKey) ||
    (event.target === last && !event.shiftKey)
  ) {
    event.preventDefault();
    external.focus();
  }
}
watch(
  () => props.open,
  (open) => {
    if (open) void nextTick(() => workButton.value?.focus());
  },
);
onMounted(() => {
  syncOverlay();
  window.addEventListener("resize", syncOverlay);
  document.addEventListener("keydown", onDocumentKeydown);
});
onBeforeUnmount(() => {
  window.removeEventListener("resize", syncOverlay);
  document.removeEventListener("keydown", onDocumentKeydown);
});
</script>

<template>
  <template v-if="open">
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
      :class="['conversation-inspector', { overlay, expanded, resizing, 'has-preview': hasPreview }]"
      :style="inspectorStyle"
      data-testid="conversation-inspector"
      :role="overlay ? 'dialog' : undefined"
      :aria-modal="overlay ? 'true' : undefined"
      aria-labelledby="conversation-inspector-title"
      @keydown="onRootKeydown"
    >
      <header class="inspector-header">
        <h2 id="conversation-inspector-title">Work &amp; agents</h2>
        <button
          v-if="!externalCloseControlId"
          class="inspector-close"
          aria-label="Close Work and agents"
          title="Close Work and agents"
          @click="emit('close')"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true">
            <rect x="2.5" y="3" width="15" height="14" rx="2" />
            <path d="M12.5 3v14" />
          </svg>
        </button>
      </header>
      <section
        v-show="hasPreview"
        class="preview-region"
        data-testid="workspace-preview-region"
      >
        <div class="preview-tabs" role="tablist" aria-label="Open files">
          <div
            v-for="(tab, index) in previewTabs"
            :key="tab.id"
            class="preview-tab-wrap"
          >
            <button
              :id="`preview-tab-${index}`"
              role="tab"
              :aria-selected="tab.id === activePreviewId"
              :aria-controls="'workspace-preview-panel'"
              :tabindex="tab.id === activePreviewId ? 0 : -1"
              :title="tab.path"
              :data-preview-id="tab.id"
              @click="emit('selectPreview', tab.id)"
              @keydown="onPreviewTabKeydown($event, index)"
            >
              {{ tab.label }}
            </button>
            <button
              class="preview-tab-close"
              :aria-label="`Close ${tab.label}`"
              @click.stop="emit('closePreview', tab.id)"
            >
              ×
            </button>
          </div>
        </div>
        <div
          id="workspace-preview-panel"
          class="preview-content"
          role="tabpanel"
          :aria-labelledby="activePreviewTabId"
        >
          <slot name="preview" />
        </div>
      </section>
      <PanelSplitter
        v-if="hasPreview && !expanded && !overlay && !shortViewport"
        data-testid="workspace-vertical-splitter"
        label="Resize file preview"
        orientation="horizontal"
        controls="workspace-preview-panel"
        persist-key="lmstreaming.previewHeight"
        :value="previewHeight"
        :min="previewMinHeight"
        :max="previewMaxHeight"
        :default-value="previewDefaultHeight"
        @update:value="emit('update:previewHeight', $event)"
        @dragging="resizing = $event"
      />
      <div
        id="workspace-monitoring-region"
        v-show="!expanded"
        class="monitoring-region"
        data-testid="workspace-monitoring-region"
        :inert="expanded || undefined"
      >
        <section class="inspector-section">
          <h3>
            <button
              id="inspector-tab-work"
              ref="workButton"
              :aria-expanded="workOpen"
              aria-controls="inspector-panel-work"
              @click="toggleSection('work')"
            >
              Work
              <span v-if="hasWork"
                >{{ workCounts.done }}/{{ workCounts.total }}</span
              >
            </button>
          </h3>
          <div
            id="inspector-panel-work"
            v-show="workOpen"
            class="inspector-content"
            :inert="!workOpen || undefined"
          >
            <TodoBoardPanel
              v-if="hasWork"
              embedded
              :tasks="tasks"
              @open-artifact="emit('openArtifact', $event)"
            />
            <p v-else class="inspector-empty">No work yet.</p>
          </div>
        </section>
        <section class="inspector-section" data-testid="workspace-files-section">
          <h3>
            <button
              id="inspector-tab-files"
              ref="filesButton"
              :aria-expanded="filesOpen"
              aria-controls="inspector-panel-files"
              @click="toggleSection('files')"
            >
              Files
            </button>
          </h3>
          <div
            id="inspector-panel-files"
            v-show="filesOpen"
            class="inspector-content inspector-content-files"
            :inert="!filesOpen || undefined"
          >
            <FileBrowser
              v-if="filesMounted"
              embedded
              :thread-id="filesThreadId"
              @open-file="emit('openArtifact', $event)"
            />
          </div>
        </section>
        <section class="inspector-section">
          <h3>
            <button
              id="inspector-tab-agents"
              :aria-expanded="agentsOpen"
              aria-controls="inspector-panel-agents"
              @click="toggleSection('agents')"
            >
              Agents <span>{{ children.length }}</span>
            </button>
          </h3>
          <div
            id="inspector-panel-agents"
            v-show="agentsOpen"
            class="inspector-content"
            :inert="!agentsOpen || undefined"
          >
            <SubAgentListPanel
              embedded
              :children="children"
              :active-tab-id="activeConversationTabId"
              @select="emit('selectAgent', $event, overlay)"
            />
          </div>
        </section>
      </div>
    </aside>
  </template>
</template>

<style scoped>
.conversation-inspector {
  box-sizing: border-box;
  position: relative;
  width: var(--inspector-width);
  min-width: var(--inspector-width);
  height: 100%;
  min-height: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  background: #f7f8fa;
  border-left: 1px solid #e2e6eb;
  z-index: 20;
}
.conversation-inspector.overlay {
  position: fixed;
  inset: 0 0 0 auto;
  width: min(92vw, 340px);
  min-width: 0;
  z-index: 101;
  box-shadow: -8px 0 24px #0003;
}

.conversation-inspector.overlay.has-preview {
  width: min(92vw, 640px);
}
.inspector-backdrop {
  position: fixed;
  inset: 0;
  background: #0004;
  z-index: 100;
}
.inspector-header {
  display: flex;
  min-height: 54px;
  align-items: center;
  justify-content: space-between;
  padding: 8px 14px;
}
.inspector-header h2,
.inspector-section h3 {
  margin: 0;
  font-size: 16px;
}
.inspector-close {
  display: inline-flex;
  width: 34px;
  height: 34px;
  align-items: center;
  justify-content: center;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  background: #fff;
  color: #394553;
}
.inspector-close svg {
  width: 18px;
  height: 18px;
  fill: none;
  stroke: currentColor;
}
.preview-region {
  height: var(--preview-height);
  min-height: 0;
  display: flex;
  flex-direction: column;
  background: #fff;
}
.expanded .preview-region {
  height: auto;
  flex: 1;
}
.preview-tabs {
  display: flex;
  overflow-x: auto;
  min-height: 38px;
  border-block: 1px solid #e2e6eb;
  background: #f7f8fa;
}
.preview-tab-wrap {
  display: flex;
  align-items: center;
  border-right: 1px solid #e2e6eb;
}
.preview-tabs button {
  border: 0;
  background: transparent;
  color: #475569;
  padding: 9px 5px 9px 10px;
  white-space: nowrap;
}
.preview-tabs [role="tab"][aria-selected="true"] {
  color: #1d4ed8;
  background: #fff;
}
.preview-tab-close {
  font-size: 16px;
  padding-inline: 5px 9px !important;
}
.preview-content {
  flex: 1;
  min-height: 0;
  overflow: auto;
}
.monitoring-region {
  flex: 1;
  min-height: 120px;
  overflow: auto;
}
.inspector-section {
  border-bottom: 1px solid #e2e6eb;
}
.inspector-section h3 button {
  width: 100%;
  display: flex;
  gap: 8px;
  padding: 11px 14px;
  border: 0;
  background: #f7f8fa;
  color: #334155;
  font: inherit;
  text-align: left;
}
.inspector-section h3 button span {
  margin-left: auto;
}
.inspector-section h3 button:before {
  content: "▾";
  color: #64748b;
}
.inspector-section h3 button[aria-expanded="false"]:before {
  content: "▸";
}
.inspector-content {
  padding: 8px 10px 12px;
}
/* The browser supplies its own gutter so its list can use the panel's full width. */
.inspector-content-files {
  padding: 0 0 8px;
}
.inspector-empty {
  padding: 16px;
  color: #64748b;
  font-size: 13px;
}
@media (max-height: 620px) {
  .conversation-inspector.has-preview {
    overflow-y: auto;
  }
  .conversation-inspector.has-preview .preview-region {
    height: auto;
    min-height: 260px;
  }
  .conversation-inspector.has-preview .monitoring-region {
    flex: none;
    overflow: visible;
  }
}
</style>
