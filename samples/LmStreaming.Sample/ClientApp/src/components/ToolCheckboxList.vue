<script setup lang="ts">
import { computed, ref } from 'vue';
import type { ToolDefinition } from '@/types/chatMode';
import { groupTools, toolId, wildcardId, type ToolGroupView } from '@/utils/modeToolSelection';

/**
 * A grouped, searchable checkbox list over the whole tool catalog.
 *
 * `modelValue` is a FLAT list of selected tool ids across every group. It is deliberately not the
 * `string[] | null` shape a mode persists: the three persisted fields disagree about what null
 * means, so the translation lives in `utils/modeToolSelection` and this component only ever deals
 * in "these ids are ticked".
 *
 * `variant` exists only so the two instances `ModeEditor` mounts stop looking identical. The editor
 * shows an "enabled tools" picker and a "required sub-agent tools" picker one after the other, and
 * with no chrome of their own the second was routinely mistaken for the first. It changes accent,
 * icon and placeholder wording — never behaviour, and never the ids the suites select on.
 */
const props = withDefaults(
  defineProps<{
    tools: ToolDefinition[];
    modelValue: string[];
    disabled?: boolean;
    variant?: 'enabled' | 'required';
  }>(),
  { variant: 'enabled' }
);

const emit = defineEmits<{
  'update:modelValue': [value: string[]];
}>();

const searchQuery = ref('');
const collapsed = ref<Record<string, boolean>>({});

const selected = computed(() => new Set(props.modelValue));

const groups = computed(() => groupTools(props.tools));

const isRequiredVariant = computed(() => props.variant === 'required');

/** Groups filtered by the search box; a group with no surviving rows drops out entirely. */
const visibleGroups = computed<ToolGroupView[]>(() => {
  const query = searchQuery.value.trim().toLowerCase();
  if (!query) return groups.value;

  return groups.value
    .map((group) => ({
      ...group,
      wildcard: group.wildcard && matches(group.wildcard, query) ? group.wildcard : undefined,
      tools: group.tools.filter((tool) => matches(tool, query)),
    }))
    .filter((group) => group.tools.length > 0 || group.wildcard);
});

function matches(tool: ToolDefinition, query: string): boolean {
  return (
    tool.name.toLowerCase().includes(query) ||
    toolId(tool).toLowerCase().includes(query) ||
    (tool.description?.toLowerCase().includes(query) ?? false)
  );
}

const totalToolCount = computed(() => props.tools.filter((t) => !t.isWildcard).length);

/** Ticked rows, excluding wildcards, so the summary counts tools rather than tokens. */
const selectedToolCount = computed(
  () => props.tools.filter((t) => !t.isWildcard && isCovered(t)).length
);

/** Groups taken wholesale via `group:*`; their contents are covered even when not individually ticked. */
const wildcardGroups = computed(() =>
  groups.value.filter((g) => g.wildcard && selected.value.has(toolId(g.wildcard)))
);

const sandboxSelected = computed(() => props.tools.some((t) => t.requiresSandbox && isCovered(t)));

/** Drives the meter under the summary. 0 for an empty catalog, so the bar never renders NaN. */
const selectedPercent = computed(() =>
  totalToolCount.value === 0 ? 0 : Math.round((selectedToolCount.value / totalToolCount.value) * 100)
);

function isSelected(tool: ToolDefinition): boolean {
  return selected.value.has(toolId(tool));
}

/** Whether the mode ends up with this tool: ticked directly, or swept in by its group's wildcard. */
function isCovered(tool: ToolDefinition): boolean {
  if (isSelected(tool)) return true;
  const group = toolGroup(tool);
  return !tool.isWildcard && selected.value.has(wildcardId(group));
}

function toolGroup(tool: ToolDefinition): string {
  return tool.group ?? 'sample';
}

/**
 * A row inside a wildcard-selected group is covered whether or not it is individually ticked, so
 * it renders checked and disabled rather than pretending the user can turn it off.
 */
function isCoveredByWildcard(group: ToolGroupView, tool: ToolDefinition): boolean {
  return !tool.isWildcard && !!group.wildcard && selected.value.has(toolId(group.wildcard));
}

function groupSelectedCount(group: ToolGroupView): number {
  if (group.wildcard && selected.value.has(toolId(group.wildcard))) return group.tools.length;
  return group.tools.filter((tool) => selected.value.has(toolId(tool))).length;
}

function isGroupFullySelected(group: ToolGroupView): boolean {
  return group.tools.length > 0 && groupSelectedCount(group) === group.tools.length;
}

function isGroupPartiallySelected(group: ToolGroupView): boolean {
  const count = groupSelectedCount(group);
  return count > 0 && count < group.tools.length;
}

function emitSelection(next: Set<string>): void {
  // Emitted in catalog order rather than click order, so two identical selections serialize
  // identically and a mode's stored list does not churn on every re-save.
  emit(
    'update:modelValue',
    props.tools.map(toolId).filter((id) => next.has(id))
  );
}

function toggleTool(group: ToolGroupView, tool: ToolDefinition): void {
  if (props.disabled || isCoveredByWildcard(group, tool)) return;

  const next = new Set(selected.value);
  const id = toolId(tool);
  if (next.has(id)) {
    next.delete(id);
  } else {
    next.add(id);
  }
  emitSelection(next);
}

/**
 * The group header checkbox. For a qualified group it writes the `group:*` wildcard rather than
 * every current tool id, because only the wildcard keeps covering tools a marketplace plugin adds
 * after this catalog was fetched.
 */
function toggleGroup(group: ToolGroupView): void {
  if (props.disabled) return;

  const next = new Set(selected.value);
  const turningOn = !isGroupFullySelected(group);

  for (const tool of group.tools) next.delete(toolId(tool));
  if (group.wildcard) next.delete(toolId(group.wildcard));

  if (turningOn) {
    if (group.wildcard) {
      next.add(wildcardId(group.key));
    } else {
      for (const tool of group.tools) next.add(toolId(tool));
    }
  }

  emitSelection(next);
}

function toggleCollapsed(group: ToolGroupView): void {
  collapsed.value = { ...collapsed.value, [group.key]: !collapsed.value[group.key] };
}

function isCollapsed(group: ToolGroupView): boolean {
  // Search results always render expanded: a hidden match reads as "no match".
  if (searchQuery.value.trim()) return false;
  return !!collapsed.value[group.key];
}

function selectAll(): void {
  if (props.disabled) return;
  const next = new Set<string>();
  for (const group of groups.value) {
    if (group.wildcard) {
      next.add(toolId(group.wildcard));
    } else {
      for (const tool of group.tools) next.add(toolId(tool));
    }
  }
  emitSelection(next);
}

function deselectAll(): void {
  if (props.disabled) return;
  emitSelection(new Set());
}
</script>

<template>
  <div class="tool-checkbox-list" :class="`variant-${variant}`" data-testid="tool-checkbox-list">
    <!--
      Summary, meter and the sandbox consequence sit ABOVE the scrolling group list. They used to
      trail it, which put them several hundred pixels into the middle of the form, where the two
      things a reader most needs — how much surface is on, and that workspace tools cost a sandbox
      session per conversation — were the easiest things to miss.
    -->
    <div class="list-status">
      <div class="status-line">
        <span class="status-icon" aria-hidden="true">
          <svg v-if="isRequiredVariant" viewBox="0 0 20 20" focusable="false">
            <path
              d="M10 2.6 3.4 5.3v4.3c0 3.6 2.7 6.5 6.6 7.8 3.9-1.3 6.6-4.2 6.6-7.8V5.3Z"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
              stroke-linejoin="round"
            />
            <path
              d="m7.3 10 2 2 3.5-3.8"
              fill="none"
              stroke="currentColor"
              stroke-width="1.6"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
          <svg v-else viewBox="0 0 20 20" focusable="false">
            <path
              d="M12.6 3.3a3.8 3.8 0 0 0-4.9 4.9l-4 4a1.4 1.4 0 0 0 0 2l2.1 2.1a1.4 1.4 0 0 0 2 0l4-4a3.8 3.8 0 0 0 4.9-4.9l-2.3 2.3-2-.1-.1-2Z"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
              stroke-linejoin="round"
            />
          </svg>
        </span>
        <span class="selection-summary" data-testid="tools-selection-summary">
          <span v-if="selectedToolCount === 0">No tools enabled</span>
          <span v-else>{{ selectedToolCount }} of {{ totalToolCount }} tools enabled</span>
          <span v-if="wildcardGroups.length > 0" class="summary-note">
            &middot; all current and future tools in
            {{ wildcardGroups.map((g) => g.label).join(', ') }}
          </span>
        </span>
        <span class="status-spacer"></span>
        <button
          type="button"
          class="action-btn"
          data-testid="tools-select-all"
          :disabled="disabled"
          @click="selectAll"
        >
          Select all
        </button>
        <button
          type="button"
          class="action-btn"
          data-testid="tools-deselect-all"
          :disabled="disabled || modelValue.length === 0"
          @click="deselectAll"
        >
          Clear
        </button>
      </div>

      <div class="status-meter" role="presentation" :style="{ '--fill': `${selectedPercent}%` }">
        <span class="status-meter-fill"></span>
      </div>
    </div>

    <p v-if="sandboxSelected" class="sandbox-note" data-testid="tools-sandbox-note">
      <span class="note-icon" aria-hidden="true">
        <svg viewBox="0 0 20 20" focusable="false">
          <path
            d="M10 3.3 2.9 16h14.2Z"
            fill="none"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linejoin="round"
          />
          <path
            d="M10 8.2v3.3"
            fill="none"
            stroke="currentColor"
            stroke-width="1.6"
            stroke-linecap="round"
          />
          <circle cx="10" cy="13.7" r="0.9" fill="currentColor" />
        </svg>
      </span>
      <span>
        Workspace tools are selected, so every conversation in this mode starts its own sandbox
        session.
      </span>
    </p>

    <div class="search-box">
      <span class="search-icon" aria-hidden="true">
        <svg viewBox="0 0 20 20" focusable="false">
          <circle cx="9" cy="9" r="5.2" fill="none" stroke="currentColor" stroke-width="1.6" />
          <path
            d="m12.9 12.9 3.6 3.6"
            fill="none"
            stroke="currentColor"
            stroke-width="1.6"
            stroke-linecap="round"
          />
        </svg>
      </span>
      <input
        v-model="searchQuery"
        type="search"
        :placeholder="isRequiredVariant ? 'Search required tools...' : 'Search tools...'"
        :aria-label="isRequiredVariant ? 'Search required sub-agent tools' : 'Search enabled tools'"
        class="search-input"
        data-testid="tools-search"
      />
    </div>

    <div v-if="tools.length === 0" class="no-tools">No tools available</div>

    <div v-else-if="visibleGroups.length === 0" class="no-tools">
      No tools match "{{ searchQuery }}"
    </div>

    <div v-else class="group-list">
      <section
        v-for="group in visibleGroups"
        :key="group.key"
        class="tool-group"
        :data-testid="`tool-group-${group.key}`"
      >
        <header class="group-header">
          <label class="group-label" :class="{ disabled }">
            <input
              type="checkbox"
              :data-testid="`tool-group-toggle-${group.key}`"
              :checked="isGroupFullySelected(group)"
              :indeterminate.prop="isGroupPartiallySelected(group)"
              :disabled="disabled"
              @change="toggleGroup(group)"
            />
            <span class="group-name">{{ group.label }}</span>
          </label>
          <button
            type="button"
            class="collapse-btn"
            :data-testid="`tool-group-collapse-${group.key}`"
            :aria-expanded="!isCollapsed(group)"
            :aria-label="`${isCollapsed(group) ? 'Expand' : 'Collapse'} ${group.label}`"
            @click="toggleCollapsed(group)"
          >
            <span class="group-count">{{ groupSelectedCount(group) }}/{{ group.tools.length }}</span>
            <span class="chevron" aria-hidden="true">{{
              isCollapsed(group) ? '&#9656;' : '&#9662;'
            }}</span>
          </button>
        </header>

        <p
          v-if="group.catalogWarning"
          class="group-warning"
          :data-testid="`tool-group-warning-${group.key}`"
        >
          {{ group.catalogWarning }}
        </p>

        <ul v-show="!isCollapsed(group)" class="tool-list">
          <li v-if="group.wildcard" class="tool-item wildcard">
            <label class="tool-label" :class="{ disabled }">
              <input
                type="checkbox"
                :data-testid="`tool-${toolId(group.wildcard)}`"
                :checked="isSelected(group.wildcard)"
                :disabled="disabled"
                @change="toggleTool(group, group.wildcard)"
              />
              <div class="tool-info">
                <span class="tool-name">{{ group.wildcard.name }}</span>
                <span v-if="group.wildcard.description" class="tool-description">
                  {{ group.wildcard.description }}
                </span>
              </div>
            </label>
          </li>
          <li v-for="tool in group.tools" :key="toolId(tool)" class="tool-item">
            <label
              class="tool-label"
              :class="{ disabled: disabled || isCoveredByWildcard(group, tool) }"
            >
              <input
                type="checkbox"
                :data-testid="`tool-${toolId(tool)}`"
                :checked="isSelected(tool) || isCoveredByWildcard(group, tool)"
                :disabled="disabled || isCoveredByWildcard(group, tool)"
                @change="toggleTool(group, tool)"
              />
              <div class="tool-info">
                <span class="tool-name">{{ tool.name }}</span>
                <span v-if="tool.description" class="tool-description">
                  {{ tool.description }}
                </span>
              </div>
            </label>
          </li>
        </ul>
      </section>
    </div>
  </div>
</template>

<style scoped>
.tool-checkbox-list {
  /* Same palette the newer shell components use (HeaderActionsMenu, ConversationInspector). */
  --tcl-border: #d6dbe1;
  --tcl-border-soft: #e2e6eb;
  --tcl-surface: #f7f8fa;
  --tcl-text: #303944;
  --tcl-muted: #64748b;
  --tcl-focus: #2d6cdf;
  --tcl-accent: #2d6cdf;
  --tcl-accent-soft: #eef3f9;

  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 10px;
  border: 1px solid var(--tcl-border);
  border-left: 3px solid var(--tcl-accent);
  border-radius: 8px;
  background: #fff;
}

/*
 * The whole point of the variant: a reader scanning the form should be able to tell the two pickers
 * apart without reading either label.
 */
.tool-checkbox-list.variant-required {
  --tcl-accent: #b5760d;
  --tcl-accent-soft: #fdf5e6;

  background: #fffdf8;
}

.list-status {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.status-line {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.status-icon {
  display: inline-flex;
  color: var(--tcl-accent);
}

.status-icon svg {
  width: 17px;
  height: 17px;
}

.selection-summary {
  font-size: 12.5px;
  color: var(--tcl-text);
  font-variant-numeric: tabular-nums;
}

.summary-note {
  color: var(--tcl-accent);
}

.status-spacer {
  flex: 1 1 auto;
}

.status-meter {
  height: 4px;
  border-radius: 999px;
  background: var(--tcl-border-soft);
  overflow: hidden;
}

.status-meter-fill {
  display: block;
  width: var(--fill, 0%);
  height: 100%;
  border-radius: inherit;
  background: var(--tcl-accent);
  transition: width 0.2s ease;
}

.action-btn {
  padding: 5px 10px;
  background: #fff;
  border: 1px solid var(--tcl-border);
  border-radius: 6px;
  color: var(--tcl-text);
  font-size: 12px;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}

.action-btn:hover:not(:disabled) {
  background: var(--tcl-accent-soft);
  border-color: var(--tcl-accent);
}

.action-btn:focus-visible {
  outline: 2px solid var(--tcl-focus);
  outline-offset: 2px;
}

.action-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.search-box {
  position: relative;
  display: flex;
  align-items: center;
}

.search-icon {
  position: absolute;
  left: 9px;
  display: inline-flex;
  color: var(--tcl-muted);
  pointer-events: none;
}

.search-icon svg {
  width: 15px;
  height: 15px;
}

.search-input {
  width: 100%;
  padding: 7px 10px 7px 30px;
  border: 1px solid var(--tcl-border);
  border-radius: 6px;
  font-size: 13px;
  font-family: inherit;
  color: var(--tcl-text);
  background: #fff;
}

.search-input:focus {
  outline: none;
  border-color: var(--tcl-focus);
  box-shadow: 0 0 0 3px rgb(45 108 223 / 18%);
}

.no-tools {
  padding: 16px;
  text-align: center;
  color: var(--tcl-muted);
  font-size: 13px;
  background: var(--tcl-surface);
  border-radius: 6px;
}

.group-list {
  max-height: 300px;
  overflow-y: auto;
  border: 1px solid var(--tcl-border-soft);
  border-radius: 6px;
  background: #fff;
}

.tool-group + .tool-group {
  border-top: 1px solid var(--tcl-border-soft);
}

.group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 7px 10px;
  background: var(--tcl-surface);
  position: sticky;
  top: 0;
  z-index: 1;
}

.group-label {
  display: flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
  min-width: 0;
}

.group-label.disabled {
  cursor: not-allowed;
  opacity: 0.7;
}

.group-label input[type='checkbox'] {
  accent-color: var(--tcl-accent);
}

.group-name {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--tcl-text);
}

.collapse-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  background: none;
  border: 0;
  border-radius: 5px;
  cursor: pointer;
  color: var(--tcl-muted);
  font-size: 12px;
  padding: 2px 5px;
}

.collapse-btn:hover {
  background: var(--tcl-accent-soft);
  color: var(--tcl-text);
}

.collapse-btn:focus-visible {
  outline: 2px solid var(--tcl-focus);
  outline-offset: 1px;
}

.group-count {
  font-variant-numeric: tabular-nums;
}

.group-warning {
  margin: 0;
  padding: 7px 10px;
  font-size: 12px;
  line-height: 1.4;
  color: #8a6d3b;
  background: #fdf5e6;
}

.tool-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.tool-item {
  border-top: 1px solid #eef0f3;
}

.tool-item.wildcard {
  background: var(--tcl-accent-soft);
}

.tool-label {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 8px 10px;
  cursor: pointer;
  transition: background 0.15s;
}

.tool-label:hover:not(.disabled) {
  background: var(--tcl-surface);
}

.tool-label:focus-within {
  background: var(--tcl-accent-soft);
}

.tool-label.disabled {
  cursor: not-allowed;
  opacity: 0.7;
}

.tool-label input[type='checkbox'] {
  margin-top: 2px;
  flex-shrink: 0;
  accent-color: var(--tcl-accent);
}

.tool-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.tool-name {
  font-weight: 500;
  font-size: 13.5px;
  color: var(--tcl-text);
}

.tool-description {
  font-size: 12px;
  color: var(--tcl-muted);
  line-height: 1.4;
}

.sandbox-note {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: #8a6d3b;
  background: #fdf5e6;
  border: 1px solid #f0e0bd;
  border-radius: 6px;
  padding: 8px 10px;
}

.note-icon {
  display: inline-flex;
  flex: none;
  margin-top: 1px;
}

.note-icon svg {
  width: 15px;
  height: 15px;
}
</style>
