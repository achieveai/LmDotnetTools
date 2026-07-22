<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, watch, nextTick, computed } from 'vue';
import { useFileBrowser } from '@/composables/useFileBrowser';
import type { FileEntry, UploadItem } from '@/types/fileBrowser';
import { filesFromDirectoryInput, resolveDrop, isDirectoryPickerSupported } from '@/utils/folderUpload';

const props = defineProps<{ threadId: string | null }>();

const {
  entries,
  breadcrumbs,
  moreCount,
  workspaceId,
  isLoading,
  error,
  noSession,
  uploadProgress,
  isUploading,
  previewTarget,
  previewResult,
  load,
  navigateTo,
  preview,
  clearPreview,
  download,
  upload,
  uploadFolder,
  createDirectory,
  remove,
  cleanup,
} = useFileBrowser(() => props.threadId);

// Whether the browser supports the `webkitdirectory` folder picker. When false, the "Upload folder"
// affordance is disabled with a hint; the flat file picker and drag-drop keep working.
const folderPickerSupported = isDirectoryPickerSupported();

// Fixed-height, internally-scrolling list panel: a flex child that keeps a STABLE height regardless of
// the file count (so the modal body no longer grows/shrinks as files come and go). The responsive
// height sits inline (with a ceiling via the scoped `.fb-list` rule) so it fits inside BaseModal's 90vh.
const listScrollStyle = { minHeight: '0', overflowY: 'auto', height: '55vh' } as const;

// The entry pending delete confirmation (null when no dialog is open).
const deleteTarget = ref<FileEntry | null>(null);
const cancelBtnRef = ref<HTMLButtonElement | null>(null);
// Files awaiting an advisory overwrite confirmation (their names collide with existing files).
const pendingUpload = ref<{ files: File[]; colliding: string[] } | null>(null);
const overwriteKeepBtnRef = ref<HTMLButtonElement | null>(null);
const fileInputRef = ref<HTMLInputElement | null>(null);
const folderInputRef = ref<HTMLInputElement | null>(null);
const isDragOver = ref(false);
// Concise summary of the files a batch upload REJECTED (per-file 413/400/409 target_busy), shown as
// its own notice. `null` when the last upload had no per-file failures.
const uploadErrors = ref<string | null>(null);
// Neutral per-batch outcome for a FOLDER upload ("Uploaded X of Y file(s)."). Flat uploads keep their
// existing behavior (failures only). `null` when no folder upload has completed.
const uploadSummary = ref<string | null>(null);

// New-folder name-entry dialog state.
const newFolderOpen = ref(false);
const newFolderName = ref('');
const newFolderInputRef = ref<HTMLInputElement | null>(null);
const newFolderNameTrimmed = computed(() => newFolderName.value.trim());

// True while a modal sub-dialog (delete confirm, overwrite confirm, or new-folder entry) is open. The
// background file-browser controls are marked `inert` so keyboard focus (BaseModal's trap skips [inert]
// subtrees) and pointer interaction stay confined to the dialog — otherwise Tab would reach
// breadcrumbs/upload/row buttons behind the overlay and activating another control would retarget it.
const isConfirmOpen = computed(
  () => deleteTarget.value !== null || pendingUpload.value !== null || newFolderOpen.value
);

onMounted(() => {
  void load('');
});

onBeforeUnmount(() => cleanup());

// Re-load from the root whenever the conversation changes.
watch(
  () => props.threadId,
  () => {
    clearPreview();
    void load('');
  }
);

function isNavigable(entry: FileEntry): boolean {
  return entry.type === 'directory' && !entry.nameLossy;
}

function canPreview(entry: FileEntry): boolean {
  return entry.type === 'file' && !entry.nameLossy;
}

function canDownload(entry: FileEntry): boolean {
  return entry.type === 'file' && !entry.nameLossy;
}

function canDelete(entry: FileEntry): boolean {
  return entry.type !== 'symlink' && !entry.nameLossy;
}

function onRowClick(entry: FileEntry): void {
  if (isNavigable(entry)) {
    void navigateTo(joinCurrent(entry.name));
  }
}

/** Directory navigation target: the entry name joined onto the current breadcrumb path. */
function joinCurrent(name: string): string {
  const current = breadcrumbs.value[breadcrumbs.value.length - 1]?.path ?? '';
  return current ? `${current}/${name}` : name;
}

function onPreview(entry: FileEntry): void {
  if (previewTarget.value?.name === entry.name) {
    clearPreview();
    return;
  }
  void preview(entry);
}

function onDownload(entry: FileEntry): void {
  void download(entry);
}

function askDelete(entry: FileEntry): void {
  deleteTarget.value = entry;
}

function cancelDelete(): void {
  deleteTarget.value = null;
}

async function confirmDelete(): Promise<void> {
  const target = deleteTarget.value;
  deleteTarget.value = null;
  if (target) {
    await remove(target);
  }
}

// When the confirm dialog opens, move focus to the (safe) Cancel button.
watch(deleteTarget, async (target) => {
  if (target) {
    await nextTick();
    cancelBtnRef.value?.focus();
  }
});

// When the overwrite-confirm opens, move focus to its (safe) "Skip existing" button.
watch(pendingUpload, async (pending) => {
  if (pending) {
    await nextTick();
    overwriteKeepBtnRef.value?.focus();
  }
});

function onFilesPicked(event: Event): void {
  const input = event.target as HTMLInputElement;
  const files = input.files ? Array.from(input.files) : [];
  // Reset so picking the same file again re-triggers change.
  input.value = '';
  // Ignore new picks while a batch is uploading (single-flight; buttons are also disabled).
  if (isUploading.value) {
    return;
  }
  if (files.length > 0) {
    handleUpload(files);
  }
}

/** Folder picker (`webkitdirectory`): each file carries its `webkitRelativePath`, so upload as a tree. */
function onFolderPicked(event: Event): void {
  const input = event.target as HTMLInputElement;
  const files = input.files ? Array.from(input.files) : [];
  input.value = '';
  if (isUploading.value) {
    return;
  }
  const result = filesFromDirectoryInput(files);
  if (result.kind === 'over-limit') {
    reportOverLimit(result.limit);
    return;
  }
  if (result.items.length > 0) {
    void doFolderUpload(result.items);
  }
}

/**
 * A drop is SPLIT into a flat file group (loose top-level files — kept on today's path WITH the basename
 * overwrite preflight) and a directory tree group (folder upload, no preflight, relative paths preserved);
 * a mixed drop runs BOTH. A tree exceeding the shared file cap rejects the whole drop.
 */
async function onDrop(event: DragEvent): Promise<void> {
  isDragOver.value = false;
  // Ignore drops while a batch is uploading (single-flight).
  if (!event.dataTransfer || isUploading.value) {
    return;
  }
  const result = await resolveDrop(event.dataTransfer);
  if (result.kind === 'over-limit') {
    reportOverLimit(result.limit);
    return;
  }
  // Loose files → the flat overwrite preflight; directories → folder upload (no preflight).
  if (result.files.length > 0) {
    handleUpload(result.files);
  }
  if (result.items.length > 0) {
    void doFolderUpload(result.items);
  }
}

/**
 * Rejects an over-limit folder selection (drop or picker): nothing is uploaded and a visible error is
 * surfaced via the upload-errors notice. Shared by both entry points so the policy is identical.
 */
function reportOverLimit(limit: number): void {
  uploadSummary.value = null;
  uploadErrors.value =
    `Too many files: a folder upload is limited to ${limit} files. ` +
    'Nothing was uploaded — choose a smaller folder.';
}

/**
 * Entry point for an upload batch. If any picked file's basename collides with an existing (non-lossy)
 * file in the current directory, an advisory overwrite confirmation is shown FIRST; otherwise the batch
 * uploads immediately. The server performs an atomic last-writer-wins replacement on confirm.
 */
function handleUpload(files: File[]): void {
  const existing = new Set(
    entries.value.filter((entry) => entry.type === 'file' && !entry.nameLossy).map((entry) => entry.name)
  );
  const colliding = files.filter((file) => existing.has(file.name)).map((file) => file.name);
  if (colliding.length > 0) {
    pendingUpload.value = { files, colliding };
    return;
  }
  void doUpload(files);
}

/** Overwrite confirmed: upload the whole batch (colliding files are replaced, last-writer-wins). */
async function confirmOverwrite(): Promise<void> {
  const pending = pendingUpload.value;
  pendingUpload.value = null;
  if (pending) {
    await doUpload(pending.files);
  }
}

/** Overwrite declined: skip the colliding files and upload only the non-colliding ones (per-file independence). */
function cancelOverwrite(): void {
  const pending = pendingUpload.value;
  pendingUpload.value = null;
  if (pending) {
    const collidingSet = new Set(pending.colliding);
    const safe = pending.files.filter((file) => !collidingSet.has(file.name));
    if (safe.length > 0) {
      void doUpload(safe);
    }
  }
}

/**
 * Uploads a batch and surfaces the per-file failures. `upload()` already sets `error` for
 * session-level rejections and reloads the listing; here we additionally report the individual
 * files the server rejected (413 file_too_large, 400 invalid_file_name, 409 target_busy) so a
 * rejected file no longer vanishes silently.
 */
async function doUpload(files: File[]): Promise<void> {
  uploadErrors.value = null;
  uploadSummary.value = null;
  reportOutcomes(await upload(files));
}

/**
 * Uploads a folder / relative-path batch (picker or directory drop). Unlike the flat path, there is NO
 * basename overwrite preflight (it would mis-collide `a/readme.md` vs `b/readme.md`); the actual
 * per-file server outcomes are reported instead, plus a neutral "Uploaded X of Y" summary.
 */
async function doFolderUpload(items: UploadItem[]): Promise<void> {
  uploadErrors.value = null;
  uploadSummary.value = null;
  const outcomes = await uploadFolder(items);
  const failedCount = reportOutcomes(outcomes);
  uploadSummary.value = `Uploaded ${outcomes.length - failedCount} of ${outcomes.length} file(s).`;
}

/** Surfaces the per-file failures of a batch as the upload-errors notice; returns the failure count. */
function reportOutcomes(outcomes: { name: string; success: boolean; error?: string }[]): number {
  const failed = outcomes.filter((outcome) => !outcome.success);
  if (failed.length > 0) {
    const detail = failed
      .map((outcome) => `${outcome.name} (${outcome.error ?? 'upload_failed'})`)
      .join(', ');
    uploadErrors.value = `${failed.length} file(s) failed: ${detail}`;
  }
  return failed.length;
}

/** Opens the new-folder name-entry dialog with a blank name. */
function openNewFolder(): void {
  newFolderName.value = '';
  newFolderOpen.value = true;
}

function cancelNewFolder(): void {
  newFolderOpen.value = false;
}

/** Confirms the new-folder dialog: creates the directory (server errors surface via the error notice). */
async function confirmNewFolder(): Promise<void> {
  const name = newFolderNameTrimmed.value;
  if (!name) {
    return;
  }
  newFolderOpen.value = false;
  await createDirectory(name);
}

// When the new-folder dialog opens, move focus to its name input.
watch(newFolderOpen, async (open) => {
  if (open) {
    await nextTick();
    newFolderInputRef.value?.focus();
  }
});

function formatSize(size: number | null): string {
  if (size === null) {
    return '';
  }
  if (size < 1024) {
    return `${size} B`;
  }
  if (size < 1024 * 1024) {
    return `${(size / 1024).toFixed(1)} KB`;
  }
  return `${(size / (1024 * 1024)).toFixed(1)} MB`;
}

function typeIcon(entry: FileEntry): string {
  if (entry.type === 'directory') {
    return '📁';
  }
  if (entry.type === 'symlink') {
    return '🔗';
  }
  return '📄';
}
</script>

<template>
  <div class="file-browser" data-testid="file-browser">
    <!-- Background content: made inert while a destructive confirmation is open so focus + pointer stay
         confined to the confirmation overlay below (which sits OUTSIDE this inert subtree). -->
    <div class="fb-main" :inert="isConfirmOpen || undefined">
      <!-- The active workspace (shared by any conversation with the same workspace/identity). -->
      <div v-if="workspaceId" class="fb-workspace" data-testid="file-browser-workspace">
      Workspace: <span class="fb-workspace-id">{{ workspaceId }}</span>
    </div>

    <!-- No sandbox session yet: the conversation hasn't provisioned a workspace. -->
    <div v-if="noSession" class="fb-empty" data-testid="file-browser-no-session">
      No sandbox session yet. Send a message that uses the sandbox to create one.
    </div>

    <template v-else>
      <!-- Breadcrumbs -->
      <nav class="fb-breadcrumbs" data-testid="file-browser-breadcrumb" aria-label="Breadcrumb">
        <template v-for="(crumb, idx) in breadcrumbs" :key="crumb.path">
          <button
            class="fb-crumb"
            :data-testid="`file-browser-crumb-${idx}`"
            @click="navigateTo(crumb.path)"
          >
            {{ crumb.name }}
          </button>
          <span v-if="idx < breadcrumbs.length - 1" class="fb-crumb-sep" aria-hidden="true">/</span>
        </template>
      </nav>

      <!-- Toolbar: directory-level actions. -->
      <div class="fb-toolbar" data-testid="file-browser-toolbar">
        <button
          class="fb-toolbar-btn"
          data-testid="file-browser-new-folder"
          @click="openNewFolder"
        >
          New folder
        </button>
      </div>

      <!-- Upload: drag-and-drop zone + flat file picker + folder picker. -->
      <div
        class="fb-dropzone"
        :class="{ 'fb-dropzone-active': isDragOver }"
        data-testid="file-browser-dropzone"
        @dragover.prevent="isDragOver = true"
        @dragleave.prevent="isDragOver = false"
        @drop.prevent="onDrop"
      >
        <span>Drag files or a folder here to upload, or</span>
        <button
          class="fb-upload-btn"
          data-testid="file-browser-upload"
          :disabled="isUploading"
          @click="fileInputRef?.click()"
        >
          Choose files
        </button>
        <button
          class="fb-upload-btn"
          data-testid="file-browser-folder-upload"
          :disabled="!folderPickerSupported || isUploading"
          @click="folderInputRef?.click()"
        >
          Upload folder
        </button>
        <input
          ref="fileInputRef"
          class="fb-file-input"
          type="file"
          multiple
          data-testid="file-browser-file-input"
          @change="onFilesPicked"
        />
        <!-- Folder picker: kept in the DOM even when unsupported so the flat picker is unaffected. -->
        <input
          ref="folderInputRef"
          class="fb-file-input"
          type="file"
          multiple
          webkitdirectory
          data-testid="file-browser-folder-input"
          @change="onFolderPicked"
        />
        <span
          v-if="!folderPickerSupported"
          class="fb-folder-unsupported"
          data-testid="file-browser-folder-unsupported"
        >
          Folder upload isn’t supported by this browser — drag a folder in or use “Choose files”.
        </span>
      </div>

      <div v-if="error" class="fb-error" data-testid="file-browser-error">{{ error }}</div>

      <div
        v-if="uploadProgress"
        class="fb-progress"
        data-testid="file-browser-upload-progress"
      >
        Uploading {{ uploadProgress.completed }}/{{ uploadProgress.total }}<span
          v-if="uploadProgress.activeName"
          class="fb-progress-name"
        >
          — {{ uploadProgress.activeName }}</span
        >
      </div>

      <div
        v-if="uploadSummary"
        class="fb-summary"
        data-testid="file-browser-upload-summary"
      >
        {{ uploadSummary }}
      </div>

      <div
        v-if="uploadErrors"
        class="fb-error"
        data-testid="file-browser-upload-errors"
      >
        {{ uploadErrors }}
      </div>

      <!-- ONE fixed-height, internally-scrolling container that stays mounted in EVERY state (loading,
           empty, populated) so the panel never collapses to a one-line div and re-expands on refresh. -->
      <ul class="fb-list" :style="listScrollStyle" data-testid="file-browser-list">
        <li v-if="isLoading" class="fb-loading" data-testid="file-browser-loading">Loading…</li>
        <li v-else-if="entries.length === 0" class="fb-empty" data-testid="file-browser-empty">
          This directory is empty.
        </li>
        <template v-else>
        <li
          v-for="entry in entries"
          :key="entry.name"
          class="fb-row"
          :class="{ 'fb-row-symlink': entry.type === 'symlink', 'fb-row-lossy': entry.nameLossy }"
          :data-testid="`file-entry-${entry.name}`"
        >
          <button
            class="fb-name"
            :class="{ 'fb-name-dir': isNavigable(entry) }"
            :disabled="!isNavigable(entry)"
            :data-testid="`file-entry-name-${entry.name}`"
            @click="onRowClick(entry)"
          >
            <span class="fb-icon" aria-hidden="true">{{ typeIcon(entry) }}</span>
            <span class="fb-label">{{ entry.name }}</span>
          </button>

          <span
            v-if="entry.nameLossy"
            class="fb-badge"
            :data-testid="`file-entry-lossy-${entry.name}`"
            title="Name could not be decoded as UTF-8; actions are disabled."
          >
            unreadable name
          </span>
          <span v-else-if="entry.type === 'symlink'" class="fb-badge fb-badge-symlink">symlink</span>

          <span class="fb-size">{{ formatSize(entry.size) }}</span>

          <span class="fb-actions">
            <button
              v-if="canPreview(entry)"
              class="fb-action"
              :data-testid="`file-entry-preview-${entry.name}`"
              @click="onPreview(entry)"
            >
              Preview
            </button>
            <button
              v-if="canDownload(entry)"
              class="fb-action"
              :data-testid="`file-entry-download-${entry.name}`"
              @click="onDownload(entry)"
            >
              Download
            </button>
            <button
              v-if="canDelete(entry)"
              class="fb-action fb-action-danger"
              :data-testid="`file-entry-delete-${entry.name}`"
              @click="askDelete(entry)"
            >
              Delete
            </button>
          </span>

          <!-- Inline preview panel for this entry -->
          <div
            v-if="previewTarget?.name === entry.name && previewResult"
            class="fb-preview"
            :data-testid="`file-preview-${entry.name}`"
          >
            <div v-if="previewResult.previewable && previewResult.text !== undefined" class="fb-preview-body">
              <pre class="file-preview" data-testid="file-preview-text">{{ previewResult.text }}</pre>
            </div>
            <div v-else class="fb-preview-unavailable" data-testid="file-preview-unavailable">
              Preview unavailable<span v-if="previewResult.reason"> ({{ previewResult.reason }})</span>.
            </div>
          </div>
        </li>
        </template>
      </ul>

      <!-- Row-cap notice: entries beyond the server cap were not returned. -->
      <div v-if="moreCount > 0" class="fb-more" data-testid="file-browser-more">
        {{ moreCount }} more item{{ moreCount === 1 ? '' : 's' }} not shown.
      </div>
    </template>
    </div>

    <!-- Delete confirmation dialog -->
    <div
      v-if="deleteTarget"
      class="fb-confirm-backdrop"
      data-testid="file-browser-delete-confirm"
      @click.self="cancelDelete"
      @keydown.esc.stop.prevent="cancelDelete"
    >
      <div class="fb-confirm" role="dialog" aria-modal="true" aria-labelledby="fb-confirm-title">
        <p id="fb-confirm-title" class="fb-confirm-text">
          <template v-if="deleteTarget.type === 'directory'">
            Delete folder {{ deleteTarget.name }} and all its contents?
          </template>
          <template v-else> Delete file {{ deleteTarget.name }}? </template>
        </p>
        <div class="fb-confirm-actions">
          <button
            ref="cancelBtnRef"
            class="fb-action"
            data-testid="file-browser-delete-cancel"
            @click="cancelDelete"
          >
            Cancel
          </button>
          <button
            class="fb-action fb-action-danger"
            data-testid="file-browser-delete-confirm-btn"
            @click="confirmDelete"
          >
            Delete
          </button>
        </div>
      </div>
    </div>

    <!-- Advisory overwrite confirmation: one or more uploaded names collide with existing files. -->
    <div
      v-if="pendingUpload"
      class="fb-confirm-backdrop"
      data-testid="file-browser-overwrite-confirm"
      @click.self="cancelOverwrite"
      @keydown.esc.stop.prevent="cancelOverwrite"
    >
      <div class="fb-confirm" role="dialog" aria-modal="true" aria-labelledby="fb-overwrite-title">
        <p id="fb-overwrite-title" class="fb-confirm-text">
          {{ pendingUpload.colliding.length }} file{{ pendingUpload.colliding.length === 1 ? '' : 's' }}
          already exist and will be overwritten: {{ pendingUpload.colliding.join(', ') }}. Continue?
        </p>
        <div class="fb-confirm-actions">
          <button
            ref="overwriteKeepBtnRef"
            class="fb-action"
            data-testid="file-browser-overwrite-cancel"
            @click="cancelOverwrite"
          >
            Skip existing
          </button>
          <button
            class="fb-action fb-action-danger"
            data-testid="file-browser-overwrite-confirm-btn"
            @click="confirmOverwrite"
          >
            Overwrite
          </button>
        </div>
      </div>
    </div>

    <!-- New-folder name-entry dialog. -->
    <div
      v-if="newFolderOpen"
      class="fb-confirm-backdrop"
      data-testid="file-browser-new-folder-dialog"
      @click.self="cancelNewFolder"
      @keydown.esc.stop.prevent="cancelNewFolder"
    >
      <div class="fb-confirm" role="dialog" aria-modal="true" aria-labelledby="fb-new-folder-title">
        <p id="fb-new-folder-title" class="fb-confirm-text">New folder</p>
        <input
          ref="newFolderInputRef"
          v-model="newFolderName"
          class="fb-new-folder-input"
          type="text"
          placeholder="Folder name"
          data-testid="file-browser-new-folder-input"
          @keydown.enter.prevent="confirmNewFolder"
        />
        <div class="fb-confirm-actions">
          <button
            class="fb-action"
            data-testid="file-browser-new-folder-cancel"
            @click="cancelNewFolder"
          >
            Cancel
          </button>
          <button
            class="fb-action fb-action-primary"
            :disabled="!newFolderNameTrimmed"
            data-testid="file-browser-new-folder-confirm"
            @click="confirmNewFolder"
          >
            Create
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.file-browser {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px 20px;
}

/* The interactive content behind any confirmation overlay. Carries the column layout so the fixed-
   position confirmations can sit outside it (as siblings) and toggle its `inert` state. */
.fb-main {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.fb-workspace {
  font-size: 12px;
  color: #666;
}

.fb-workspace-id {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  color: #333;
}

.fb-breadcrumbs {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
  font-size: 14px;
}

.fb-crumb {
  background: none;
  border: none;
  color: #2d6cdf;
  cursor: pointer;
  padding: 2px 4px;
  font-size: 14px;
}

.fb-crumb:hover {
  text-decoration: underline;
}

.fb-crumb-sep {
  color: #999;
}

.fb-toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.fb-toolbar-btn {
  padding: 4px 12px;
  background: #f0f0f0;
  border: 1px solid #ddd;
  border-radius: 6px;
  font-size: 13px;
  cursor: pointer;
}

.fb-toolbar-btn:hover {
  background: #e6e6e6;
}

.fb-dropzone {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  padding: 12px;
  border: 1px dashed #ccc;
  border-radius: 8px;
  color: #666;
  font-size: 13px;
}

.fb-dropzone-active {
  border-color: #2d6cdf;
  background: #f0f6ff;
}

.fb-upload-btn {
  padding: 4px 12px;
  background: #2d6cdf;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 13px;
  cursor: pointer;
}

.fb-upload-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.fb-folder-unsupported {
  color: #999;
  font-size: 12px;
}

.fb-file-input {
  display: none;
}

.fb-error {
  padding: 8px 12px;
  background: #f8d7da;
  color: #721c24;
  border-radius: 6px;
  font-size: 13px;
  /* Long relative-path lists (folder uploads) must wrap, never blow out horizontally. */
  word-break: break-word;
  overflow-wrap: anywhere;
}

.fb-progress {
  padding: 8px 12px;
  background: #e7f1ff;
  color: #1c4a8a;
  border-radius: 6px;
  font-size: 13px;
  word-break: break-word;
  overflow-wrap: anywhere;
}

.fb-progress-name {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
}

.fb-summary {
  color: #4a7a4a;
  font-size: 13px;
}

.fb-loading,
.fb-empty {
  color: #666;
  font-size: 14px;
  padding: 8px 0;
}

/* Stable-height, internally-scrolling list panel. The responsive height + overflow + min-height:0 come
   from the inline `listScrollStyle`; this rule caps it on very tall viewports so it stays within 90vh. */
.fb-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  max-height: 620px;
}

.fb-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  padding: 6px 4px;
  border-bottom: 1px solid #f0f0f0;
}

.fb-row-symlink {
  font-style: italic;
  color: #777;
}

.fb-row-lossy {
  opacity: 0.7;
}

.fb-name {
  display: flex;
  align-items: center;
  gap: 6px;
  background: none;
  border: none;
  padding: 2px 4px;
  font-size: 14px;
  color: #333;
  cursor: default;
  text-align: left;
  /* Allow the flex child to shrink so a long name ellipsizes instead of overflowing the row. */
  min-width: 0;
}

.fb-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fb-name-dir {
  color: #2d6cdf;
  cursor: pointer;
}

.fb-name-dir:hover {
  text-decoration: underline;
}

.fb-name:disabled {
  cursor: default;
}

.fb-badge {
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 10px;
  background: #ffe0b2;
  color: #8a5a00;
}

.fb-badge-symlink {
  background: #e0e0e0;
  color: #555;
}

.fb-size {
  margin-left: auto;
  color: #999;
  font-size: 12px;
  min-width: 60px;
  text-align: right;
}

.fb-actions {
  display: flex;
  gap: 4px;
}

.fb-action {
  padding: 2px 8px;
  background: #f0f0f0;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 12px;
  cursor: pointer;
}

.fb-action:hover {
  background: #e6e6e6;
}

.fb-action-danger {
  color: #c82333;
  border-color: #f1b0b7;
}

.fb-preview {
  flex-basis: 100%;
  width: 100%;
}

.file-preview {
  margin: 6px 0 0;
  padding: 10px 12px;
  background: #1e1e1e;
  color: #e6e6e6;
  border-radius: 6px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 320px;
  overflow: auto;
}

.fb-preview-unavailable {
  margin-top: 6px;
  color: #999;
  font-size: 13px;
  font-style: italic;
}

.fb-more {
  color: #888;
  font-size: 13px;
  font-style: italic;
  padding: 4px 0;
}

.fb-confirm-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.4);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1100;
}

.fb-confirm {
  background: white;
  border-radius: 10px;
  padding: 20px;
  max-width: 380px;
  box-shadow: 0 12px 32px rgba(0, 0, 0, 0.2);
}

.fb-confirm-text {
  margin: 0 0 16px;
  font-size: 15px;
  color: #333;
}

.fb-confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.fb-new-folder-input {
  width: 100%;
  box-sizing: border-box;
  margin: 0 0 16px;
  padding: 8px 10px;
  border: 1px solid #ccc;
  border-radius: 6px;
  font-size: 14px;
}

.fb-action-primary {
  background: #2d6cdf;
  color: white;
  border-color: #2d6cdf;
}

.fb-action-primary:hover:not(:disabled) {
  background: #245ac0;
}

.fb-action-primary:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
