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
 */
const props = defineProps<{
  tools: ToolDefinition[];
  modelValue: string[];
  disabled?: boolean;
}>();

const emit = defineEmits<{
  'update:modelValue': [value: string[]];
}>();

const searchQuery = ref('');
const collapsed = ref<Record<string, boolean>>({});

const selected = computed(() => new Set(props.modelValue));

const groups = computed(() => groupTools(props.tools));

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
  <div class="tool-checkbox-list" data-testid="tool-checkbox-list">
    <div class="tool-actions">
      <button
        type="button"
        class="action-btn"
        data-testid="tools-select-all"
        :disabled="disabled"
        @click="selectAll"
      >
        Select All
      </button>
      <button
        type="button"
        class="action-btn"
        data-testid="tools-deselect-all"
        :disabled="disabled || modelValue.length === 0"
        @click="deselectAll"
      >
        Deselect All
      </button>
    </div>

    <div class="search-box">
      <input
        v-model="searchQuery"
        type="text"
        placeholder="Search tools..."
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
            @click="toggleCollapsed(group)"
          >
            <span class="group-count">{{ groupSelectedCount(group) }}/{{ group.tools.length }}</span>
            <span class="chevron">{{ isCollapsed(group) ? '&#9656;' : '&#9662;' }}</span>
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

    <div class="selection-summary" data-testid="tools-selection-summary">
      <span v-if="selectedToolCount === 0">No tools enabled</span>
      <span v-else>{{ selectedToolCount }} of {{ totalToolCount }} tools enabled</span>
      <span v-if="wildcardGroups.length > 0" class="summary-note">
        &middot; all current and future tools in
        {{ wildcardGroups.map((g) => g.label).join(', ') }}
      </span>
    </div>

    <p v-if="sandboxSelected" class="sandbox-note" data-testid="tools-sandbox-note">
      Workspace tools are selected, so every conversation in this mode starts its own sandbox
      session.
    </p>
  </div>
</template>

<style scoped>
.tool-checkbox-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.tool-actions {
  display: flex;
  gap: 8px;
}

.action-btn {
  padding: 6px 12px;
  background: #f8f9fa;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 12px;
  cursor: pointer;
  transition: background 0.2s;
}

.action-btn:hover:not(:disabled) {
  background: #e9ecef;
}

.action-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.search-box {
  position: relative;
}

.search-input {
  width: 100%;
  padding: 8px 12px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
}

.search-input:focus {
  outline: none;
  border-color: #0d6efd;
  box-shadow: 0 0 0 2px rgba(13, 110, 253, 0.25);
}

.no-tools {
  padding: 16px;
  text-align: center;
  color: #666;
  background: #f8f9fa;
  border-radius: 4px;
}

.group-list {
  max-height: 320px;
  overflow-y: auto;
  border: 1px solid #ddd;
  border-radius: 4px;
}

.tool-group + .tool-group {
  border-top: 1px solid #ddd;
}

.group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 12px;
  background: #f8f9fa;
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

.group-name {
  font-size: 13px;
  font-weight: 600;
  color: #333;
}

.collapse-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  background: none;
  border: none;
  cursor: pointer;
  color: #666;
  font-size: 12px;
  padding: 2px 4px;
}

.group-count {
  font-variant-numeric: tabular-nums;
}

.group-warning {
  margin: 0;
  padding: 8px 12px;
  font-size: 12px;
  line-height: 1.4;
  color: #8a6d3b;
  background: #fcf8e3;
}

.tool-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.tool-item {
  border-top: 1px solid #eee;
}

.tool-item.wildcard {
  background: #f6faff;
}

.tool-label {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 10px 12px;
  cursor: pointer;
  transition: background 0.2s;
}

.tool-label:hover:not(.disabled) {
  background: #f8f9fa;
}

.tool-label.disabled {
  cursor: not-allowed;
  opacity: 0.7;
}

.tool-label input[type='checkbox'] {
  margin-top: 2px;
  flex-shrink: 0;
}

.tool-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.tool-name {
  font-weight: 500;
  font-size: 14px;
  color: #333;
}

.tool-description {
  font-size: 12px;
  color: #666;
  line-height: 1.4;
}

.selection-summary {
  font-size: 12px;
  color: #666;
  text-align: right;
}

.summary-note {
  color: #0d6efd;
}

.sandbox-note {
  margin: 0;
  font-size: 12px;
  line-height: 1.4;
  color: #8a6d3b;
  background: #fcf8e3;
  border-radius: 4px;
  padding: 8px 10px;
}
</style>
