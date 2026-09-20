<script setup lang="ts">
import { ref, computed, watch, onMounted, nextTick } from 'vue';
import { getConversationCapabilities } from '@/api/conversationsApi';
import { BUILT_IN_TOOL_GROUP, SANDBOX_TOOL_GROUP } from '@/types/chatMode';
import type { ChatMode, ChatModeCreateUpdate, ToolDefinition } from '@/types/chatMode';
import { selectionFromMode, selectionToModeFields, toolGroup, toolId } from '@/utils/modeToolSelection';
import ToolCheckboxList from './ToolCheckboxList.vue';
import EnvEditor from './EnvEditor.vue';

const props = defineProps<{
  mode?: ChatMode | null;
  tools: ToolDefinition[];
  isLoading?: boolean;
}>();

/**
 * Whether the running gateway can apply per-sandbox env at all — see `getConversationCapabilities`,
 * which fails closed. Hidden rather than disabled for the same reason as the workspace form: on a
 * gateway without the env routes, nothing typed here would ever reach the sandbox, and an editor
 * showing variables implies otherwise.
 */
const sandboxEnvSupported = ref(false);

onMounted(() => {
  // The `.catch` keeps failing-closed a property of this call site rather than of the API helper's
  // current internals — see the matching note in `WorkspaceSelector.vue`.
  void getConversationCapabilities()
    .then((c) => {
      sandboxEnvSupported.value = c.sandboxEnv;
    })
    .catch(() => {
      sandboxEnvSupported.value = false;
    });
});

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
 * conversations in this mode. Empty means "no fragment"; the save payload writes an explicit
 * `null` for it (the server's presence-aware update contract reads an omitted key as "leave the
 * stored fragment alone", not "clear it").
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
 * an agent template restricts its own tool list. Empty means "not enforced"; the save payload
 * writes an explicit `null` for it (the server's presence-aware update contract reads an omitted
 * key as "leave the stored selection alone", not "disable enforcement").
 */
const requiredToolIds = ref<string[]>([]);
/**
 * Stored required-tool ids the catalog has no row for (e.g. a `tasks:*` pattern in a mode copied
 * from a system mode). The picker cannot render them, and a choice the user was never shown is
 * not a choice the user revoked — so they ride along untouched and are re-appended on save.
 */
const preservedRequiredToolIds = ref<string[]>([]);
/**
 * Sandbox environment variables for this mode. Seeded from the mode's stored map (`{}` when absent
 * or null); the save payload omits the key when unchanged and writes an explicit `null` to clear it
 * — see {@link handleSave} — matching the server's presence-aware update contract.
 */
const env = ref<Record<string, string>>({});
/** Live handle on the env rows, so `validate()` can refuse a save the editor already knows is bad. */
const envEditorRef = ref<InstanceType<typeof EnvEditor> | null>(null);
/** A create/update failure surfaced by the parent via the exposed {@link showFormError}. */
const formError = ref<string | null>(null);

/**
 * Collapsed state per section. The form was a single unbroken ~1750px scroll with no grouping; the
 * sections below give it structure, and collapsing one is how a user gets past the parts they are
 * not editing. Bodies are `v-show`, never `v-if`: the field still has to exist for `v-model`,
 * validation and the suites' selectors whether or not its section is open.
 */
type SectionKey = 'basics' | 'prompts' | 'tools' | 'subagent-tools' | 'env';
const collapsedSections = ref<Record<SectionKey, boolean>>({
  basics: false,
  prompts: false,
  tools: false,
  'subagent-tools': true,
  env: true,
});

function isSectionOpen(key: SectionKey): boolean {
  return !collapsedSections.value[key];
}

function toggleSection(key: SectionKey): void {
  collapsedSections.value = {
    ...collapsedSections.value,
    [key]: !collapsedSections.value[key],
  };
}

/**
 * A section that already holds configuration opens by default: collapsing a populated
 * "Required sub-agent tools" would hide a setting the mode is actually running with, which is the
 * opposite of the problem the sections are here to solve.
 */
function syncSectionDefaults(): void {
  collapsedSections.value = {
    ...collapsedSections.value,
    'subagent-tools':
      requiredToolIds.value.length === 0 && preservedRequiredToolIds.value.length === 0,
    env: Object.keys(env.value).length === 0,
  };
}

/** Section open state is also the jump target: opening a collapsed section scrolls it into view. */
const sectionEls = ref<Record<string, HTMLElement | null>>({});

function setSectionEl(key: SectionKey, el: unknown): void {
  sectionEls.value[key] = el instanceof HTMLElement ? el : null;
}

function onSectionToggle(key: SectionKey): void {
  const wasClosed = !isSectionOpen(key);
  toggleSection(key);
  if (!wasClosed) return;
  void nextTick(() => {
    sectionEls.value[key]?.scrollIntoView?.({ block: 'nearest' });
  });
}

/**
 * The two prompt fields are the form's tallest content and used to render six and three rows with
 * their own inner scrollbars — a nested scroll region inside a scrolling form. They now grow with
 * the text up to a cap, so the inner scrollbar only appears for genuinely long prompts.
 */
const systemPromptEl = ref<HTMLTextAreaElement | null>(null);
const subAgentPromptEl = ref<HTMLTextAreaElement | null>(null);

function autoGrow(el: HTMLTextAreaElement | null): void {
  if (!el) return;
  // jsdom/happy-dom report scrollHeight 0 (no layout), which would collapse the field to nothing.
  el.style.height = 'auto';
  if (el.scrollHeight > 0) el.style.height = `${el.scrollHeight}px`;
}

function growPrompts(): void {
  void nextTick(() => {
    autoGrow(systemPromptEl.value);
    autoGrow(subAgentPromptEl.value);
  });
}

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

/** Header chips: a glance at what the mode grants, without scrolling to the pickers. */
const enabledToolSummary = computed(() => {
  const count = selectedToolIds.value.filter((id) => !id.endsWith(':*')).length;
  const wildcards = selectedToolIds.value.filter((id) => id.endsWith(':*')).length;
  if (wildcards > 0) return `${count + wildcards === 0 ? 'No' : count} tools + ${wildcards} group${wildcards === 1 ? '' : 's'}`;
  return count === 1 ? '1 tool' : `${count} tools`;
});

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
      env.value = { ...(newMode.env ?? {}) };
      formError.value = null;
    } else {
      resetForm();
    }
    syncSectionDefaults();
    growPrompts();
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
    syncSectionDefaults();
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
  env.value = {};
  formError.value = null;
  nameError.value = '';
  systemPromptError.value = '';
}

/** Order-insensitive record equality — same semantics as `WorkspaceSelector`'s helper of the same name. */
function sameRecord(a: Record<string, string>, b: Record<string, string>): boolean {
  const aKeys = Object.keys(a);
  const bKeys = Object.keys(b);
  if (aKeys.length !== bKeys.length) return false;
  return aKeys.every((k) => Object.prototype.hasOwnProperty.call(b, k) && a[k] === b[k]);
}

/**
 * Surfaces a create/update failure from the parent (e.g. `InvalidEnvError`). The form stays mounted
 * so the inline error element renders; mirrors `WorkspaceSelector.showFormError`.
 */
function showFormError(message: string): void {
  formError.value = message;
}

defineExpose({ showFormError });

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

  // The env rows validate themselves and render their own per-row messages, but until the parent
  // ASKS, nothing stopped the save: `buildRecord` collapses two rows with the same name into one
  // object key, so submitting with a duplicate silently dropped a variable the user had typed. The
  // row error was on screen the whole time and the save went through anyway.
  if (envEditorRef.value?.hasErrors) {
    formError.value = 'Fix the highlighted environment variables before saving.';
    valid = false;
  }

  // A failed field inside a collapsed section would otherwise report an error the user cannot see.
  if (nameError.value) collapsedSections.value = { ...collapsedSections.value, basics: false };
  if (systemPromptError.value) {
    collapsedSections.value = { ...collapsedSections.value, prompts: false };
  }
  if (envEditorRef.value?.hasErrors) {
    collapsedSections.value = { ...collapsedSections.value, env: false };
  }

  return valid;
}

function handleSave(): void {
  formError.value = null;
  if (!validate()) return;

  const trimmedSubAgentPrompt = subAgentPrompt.value.trim();
  const data: ChatModeCreateUpdate = {
    name: name.value.trim(),
    // Explicit null (not omission) when blank: the server's presence-aware update contract
    // preserves the stored value for an omitted key, so clearing on save requires the literal.
    description: description.value.trim() || null,
    systemPrompt: systemPrompt.value.trim(),
    // subAgentPrompt writes an explicit null when blank, for the same reason as description.
    // subAgentPromptPlacement is unaffected by this fix: it has no persisted "clear" meaning of
    // its own and stays omitted whenever there is no fragment to place.
    subAgentPrompt: trimmedSubAgentPrompt || null,
    subAgentPromptPlacement: trimmedSubAgentPrompt ? subAgentPromptPlacement.value : undefined,
    // These policy fields are not editable in this form. Preserve an existing mode's values rather
    // than silently clearing them on save.
    subAgentReasoningEffort: props.mode?.subAgentReasoningEffort,
    subAgentModelIntelligenceByType: props.mode?.subAgentModelIntelligenceByType,
    defaultSubAgentModelIntelligence: props.mode?.defaultSubAgentModelIntelligence,
    // props.mode is passed so a group the catalog could not show is preserved, not zeroed.
    ...selectionToModeFields(selectedToolIds.value, props.tools, props.mode),
  };

  // Explicit null (not omission) disables enforcement: the server's presence-aware update
  // contract preserves the stored selection for an omitted key, so an untouched mode's
  // enforcement would otherwise survive a save that means "I unchecked everything".
  // Preserved (unrenderable) ids are appended after the picked ones — the catalog has no position
  // for them, so their stored order is the only stable one available.
  const requiredTools = [...requiredToolIds.value, ...preservedRequiredToolIds.value];
  data.subAgentRequiredTools = requiredTools.length > 0 ? requiredTools : null;

  // env follows the same presence-aware contract as subAgentPrompt/description: omitted means
  // "unchanged" server-side, so it is only written when it actually differs from the loaded mode.
  // An explicit null clears a previously-set map; an empty object is not a valid "clear" spelling
  // here (unlike Workspace.env, which is never tri-state to begin with).
  const loadedEnv = props.mode?.env ?? {};
  if (!sameRecord(env.value, loadedEnv)) {
    data.env = Object.keys(env.value).length > 0 ? { ...env.value } : null;
  }

  emit('save', data);
}

function handleCancel(): void {
  emit('cancel');
}
</script>

<template>
  <div class="mode-editor" data-testid="mode-editor">
    <!--
      Three bands: a pinned title, one scrolling body, a pinned action bar. Save and Cancel used to
      sit at the foot of the body, which on a real mode put them ~1700px below the fold.
    -->
    <form class="editor-form" @submit.prevent="handleSave">
      <header class="editor-header">
        <div class="editor-heading">
          <h2 class="editor-title">{{ title }}</h2>
          <p class="editor-subtitle">
            {{
              isEditing
                ? 'Changes apply to new conversations started in this mode.'
                : 'A mode bundles a system prompt with the tools a conversation may use.'
            }}
          </p>
        </div>
        <span v-if="name.trim()" class="editor-chip">{{ enabledToolSummary }}</span>
      </header>

      <div class="editor-body">
        <section
          :ref="(el) => setSectionEl('basics', el)"
          class="editor-section"
          data-testid="mode-editor-section-basics"
        >
          <h3 class="section-heading">
            <button
              type="button"
              class="section-toggle"
              :aria-expanded="isSectionOpen('basics')"
              @click="onSectionToggle('basics')"
            >
              <span class="section-chevron" aria-hidden="true"></span>
              <span class="section-name">Basics</span>
              <span class="section-meta">Name and description</span>
            </button>
          </h3>

          <div v-show="isSectionOpen('basics')" class="section-body">
            <div class="form-group">
              <label for="mode-name" class="form-label">
                Name <span class="required" aria-hidden="true">*</span>
              </label>
              <input
                id="mode-name"
                v-model="name"
                data-testid="mode-editor-name"
                type="text"
                class="form-input"
                :class="{ error: nameError }"
                placeholder="Enter mode name"
                required
                :aria-invalid="!!nameError"
                :aria-describedby="nameError ? 'mode-name-error' : undefined"
                :disabled="isLoading"
              />
              <span v-if="nameError" id="mode-name-error" class="error-message">{{ nameError }}</span>
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
              <p class="field-hint">
                Shown in the mode list — the first line is what people scan, so lead with what
                makes this mode different.
              </p>
            </div>
          </div>
        </section>

        <section
          :ref="(el) => setSectionEl('prompts', el)"
          class="editor-section"
          data-testid="mode-editor-section-prompts"
        >
          <h3 class="section-heading">
            <button
              type="button"
              class="section-toggle"
              :aria-expanded="isSectionOpen('prompts')"
              @click="onSectionToggle('prompts')"
            >
              <span class="section-chevron" aria-hidden="true"></span>
              <span class="section-name">Prompts</span>
              <span class="section-meta">System prompt and sub-agent fragment</span>
            </button>
          </h3>

          <div v-show="isSectionOpen('prompts')" class="section-body">
            <div class="form-group">
              <label for="mode-prompt" class="form-label">
                System Prompt <span class="required" aria-hidden="true">*</span>
              </label>
              <textarea
                id="mode-prompt"
                ref="systemPromptEl"
                v-model="systemPrompt"
                class="form-textarea system-prompt"
                :class="{ error: systemPromptError }"
                placeholder="Enter the system prompt for this mode..."
                rows="6"
                required
                :aria-invalid="!!systemPromptError"
                :aria-describedby="systemPromptError ? 'mode-prompt-error' : undefined"
                :disabled="isLoading"
                @input="autoGrow(systemPromptEl)"
              ></textarea>
              <span v-if="systemPromptError" id="mode-prompt-error" class="error-message">{{
                systemPromptError
              }}</span>
            </div>

            <div class="form-group">
              <label for="mode-subagent-prompt" class="form-label">Sub-agent Prompt</label>
              <textarea
                id="mode-subagent-prompt"
                ref="subAgentPromptEl"
                v-model="subAgentPrompt"
                data-testid="mode-editor-subagent-prompt"
                class="form-textarea system-prompt"
                placeholder="Optional fragment added to every sub-agent's system prompt in this mode..."
                rows="3"
                :disabled="isLoading"
                @input="autoGrow(subAgentPromptEl)"
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
          </div>
        </section>

        <!--
          Order matters beyond looks: the enabled picker must stay ahead of the required one in the
          DOM. `ModeEditor.test.ts` resolves a duplicated tool id with findAll(...)[0] and treats
          that first hit as the enabled instance.
        -->
        <section
          :ref="(el) => setSectionEl('tools', el)"
          class="editor-section"
          data-testid="mode-editor-section-tools"
        >
          <h3 class="section-heading">
            <button
              type="button"
              class="section-toggle"
              :aria-expanded="isSectionOpen('tools')"
              @click="onSectionToggle('tools')"
            >
              <span class="section-chevron" aria-hidden="true"></span>
              <span class="section-name">Enabled tools</span>
              <span class="section-meta">What the conversation itself may call</span>
            </button>
          </h3>

          <div v-show="isSectionOpen('tools')" class="section-body">
            <ToolCheckboxList v-model="selectedToolIds" :tools="tools" :disabled="isLoading" />
          </div>
        </section>

        <section
          :ref="(el) => setSectionEl('subagent-tools', el)"
          class="editor-section accent-required"
          data-testid="mode-editor-required-tools"
        >
          <h3 class="section-heading">
            <button
              type="button"
              class="section-toggle"
              :aria-expanded="isSectionOpen('subagent-tools')"
              @click="onSectionToggle('subagent-tools')"
            >
              <span class="section-chevron" aria-hidden="true"></span>
              <span class="section-name">Required sub-agent tools</span>
              <span class="section-meta">Guaranteed to every sub-agent</span>
            </button>
          </h3>

          <div v-show="isSectionOpen('subagent-tools')" class="section-body">
            <p class="field-hint" data-testid="mode-editor-required-tools-hint">
              These tools are guaranteed to every sub-agent in this mode, even if an agent template
              restricts its tools.
            </p>
            <ToolCheckboxList
              v-model="requiredToolIds"
              :tools="requiredToolsCatalog"
              :disabled="isLoading"
              variant="required"
            />
          </div>
        </section>

        <section
          v-if="sandboxEnvSupported"
          :ref="(el) => setSectionEl('env', el)"
          class="editor-section"
          data-testid="mode-editor-env"
        >
          <h3 class="section-heading">
            <button
              type="button"
              class="section-toggle"
              :aria-expanded="isSectionOpen('env')"
              @click="onSectionToggle('env')"
            >
              <span class="section-chevron" aria-hidden="true"></span>
              <span class="section-name">Environment variables</span>
              <span class="section-meta">Layered onto every sandbox session</span>
            </button>
          </h3>

          <div v-show="isSectionOpen('env')" class="section-body">
            <p class="field-hint">
              Sandbox environment variables layered onto every session opened in this mode.
            </p>
            <EnvEditor
              ref="envEditorRef"
              v-model="env"
              testid-prefix="mode-env"
              :disabled="isLoading"
            />
          </div>
        </section>
      </div>

      <footer class="editor-footer">
        <p v-if="formError" class="form-error" role="alert" data-testid="mode-editor-form-error">
          {{ formError }}
        </p>

        <div class="form-actions">
          <button type="button" class="btn btn-secondary" :disabled="isLoading" @click="handleCancel">
            Cancel
          </button>
          <button
            type="submit"
            class="btn btn-primary"
            data-testid="mode-editor-save"
            :disabled="isLoading"
          >
            {{ isLoading ? 'Saving...' : 'Save mode' }}
          </button>
        </div>
      </footer>
    </form>
  </div>
</template>

<style scoped>
.mode-editor {
  --me-border: #d6dbe1;
  --me-border-soft: #e2e6eb;
  --me-surface: #f7f8fa;
  --me-text: #303944;
  --me-muted: #64748b;
  --me-accent: #2d6cdf;
  --me-accent-soft: #eef3f9;
  --me-danger: #b4232e;

  display: flex;
  flex-direction: column;
  min-height: 0;
  height: 100%;
  color: var(--me-text);
}

.editor-form {
  display: flex;
  flex-direction: column;
  min-height: 0;
  flex: 1;
}

.editor-header {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  flex: none;
  padding: 14px 20px 12px;
  border-bottom: 1px solid var(--me-border-soft);
  background: #fff;
}

.editor-heading {
  min-width: 0;
}

.editor-title {
  margin: 0;
  font-size: 17px;
  font-weight: 600;
}

.editor-subtitle {
  margin: 3px 0 0;
  font-size: 12.5px;
  line-height: 1.45;
  color: var(--me-muted);
}

.editor-chip {
  flex: none;
  margin-left: auto;
  padding: 3px 9px;
  border: 1px solid var(--me-border);
  border-radius: 999px;
  background: var(--me-surface);
  font-size: 11.5px;
  color: var(--me-muted);
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

/* The one scroll region in the editor. `min-height: 0` is what actually lets it scroll. */
.editor-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 14px 20px 18px;
  background: var(--me-surface);
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.editor-section {
  /*
   * `flex: none` is load-bearing, not tidiness. `.editor-body` is a flex column, so its children
   * default to `flex-shrink: 1`: once the sections were taller than the body they each shrank and
   * `overflow: hidden` silently clipped the fields inside them instead of letting the body scroll.
   */
  flex: none;
  border: 1px solid var(--me-border-soft);
  border-radius: 8px;
  background: #fff;
  overflow: hidden;
}

.editor-section.accent-required {
  border-color: #ecdcb9;
}

.section-heading {
  margin: 0;
  font-size: inherit;
  font-weight: inherit;
  position: sticky;
  top: 0;
  z-index: 2;
}

.section-toggle {
  display: flex;
  align-items: baseline;
  gap: 8px;
  width: 100%;
  padding: 10px 14px;
  border: 0;
  background: var(--me-surface);
  color: var(--me-text);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.section-toggle:hover {
  background: var(--me-accent-soft);
}

.section-toggle:focus-visible {
  outline: 2px solid var(--me-accent);
  outline-offset: -2px;
}

.accent-required .section-toggle {
  background: #fdf5e6;
}

.accent-required .section-toggle:hover {
  background: #fbeed3;
}

.section-chevron {
  flex: none;
  align-self: center;
  width: 0;
  height: 0;
  border-left: 5px solid currentColor;
  border-top: 4px solid transparent;
  border-bottom: 4px solid transparent;
  color: var(--me-muted);
  transition: transform 0.15s ease;
}

.section-toggle[aria-expanded='true'] .section-chevron {
  transform: rotate(90deg);
}

.section-name {
  font-size: 13.5px;
  font-weight: 600;
}

.section-meta {
  font-size: 12px;
  color: var(--me-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.section-body {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 14px;
  border-top: 1px solid var(--me-border-soft);
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.form-label {
  font-size: 13px;
  font-weight: 500;
  color: var(--me-text);
}

.required {
  color: var(--me-danger);
}

.form-input,
.form-textarea {
  padding: 9px 11px;
  border: 1px solid var(--me-border);
  border-radius: 6px;
  font-size: 13.5px;
  font-family: inherit;
  color: var(--me-text);
  background: #fff;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.form-input:focus,
.form-textarea:focus {
  outline: none;
  border-color: var(--me-accent);
  box-shadow: 0 0 0 3px rgb(45 108 223 / 18%);
}

.form-input.error,
.form-textarea.error {
  border-color: var(--me-danger);
}

.form-input.error:focus,
.form-textarea.error:focus {
  box-shadow: 0 0 0 3px rgb(180 35 46 / 20%);
}

.form-textarea {
  resize: vertical;
  min-height: 60px;
}

/*
 * `max-height` caps the auto-grow: a very long prompt still gets its own scrollbar, but an ordinary
 * one is fully visible instead of being read through a six-row porthole.
 */
.system-prompt {
  font-family: 'Monaco', 'Menlo', 'Ubuntu Mono', monospace;
  font-size: 12.5px;
  line-height: 1.55;
  min-height: 130px;
  max-height: 46vh;
}

.error-message {
  font-size: 12px;
  color: var(--me-danger);
}

.field-hint {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--me-muted);
}

.placement-row {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 4px;
}

.placement-select {
  flex: 1;
  min-width: 0;
}

.editor-footer {
  flex: none;
  padding: 12px 20px;
  border-top: 1px solid var(--me-border-soft);
  background: #fff;
}

.form-error {
  margin: 0 0 10px;
  padding: 8px 10px;
  border: 1px solid #f0c7cb;
  border-radius: 6px;
  background: #fdf1f2;
  font-size: 12.5px;
  line-height: 1.45;
  color: #8f1c26;
}

.form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}

.btn {
  padding: 9px 18px;
  border: 1px solid transparent;
  border-radius: 6px;
  font-size: 13.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s, opacity 0.15s;
}

.btn:focus-visible {
  outline: 2px solid var(--me-accent);
  outline-offset: 2px;
}

.btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.btn-primary {
  background: var(--me-accent);
  color: #fff;
}

.btn-primary:hover:not(:disabled) {
  background: #2559b8;
}

.btn-secondary {
  background: #fff;
  border-color: var(--me-border);
  color: var(--me-text);
}

.btn-secondary:hover:not(:disabled) {
  background: var(--me-surface);
  border-color: #aeb7c2;
}

@media (max-width: 640px) {
  .editor-header,
  .editor-footer {
    padding-inline: 14px;
  }

  .editor-body {
    padding-inline: 14px;
  }

  .section-meta {
    display: none;
  }

  .placement-row {
    align-items: stretch;
    flex-direction: column;
    gap: 6px;
  }

  .form-actions .btn {
    flex: 1;
  }
}
</style>
