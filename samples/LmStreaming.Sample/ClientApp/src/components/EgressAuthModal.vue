<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref } from 'vue';
import type { EgressHeaderPair, EgressKeyKind, EgressKeyRequest, EgressKeyView } from '@/types/egressAuth';
import { useEgressAuth, egressDialogRequest } from '@/composables/useEgressAuth';

const emit = defineEmits<{ close: [] }>();

const { egressKeys, isLoading, loadEgressKeys, saveEgressKey, removeEgressKey } = useEgressAuth();

// --- Editor state -----------------------------------------------------------
const editorOpen = ref(false);
const editingId = ref<string | null>(null);
const host = ref('');
const kind = ref<EgressKeyKind>('custom-headers');
const headerName = ref('Authorization');
const tokenEndpoint = ref('');
const clientId = ref('');
const clientSecret = ref('');
const refreshToken = ref('');
const scopesText = ref('');
const headerRows = ref<EgressHeaderPair[]>([{ name: '', value: '' }]);

const hostError = ref('');
const serverError = ref('');
const saving = ref(false);

const isEditing = computed(() => editingId.value !== null);
const isOAuth = computed(() => kind.value !== 'custom-headers');
const editorTitle = computed(() => (isEditing.value ? 'Edit egress key' : 'New egress key'));

const KIND_OPTIONS: { value: EgressKeyKind; label: string }[] = [
  { value: 'custom-headers', label: 'Custom Headers' },
  { value: 'refresh-token', label: 'OAuth2 Refresh Token' },
  { value: 'client-credentials', label: 'OAuth2 Client-Credentials' },
];

function kindLabel(k: EgressKeyKind): string {
  return KIND_OPTIONS.find((o) => o.value === k)?.label ?? k;
}

function resetForm(): void {
  editingId.value = null;
  host.value = '';
  kind.value = 'custom-headers';
  headerName.value = 'Authorization';
  tokenEndpoint.value = '';
  clientId.value = '';
  clientSecret.value = '';
  refreshToken.value = '';
  scopesText.value = '';
  headerRows.value = [{ name: '', value: '' }];
  hostError.value = '';
  serverError.value = '';
}

function startCreate(prefillHost?: string): void {
  resetForm();
  if (prefillHost) {
    host.value = prefillHost;
  }
  editorOpen.value = true;
}

function startEdit(view: EgressKeyView): void {
  resetForm();
  editingId.value = view.id;
  host.value = view.host;
  kind.value = view.kind;
  headerName.value = view.headerName || 'Authorization';
  scopesText.value = view.scopes.join(' ');
  if (view.kind === 'custom-headers') {
    // Prefill header NAMES only; values are masked, so leave them blank to keep
    // the stored secret on update.
    headerRows.value =
      view.headerNames.length > 0
        ? view.headerNames.map((n) => ({ name: n, value: '' }))
        : [{ name: '', value: '' }];
  }
  editorOpen.value = true;
}

function cancelEditor(): void {
  editorOpen.value = false;
  resetForm();
}

function addHeaderRow(): void {
  headerRows.value.push({ name: '', value: '' });
}

function removeHeaderRow(index: number): void {
  if (headerRows.value.length > 1) {
    headerRows.value.splice(index, 1);
  }
}

function parseScopes(text: string): string[] {
  return text
    .split(/[\s,]+/)
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

/**
 * Builds the upsert request. Blank optional fields are OMITTED (not sent as null),
 * so on update the server preserves the stored value — this is what keeps secrets
 * (clientSecret / refreshToken / blank custom-header values) intact when left blank.
 */
function buildRequest(): EgressKeyRequest {
  const req: EgressKeyRequest = {
    id: editingId.value,
    host: host.value.trim(),
    kind: kind.value,
  };

  if (kind.value === 'custom-headers') {
    req.headers = headerRows.value
      .filter((r) => r.name.trim().length > 0)
      .map((r) => ({ name: r.name.trim(), value: r.value }));
  } else {
    req.headerName = headerName.value.trim() || 'Authorization';
    req.scopes = parseScopes(scopesText.value);

    const te = tokenEndpoint.value.trim();
    if (te) req.tokenEndpoint = te;

    const ci = clientId.value.trim();
    if (ci) req.clientId = ci;

    if (clientSecret.value.length > 0) req.clientSecret = clientSecret.value;

    if (kind.value === 'refresh-token' && refreshToken.value.length > 0) {
      req.refreshToken = refreshToken.value;
    }
  }

  return req;
}

async function handleSave(): Promise<void> {
  hostError.value = '';
  serverError.value = '';

  if (!host.value.trim()) {
    hostError.value = 'Host is required';
    return;
  }

  saving.value = true;
  try {
    await saveEgressKey(buildRequest());
    editorOpen.value = false;
    resetForm();
  } catch (e) {
    serverError.value = e instanceof Error ? e.message : 'Failed to save egress key';
  } finally {
    saving.value = false;
  }
}

async function handleDelete(id: string): Promise<void> {
  serverError.value = '';
  try {
    await removeEgressKey(id);
    // If we were editing the deleted key, drop the editor.
    if (editingId.value === id) {
      cancelEditor();
    }
  } catch (e) {
    serverError.value = e instanceof Error ? e.message : 'Failed to delete egress key';
  }
}

// --- Modal shell ------------------------------------------------------------
function handleClose(): void {
  emit('close');
}

function handleBackdropClick(event: MouseEvent): void {
  if (event.target === event.currentTarget) {
    handleClose();
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    handleClose();
  }
}

onMounted(() => {
  document.addEventListener('keydown', handleKeydown);
  loadEgressKeys();
  // Honour a programmatic open (openEgressDialog(host)): start in create mode with
  // the host prefilled.
  const requestState = egressDialogRequest.value;
  if (requestState.open) {
    startCreate(requestState.prefillHost);
  }
});

onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown));
</script>

<template>
  <div class="modal-backdrop" data-testid="egress-auth-modal" @click="handleBackdropClick">
    <div class="modal-container">
      <div class="modal-header">
        <h2 class="modal-title">Egress Auth Keys</h2>
        <button
          class="close-btn"
          data-testid="egress-auth-modal-close"
          title="Close"
          @click="handleClose"
        >
          &times;
        </button>
      </div>

      <div class="modal-content">
        <!-- Existing keys ------------------------------------------------- -->
        <section class="keys-section">
          <div class="section-head">
            <h3 class="section-title">Configured keys</h3>
            <button
              type="button"
              class="btn btn-primary"
              data-testid="egress-add-button"
              @click="startCreate()"
            >
              + Add key
            </button>
          </div>

          <p v-if="isLoading" class="muted">Loading…</p>
          <p v-else-if="egressKeys.length === 0" class="muted">
            No egress keys configured yet.
          </p>

          <ul v-else class="key-list">
            <li
              v-for="key in egressKeys"
              :key="key.id"
              class="key-item"
              data-testid="egress-key-item"
              :data-key-id="key.id"
            >
              <div class="key-main">
                <span class="key-host">{{ key.host }}</span>
                <span class="kind-badge">{{ kindLabel(key.kind) }}</span>
              </div>
              <div class="key-meta">
                <span v-if="key.headerNames.length" class="meta-line">
                  Headers: {{ key.headerNames.join(', ') }}
                </span>
                <span v-else-if="key.headerName" class="meta-line">
                  Header: {{ key.headerName }}
                </span>
                <span v-if="key.scopes.length" class="meta-line">
                  Scopes: {{ key.scopes.join(' ') }}
                </span>
                <span v-if="key.hasClientSecret" class="indicator">client secret set</span>
                <span v-if="key.hasRefreshToken" class="indicator">refresh token set</span>
              </div>
              <div class="key-actions">
                <button
                  type="button"
                  class="btn btn-secondary btn-sm"
                  @click="startEdit(key)"
                >
                  Edit
                </button>
                <button
                  type="button"
                  class="btn btn-danger btn-sm"
                  data-testid="egress-delete-button"
                  @click="handleDelete(key.id)"
                >
                  Delete
                </button>
              </div>
            </li>
          </ul>
        </section>

        <!-- Editor -------------------------------------------------------- -->
        <section v-if="editorOpen" class="editor-section">
          <h3 class="section-title">{{ editorTitle }}</h3>

          <form class="editor-form" @submit.prevent="handleSave">
            <div class="form-group">
              <label class="form-label" for="egress-kind">Kind</label>
              <select
                id="egress-kind"
                v-model="kind"
                class="form-input"
                data-testid="egress-kind-select"
                :disabled="saving"
              >
                <option v-for="opt in KIND_OPTIONS" :key="opt.value" :value="opt.value">
                  {{ opt.label }}
                </option>
              </select>
            </div>

            <div class="form-group">
              <label class="form-label" for="egress-host">
                Host <span class="required">*</span>
              </label>
              <input
                id="egress-host"
                v-model="host"
                type="text"
                class="form-input"
                :class="{ error: hostError }"
                data-testid="egress-host-input"
                placeholder="api.example.com"
                :disabled="saving"
              />
              <span v-if="hostError" class="error-message">{{ hostError }}</span>
            </div>

            <!-- Custom headers -->
            <template v-if="!isOAuth">
              <div class="form-group">
                <label class="form-label">Headers</label>
                <div
                  v-for="(row, index) in headerRows"
                  :key="index"
                  class="header-row"
                >
                  <input
                    v-model="row.name"
                    type="text"
                    class="form-input header-name"
                    data-testid="egress-header-name-input"
                    placeholder="Header name (e.g. Authorization)"
                    :disabled="saving"
                  />
                  <input
                    v-model="row.value"
                    type="text"
                    class="form-input header-value"
                    data-testid="egress-header-value-input"
                    placeholder="Header value"
                    :disabled="saving"
                  />
                  <button
                    type="button"
                    class="btn btn-secondary btn-sm"
                    :disabled="headerRows.length <= 1 || saving"
                    @click="removeHeaderRow(index)"
                  >
                    &times;
                  </button>
                </div>
                <button
                  type="button"
                  class="btn btn-secondary btn-sm add-header-btn"
                  data-testid="egress-add-header-row"
                  :disabled="saving"
                  @click="addHeaderRow"
                >
                  + Add header
                </button>
                <span v-if="isEditing" class="hint">Leave a value blank to keep the current secret.</span>
              </div>
            </template>

            <!-- OAuth kinds -->
            <template v-else>
              <div class="form-group">
                <label class="form-label" for="egress-header-name">Header name</label>
                <input
                  id="egress-header-name"
                  v-model="headerName"
                  type="text"
                  class="form-input"
                  placeholder="Authorization"
                  :disabled="saving"
                />
              </div>

              <div class="form-group">
                <label class="form-label" for="egress-token-endpoint">Token endpoint</label>
                <input
                  id="egress-token-endpoint"
                  v-model="tokenEndpoint"
                  type="text"
                  class="form-input"
                  placeholder="https://auth.example.com/oauth/token"
                  :disabled="saving"
                />
                <span v-if="isEditing" class="hint">Leave blank to keep current.</span>
              </div>

              <div class="form-group">
                <label class="form-label" for="egress-client-id">Client id</label>
                <input
                  id="egress-client-id"
                  v-model="clientId"
                  type="text"
                  class="form-input"
                  placeholder="client id"
                  :disabled="saving"
                />
                <span v-if="isEditing" class="hint">Leave blank to keep current.</span>
              </div>

              <div class="form-group">
                <label class="form-label" for="egress-client-secret">Client secret</label>
                <input
                  id="egress-client-secret"
                  v-model="clientSecret"
                  type="password"
                  class="form-input"
                  placeholder="client secret"
                  autocomplete="off"
                  :disabled="saving"
                />
                <span v-if="isEditing" class="hint">Leave blank to keep current.</span>
              </div>

              <div v-if="kind === 'refresh-token'" class="form-group">
                <label class="form-label" for="egress-refresh-token">Refresh token</label>
                <input
                  id="egress-refresh-token"
                  v-model="refreshToken"
                  type="password"
                  class="form-input"
                  placeholder="refresh token"
                  autocomplete="off"
                  :disabled="saving"
                />
                <span v-if="isEditing" class="hint">Leave blank to keep current.</span>
              </div>

              <div class="form-group">
                <label class="form-label" for="egress-scopes">Scopes</label>
                <input
                  id="egress-scopes"
                  v-model="scopesText"
                  type="text"
                  class="form-input"
                  placeholder="space- or comma-separated scopes"
                  :disabled="saving"
                />
              </div>
            </template>

            <span v-if="serverError" class="error-message" data-testid="egress-error">
              {{ serverError }}
            </span>

            <div class="form-actions">
              <button
                type="button"
                class="btn btn-secondary"
                :disabled="saving"
                @click="cancelEditor"
              >
                Cancel
              </button>
              <button
                type="submit"
                class="btn btn-primary"
                data-testid="egress-save-button"
                :disabled="saving"
              >
                {{ saving ? 'Saving…' : 'Save' }}
              </button>
            </div>
          </form>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.modal-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 20px;
}

.modal-container {
  background: white;
  border-radius: 12px;
  box-shadow: 0 20px 50px rgba(0, 0, 0, 0.2);
  width: 100%;
  max-width: 640px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px;
  border-bottom: 1px solid #eee;
}

.modal-title {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: #333;
}

.close-btn {
  width: 32px;
  height: 32px;
  padding: 0;
  background: transparent;
  border: none;
  border-radius: 4px;
  font-size: 24px;
  line-height: 1;
  color: #666;
  cursor: pointer;
  transition: background 0.2s, color 0.2s;
}

.close-btn:hover {
  background: #f8f9fa;
  color: #333;
}

.modal-content {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
}

.section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}

.section-title {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
  color: #333;
}

.muted {
  color: #888;
  font-size: 14px;
  margin: 8px 0;
}

.key-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.key-item {
  border: 1px solid #e0e0e0;
  border-radius: 8px;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.key-main {
  display: flex;
  align-items: center;
  gap: 10px;
}

.key-host {
  font-weight: 600;
  color: #333;
  word-break: break-all;
}

.kind-badge {
  font-size: 12px;
  padding: 2px 8px;
  border-radius: 999px;
  background: #e7f1ff;
  color: #0b5ed7;
  white-space: nowrap;
}

.key-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  font-size: 12px;
  color: #666;
}

.indicator {
  color: #198754;
}

.key-actions {
  display: flex;
  gap: 8px;
  margin-top: 4px;
}

.editor-section {
  margin-top: 24px;
  padding-top: 20px;
  border-top: 1px solid #eee;
}

.editor-form {
  display: flex;
  flex-direction: column;
  gap: 16px;
  margin-top: 12px;
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

.form-input {
  padding: 10px 12px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
  font-family: inherit;
  transition: border-color 0.2s, box-shadow 0.2s;
}

.form-input:focus {
  outline: none;
  border-color: #0d6efd;
  box-shadow: 0 0 0 2px rgba(13, 110, 253, 0.25);
}

.form-input.error {
  border-color: #dc3545;
}

.header-row {
  display: flex;
  gap: 8px;
  margin-bottom: 8px;
}

.header-name {
  flex: 1;
}

.header-value {
  flex: 1.4;
}

.add-header-btn {
  align-self: flex-start;
}

.hint {
  font-size: 12px;
  color: #888;
}

.error-message {
  font-size: 12px;
  color: #dc3545;
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

.btn-sm {
  padding: 6px 12px;
  font-size: 13px;
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

.btn-danger {
  background: #dc3545;
  color: white;
}

.btn-danger:hover:not(:disabled) {
  background: #c82333;
}
</style>
