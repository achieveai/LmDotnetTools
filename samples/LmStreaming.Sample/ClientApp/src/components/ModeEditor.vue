<script setup lang="ts">
import { ref, computed, watch } from 'vue';
import { BUILT_IN_TOOL_GROUP, SANDBOX_TOOL_GROUP } from '@/types/chatMode';
import type { ChatMode, ChatModeCreateUpdate, ToolDefinition } from '@/types/chatMode';
import { selectionFromMode, selectionToModeFields, toolGroup, toolId } from '@/utils/modeToolSelection';
import ToolCheckboxList from './ToolCheckboxList.vue';

const props = defineProps<{
  mode?: ChatMode | null;
  tools: ToolDefinition[];
  isLoading?: boolean;
}>();

const emit = defineEmits<{
  save: [data: ChatModeCreateUpdate];
  cancel: [];
}>();

// Form state
const name = ref('');
const description = ref('');
const systemPrompt = ref('');
/**
 * Per-mode sub-agent prompt fragment (#610): folded into every sub-agent's system prompt for
 * conversations in this mode. Empty means "no fragment" and both fields are omitted from the
 * save payload so an untouched mode keeps today's behavior exactly.
 */
const subAgentPrompt = ref('');
const subAgentPromptPlacement = ref<'prepend' | 'append'>('append');
/**
 * The flat set of selected tool ids across every catalog group. A mode stores this across three
 * fields with three different null rules, so the editor holds the flat form and converts at the
 * boundary (see utils/modeToolSelection).
 */
const selectedToolIds = ref<string[]>([]);
/**
 * Required sub-agent tools (#623): guaranteed to every sub-agent spawned in this mode, even when
 * an agent template restricts its own tool list. Empty means "not enforced" and the field is
 * omitted from the save payload — the same unset convention the server treats as today's
 * behavior byte-for-byte.
 */
const requiredToolIds = ref<string[]>([]);
/**
 * Stored required-tool ids the catalog has no row for (e.g. a `tasks:*` pattern in a mode copied
 * from a system mode). The picker cannot render them, and a choice the user was never shown is
 * not a choice the user revoked — so they ride along untouched and are re-appended on save.
 */
const preservedRequiredToolIds = ref<string[]>([]);

/**
 * Two groups are left out of this picker because a pick there could not do what it says:
 * - Provider built-ins (e.g. `web_search`) execute inside the provider, not as registered tool
 *   contracts, so they can never be granted to a sub-agent.
 * - Sandbox tools come from a live gateway with no static roster, so the server resolves a
 *   `sandbox:*` requirement to nothing — offering the row here would recreate the exact #623
 *   silent-failure shape this picker exists to eliminate. (Excluding the group also keeps the
 *   picker's "starts its own sandbox session" note honest: required picks never feed
 *   `enabledCapabilityTools`, so they never open a sandbox session.)
 * A `sandbox:*`/`sandbox:tool` id stored in the mode anyway (hand-edited YAML) still round-trips
 * via the preserved-ids path below; the server logs the `sandbox:*` wildcard as unresolved.
 */
const requiredToolsCatalog = computed(() =>
  props.tools.filter((tool) => {
    const group = toolGroup(tool);
    return group !== BUILT_IN_TOOL_GROUP && group !== SANDBOX_TOOL_GROUP;
  })
);

function loadRequiredTools(mode: ChatMode | null | undefined): void {
  const stored = mode?.subAgentRequiredTools ?? [];
  const catalogIds = new Set(requiredToolsCatalog.value.map(toolId));
  requiredToolIds.value = stored.filter((id) => catalogIds.has(id));
  preservedRequiredToolIds.value = stored.filter((id) => !catalogIds.has(id));
}

// Validation
const nameError = ref('');
const systemPromptError = ref('');

const isEditing = computed(() => !!props.mode);
const title = computed(() => (isEditing.value ? 'Edit Mode' : 'Create New Mode'));

// Initialize form when mode changes
watch(
  () => props.mode,
  (newMode) => {
    if (newMode) {
      name.value = newMode.name;
      description.value = newMode.description || '';
      systemPrompt.value = newMode.systemPrompt;
      subAgentPrompt.value = newMode.subAgentPrompt || '';
      subAgentPromptPlacement.value = newMode.subAgentPromptPlacement || 'append';
      selectedToolIds.value = selectionFromMode(newMode, props.tools);
      loadRequiredTools(newMode);
    } else {
      resetForm();
    }
  },
  { immediate: true }
);

// The catalog arrives asynchronously, so a mode opened before it lands would otherwise show an
// empty selection and then save that emptiness. Re-derive whenever the catalog changes.
watch(
  () => props.tools,
  (tools) => {
    selectedToolIds.value = selectionFromMode(props.mode ?? null, tools);
    loadRequiredTools(props.mode);
  }
);

function resetForm(): void {
  name.value = '';
  description.value = '';
  systemPrompt.value = '';
  subAgentPrompt.value = '';
  subAgentPromptPlacement.value = 'append';
  selectedToolIds.value = selectionFromMode(null, props.tools);
  requiredToolIds.value = [];
  preservedRequiredToolIds.value = [];
  nameError.value = '';
  systemPromptError.value = '';
}

function validate(): boolean {
  let valid = true;

  if (!name.value.trim()) {
    nameError.value = 'Name is required';
    valid = false;
  } else {
    nameError.value = '';
  }

  if (!systemPrompt.value.trim()) {
    systemPromptError.value = 'System prompt is required';
    valid = false;
  } else {
    systemPromptError.value = '';
  }

  return valid;
}

function handleSave(): void {
  if (!validate()) return;

  const trimmedSubAgentPrompt = subAgentPrompt.value.trim();
  const data: ChatModeCreateUpdate = {
    name: name.value.trim(),
    description: description.value.trim() || undefined,
    systemPrompt: systemPrompt.value.trim(),
    // Both omitted when the fragment is empty — an untouched mode must save exactly what it
    // saved before these fields existed.
    subAgentPrompt: trimmedSubAgentPrompt || undefined,
    subAgentPromptPlacement: trimmedSubAgentPrompt ? subAgentPromptPlacement.value : undefined,
    // These policy fields are not editable in this form. Preserve an existing mode's values rather
    // than silently clearing them on save.
    subAgentReasoningEffort: props.mode?.subAgentReasoningEffort,
    subAgentModelIntelligenceByType: props.mode?.subAgentModelIntelligenceByType,
    defaultSubAgentModelIntelligence: props.mode?.defaultSubAgentModelIntelligence,
    // props.mode is passed so a group the catalog could not show is preserved, not zeroed.
    ...selectionToModeFields(selectedToolIds.value, props.tools, props.mode),
  };

  // Empty saves as "not enforced": the field is omitted so an untouched mode round-trips exactly.
  // Preserved (unrenderable) ids are appended after the picked ones — the catalog has no position
  // for them, so their stored order is the only stable one available.
  const requiredTools = [...requiredToolIds.value, ...preservedRequiredToolIds.value];
  if (requiredTools.length > 0) {
    data.subAgentRequiredTools = requiredTools;
  }

  emit('save', data);
}

function handleCancel(): void {
  emit('cancel');
}
</script>

<template>
  <div class="mode-editor" data-testid="mode-editor">
    <h2 class="editor-title">{{ title }}</h2>

    <form @submit.prevent="handleSave" class="editor-form">
      <div class="form-group">
        <label for="mode-name" class="form-label">
          Name <span class="required">*</span>
        </label>
        <input
          id="mode-name"
          v-model="name"
          data-testid="mode-editor-name"
          type="text"
          class="form-input"
          :class="{ error: nameError }"
          placeholder="Enter mode name"
          :disabled="isLoading"
        />
        <span v-if="nameError" class="error-message">{{ nameError }}</span>
      </div>

      <div class="form-group">
        <label for="mode-description" class="form-label">Description</label>
        <textarea
          id="mode-description"
          v-model="description"
          class="form-textarea"
          placeholder="Optional description of what this mode does"
          rows="2"
          :disabled="isLoading"
        ></textarea>
      </div>

      <div class="form-group">
        <label for="mode-prompt" class="form-label">
          System Prompt <span class="required">*</span>
        </label>
        <textarea
          id="mode-prompt"
          v-model="systemPrompt"
          class="form-textarea system-prompt"
          :class="{ error: systemPromptError }"
          placeholder="Enter the system prompt for this mode..."
          rows="6"
          :disabled="isLoading"
        ></textarea>
        <span v-if="systemPromptError" class="error-message">{{ systemPromptError }}</span>
      </div>

      <div class="form-group">
        <label for="mode-subagent-prompt" class="form-label">Sub-agent Prompt</label>
        <textarea
          id="mode-subagent-prompt"
          v-model="subAgentPrompt"
          data-testid="mode-editor-subagent-prompt"
          class="form-textarea system-prompt"
          placeholder="Optional fragment added to every sub-agent's system prompt in this mode..."
          rows="3"
          :disabled="isLoading"
        ></textarea>
        <div class="placement-row">
          <label for="mode-subagent-placement" class="form-label">Placement</label>
          <select
            id="mode-subagent-placement"
            v-model="subAgentPromptPlacement"
            data-testid="mode-editor-subagent-placement"
            class="form-input placement-select"
            :disabled="isLoading || !subAgentPrompt.trim()"
          >
            <option value="append">Append (after the sub-agent's own prompt)</option>
            <option value="prepend">Prepend (before the sub-agent's own prompt)</option>
          </select>
        </div>
      </div>

      <div class="form-group">
        <label class="form-label">Enabled Tools</label>
        <ToolCheckboxList
          v-model="selectedToolIds"
          :tools="tools"
          :disabled="isLoading"
        />
      </div>

      <div class="form-group" data-testid="mode-editor-required-tools">
        <label class="form-label">Required Sub-agent Tools</label>
        <p class="field-hint" data-testid="mode-editor-required-tools-hint">
          These tools are guaranteed to every sub-agent in this mode, even if an agent template
          restricts its tools.
        </p>
        <ToolCheckboxList
          v-model="requiredToolIds"
          :tools="requiredToolsCatalog"
          :disabled="isLoading"
        />
      </div>

      <div class="form-actions">
        <button
          type="button"
          class="btn btn-secondary"
          :disabled="isLoading"
          @click="handleCancel"
        >
          Cancel
        </button>
        <button
          type="submit"
          class="btn btn-primary"
          data-testid="mode-editor-save"
          :disabled="isLoading"
        >
          {{ isLoading ? 'Saving...' : 'Save' }}
        </button>
      </div>
    </form>
  </div>
</template>

<style scoped>
.mode-editor {
  padding: 20px;
}

.editor-title {
  margin: 0 0 20px;
  font-size: 20px;
  font-weight: 600;
  color: #333;
}

.editor-form {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.form-label {
  font-size: 14px;
  font-weight: 500;
  color: #333;
}

.required {
  color: #dc3545;
}

.form-input,
.form-textarea {
  padding: 10px 12px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
  font-family: inherit;
  transition: border-color 0.2s, box-shadow 0.2s;
}

.form-input:focus,
.form-textarea:focus {
  outline: none;
  border-color: #0d6efd;
  box-shadow: 0 0 0 2px rgba(13, 110, 253, 0.25);
}

.form-input.error,
.form-textarea.error {
  border-color: #dc3545;
}

.form-input.error:focus,
.form-textarea.error:focus {
  box-shadow: 0 0 0 2px rgba(220, 53, 69, 0.25);
}

.form-textarea {
  resize: vertical;
  min-height: 60px;
}

.system-prompt {
  font-family: 'Monaco', 'Menlo', 'Ubuntu Mono', monospace;
  font-size: 13px;
  line-height: 1.5;
}

.error-message {
  font-size: 12px;
  color: #dc3545;
}

.field-hint {
  margin: 0;
  font-size: 12px;
  line-height: 1.4;
  color: #666;
}

.placement-row {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 6px;
}

.placement-select {
  flex: 1;
}

.form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
  padding-top: 12px;
  border-top: 1px solid #eee;
}

.btn {
  padding: 10px 20px;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.2s, opacity 0.2s;
}

.btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.btn-primary {
  background: #0d6efd;
  color: white;
}

.btn-primary:hover:not(:disabled) {
  background: #0b5ed7;
}

.btn-secondary {
  background: #6c757d;
  color: white;
}

.btn-secondary:hover:not(:disabled) {
  background: #5a6268;
}
</style>
