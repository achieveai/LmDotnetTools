<script setup lang="ts">
import { ref, watch } from 'vue';

/**
 * A row-editor for a `Record<string,string>` of environment variables, used by both the workspace
 * and chat-mode forms. `modelValue` is the flat record the parent persists; this component holds
 * its OWN row list internally (with a stable synthetic id per row, independent of the key text) so
 * that an in-progress edit — a blank "Add variable" row, a malformed or protected draft key — never
 * gets discarded by a prop round-trip. See the `lastEmitted` echo-guard below for how that is kept
 * safe against the parent's v-model reflecting our own emission straight back as a prop.
 */
const props = defineProps<{
  modelValue: Record<string, string>;
  disabled?: boolean;
  testidPrefix: string;
}>();

const emit = defineEmits<{
  'update:modelValue': [value: Record<string, string>];
}>();

interface EnvRow {
  id: number;
  key: string;
  value: string;
}

/** Mirrors the gateway's key rule (see `SandboxEnvRules` server-side). Kept in sync manually. */
const KEY_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/;

/**
 * Names the gateway refuses to let a workspace/mode set, checked case-insensitively. `PATH` is
 * deliberately NOT here — it is the one commonly-set variable that is allowed. Mirrors the gateway's
 * list server-side; the server stays authoritative, this is only for immediate feedback.
 */
const PROTECTED_KEYS = new Set([
  'SANDBOX_ALLOWED_PATHS',
  'SANDBOX_WORKSPACE',
  'SANDBOX_HOME',
  'PWSH_STATE_DIR',
  'HTTP_PROXY',
  'HTTPS_PROXY',
  'NO_PROXY',
  'NODE_TLS_REJECT_UNAUTHORIZED',
  'PYTHONHTTPSVERIFY',
  'GIT_SSL_NO_VERIFY',
  'REQUESTS_CA_BUNDLE',
  'SSL_CERT_FILE',
  'CURL_CA_BUNDLE',
  'NODE_EXTRA_CA_CERTS',
  'NODE_USE_ENV_PROXY',
]);

let nextRowId = 0;
const rows = ref<EnvRow[]>([]);

/** The record most recently emitted, so the `modelValue` watcher can tell its own echo from an
 * external reseed (e.g. the parent opening the form on a different workspace/mode) and skip
 * resetting `rows` — which would otherwise wipe an in-progress blank/invalid draft row. */
let lastEmitted: Record<string, string> | null = null;

function sameRecord(a: Record<string, string>, b: Record<string, string>): boolean {
  const aKeys = Object.keys(a);
  const bKeys = Object.keys(b);
  if (aKeys.length !== bKeys.length) return false;
  return aKeys.every((k) => Object.prototype.hasOwnProperty.call(b, k) && a[k] === b[k]);
}

function rowsFrom(record: Record<string, string>): EnvRow[] {
  return Object.entries(record).map(([key, value]) => ({ id: nextRowId++, key, value }));
}

watch(
  () => props.modelValue,
  (record) => {
    if (lastEmitted && sameRecord(record, lastEmitted)) return;
    rows.value = rowsFrom(record);
  },
  { immediate: true }
);

function buildRecord(): Record<string, string> {
  const record: Record<string, string> = {};
  for (const row of rows.value) {
    const key = row.key.trim();
    if (!key) continue;
    record[key] = row.value;
  }
  return record;
}

function emitRows(): void {
  const record = buildRecord();
  lastEmitted = record;
  emit('update:modelValue', record);
}

function addRow(): void {
  if (props.disabled) return;
  rows.value = [...rows.value, { id: nextRowId++, key: '', value: '' }];
  // The new row has an empty key, so the emitted record is unchanged — emitted anyway to keep
  // lastEmitted in step with the current row list.
  emitRows();
}

function removeRow(id: number): void {
  if (props.disabled) return;
  rows.value = rows.value.filter((row) => row.id !== id);
  emitRows();
}

function updateKey(id: number, value: string): void {
  const row = rows.value.find((r) => r.id === id);
  if (!row) return;
  row.key = value;
  emitRows();
}

function updateValue(id: number, value: string): void {
  const row = rows.value.find((r) => r.id === id);
  if (!row) return;
  row.value = value;
  emitRows();
}

/** Inline validation text for one row, mirroring the gateway's rule. Empty when the key is blank
 * (blank rows are silently dropped, not flagged as errors) or well-formed and unprotected. */
function rowError(row: EnvRow): string {
  const key = row.key.trim();
  if (!key) return '';
  if (!KEY_PATTERN.test(key)) {
    return 'Key must start with a letter or underscore and use only letters, digits, underscores.';
  }
  if (PROTECTED_KEYS.has(key.toUpperCase())) {
    return 'This name is protected by the sandbox and cannot be set.';
  }
  return '';
}
</script>

<template>
  <div class="env-editor">
    <div
      v-for="row in rows"
      :key="row.id"
      class="env-row"
      :data-testid="`${testidPrefix}-row`"
    >
      <div class="env-row-fields">
        <input
          type="text"
          class="env-input env-key-input"
          :data-testid="`${testidPrefix}-key`"
          :value="row.key"
          placeholder="KEY"
          :disabled="disabled"
          @input="updateKey(row.id, ($event.target as HTMLInputElement).value)"
        />
        <input
          type="text"
          class="env-input env-value-input"
          :data-testid="`${testidPrefix}-value`"
          :value="row.value"
          placeholder="value"
          :disabled="disabled"
          @input="updateValue(row.id, ($event.target as HTMLInputElement).value)"
        />
        <button
          type="button"
          class="env-remove-btn"
          :data-testid="`${testidPrefix}-remove`"
          :disabled="disabled"
          title="Remove variable"
          @click="removeRow(row.id)"
        >
          &times;
        </button>
      </div>
      <span
        v-if="rowError(row)"
        class="env-error"
        :data-testid="`${testidPrefix}-error`"
      >{{ rowError(row) }}</span>
    </div>

    <button
      type="button"
      class="env-add-btn"
      :data-testid="`${testidPrefix}-add`"
      :disabled="disabled"
      @click="addRow"
    >
      + Add variable
    </button>
  </div>
</template>

<style scoped>
.env-editor {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.env-row {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.env-row-fields {
  display: flex;
  align-items: center;
  gap: 6px;
}

.env-input {
  padding: 6px 8px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 13px;
  font-family: 'Monaco', 'Menlo', 'Ubuntu Mono', monospace;
}

.env-input:focus {
  outline: none;
  border-color: #0d6efd;
  box-shadow: 0 0 0 2px rgba(13, 110, 253, 0.25);
}

.env-key-input {
  flex: 1;
  min-width: 0;
}

.env-value-input {
  flex: 2;
  min-width: 0;
}

.env-remove-btn {
  flex-shrink: 0;
  width: 24px;
  height: 24px;
  padding: 0;
  background: none;
  border: 1px solid #ddd;
  border-radius: 4px;
  color: #666;
  font-size: 14px;
  line-height: 1;
  cursor: pointer;
}

.env-remove-btn:hover:not(:disabled) {
  background: #f8d7da;
  color: #b02a37;
  border-color: #f0c0c6;
}

.env-remove-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.env-error {
  font-size: 11px;
  color: #b02a37;
}

.env-add-btn {
  align-self: flex-start;
  padding: 6px 12px;
  background: #f8f9fa;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 12px;
  cursor: pointer;
  transition: background 0.2s;
}

.env-add-btn:hover:not(:disabled) {
  background: #e9ecef;
}

.env-add-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
