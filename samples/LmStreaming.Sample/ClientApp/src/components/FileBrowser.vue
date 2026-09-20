<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, watch, nextTick, computed } from 'vue';
import { useFileBrowser } from '@/composables/useFileBrowser';
import type { FileEntry, UploadItem } from '@/types/fileBrowser';
import { filesFromDirectoryInput, resolveDrop, isDirectoryPickerSupported } from '@/utils/folderUpload';

const props = defineProps<{ threadId: string | null; embedded?: boolean }>();

/**
 * Opening a file is NOT this component's job any more: it names the file and the host (the right
 * panel) opens it as a tab in the shared preview region. The browser therefore no longer fetches
 * `/preview` at all — one preview surface, not two.
 */
const emit = defineEmits<{ openFile: [path: string] }>();

const {
  entries,
  breadcrumbs,
  moreCount,
  workspaceId,
  isLoading,
  error,
  noSession,
  uploadProgress,
  uploadBusy,
  setOverwritePending,
  load,
  navigateTo,
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
// the file count (so the panel body no longer grows/shrinks as files come and go). The responsive
// height sits inline (with a ceiling via the scoped `.fb-list` rule) so the section stays compact
// inside a 320–640px inspector column without swallowing Work and Agents below it.
const listScrollStyle = { minHeight: '0', overflowY: 'auto', height: '40vh' } as const;

// The entry pending delete confirmation (null when no dialog is open).
const deleteTarget = ref<FileEntry | null>(null);
const cancelBtnRef = ref<HTMLButtonElement | null>(null);
// Files awaiting an advisory overwrite confirmation (their names collide with existing files).
const pendingUpload = ref<{ files: File[]; colliding: string[] } | null>(null);
const overwriteKeepBtnRef = ref<HTMLButtonElement | null>(null);

/**
 * Opens/closes the overwrite confirmation, keeping the composable's `uploadBusy` admission barrier in
 * lockstep so a folder pick/drop cannot start a batch while the user is still deciding (which would
 * mutate the directory and make the pending decision stale).
 */
function setPendingUpload(pending: { files: File[]; colliding: string[] } | null): void {
  pendingUpload.value = pending;
  setOverwritePending(pending !== null);
}
const fileInputRef = ref<HTMLInputElement | null>(null);
const folderInputRef = ref<HTMLInputElement | null>(null);
const isDragOver = ref(false);
// Concise summary of the files a batch upload REJECTED (per-file 413/400/409 target_busy), shown as
// its own notice. `null` when the last upload had no per-file failures.
const uploadErrors = ref<string | null>(null);
// Neutral per-batch outcome for a FOLDER upload ("Uploaded X of Y file(s)."). Flat uploads keep their
// existing behavior (failures only). `null` when no folder upload has completed.
const uploadSummary = ref<string | null>(null);

// Client-side row filter over the CURRENT listing page (the server caps a listing at
// FileBrowserLimits.MaxListingRows, so there is nothing below the fold to miss). Never refetches, and
// is cleared on navigation so a new directory is never silently hidden behind a stale term.
const filterText = ref('');

// New-folder name-entry dialog state.
const newFolderOpen = ref(false);
const newFolderName = ref('');
const newFolderInputRef = ref<HTMLInputElement | null>(null);
const newFolderNameTrimmed = computed(() => newFolderName.value.trim());

// True while a modal sub-dialog (delete confirm, overwrite confirm, or new-folder entry) is open. The
// background file-browser controls are marked `inert` so keyboard focus (the inspector's trap skips
// [inert] subtrees) and pointer interaction stay confined to the dialog — otherwise Tab would reach
// breadcrumbs/upload/row buttons behind the overlay and activating another control would retarget it.
const isConfirmOpen = computed(
  () => deleteTarget.value !== null || pendingUpload.value !== null || newFolderOpen.value
);

/** Whether the conversation has an id at all. Null = an unsent New Chat: there is no workspace yet. */
const hasThread = computed(() => props.threadId !== null);

/**
 * Rows as rendered: filtered by {@link filterText}, then folders first and each group by
 * case-insensitive name. Both are client-side over the page the server already returned.
 */
const visibleEntries = computed<FileEntry[]>(() => {
  const term = filterText.value.trim().toLowerCase();
  const rows = term
    ? entries.value.filter((entry) => entry.name.toLowerCase().includes(term))
    : [...entries.value];
  const rank = (entry: FileEntry): number => (entry.type === 'directory' ? 0 : 1);
  return rows.sort(
    (a, b) => rank(a) - rank(b) || a.name.toLowerCase().localeCompare(b.name.toLowerCase())
  );
});

onMounted(() => {
  void load('');
});

onBeforeUnmount(() => cleanup());

// Re-load from the root whenever the conversation changes. A different conversation may be a
// different workspace, where the current path need not exist.
watch(
  () => props.threadId,
  () => {
    filterText.value = '';
    void load('');
  }
);

function isNavigable(entry: FileEntry): boolean {
  return entry.type === 'directory' && !entry.nameLossy;
}

/** A file the panel can open as a preview tab (the same rule the old inline preview used). */
function isOpenable(entry: FileEntry): boolean {
  return entry.type === 'file' && !entry.nameLossy;
}

function canDownload(entry: FileEntry): boolean {
  return entry.type === 'file' && !entry.nameLossy;
}

/**
 * Dot-DIRECTORIES (`.claude`, `.conversations`, …) hold agent-managed state: deleting one from the
 * browser destroys the conversation's own bookkeeping, so the affordance is withheld. This mirrors
 * the server's dot-directory reasoning in `FilePreviewPolicy.IsUnderDotDirectory`. It is a client
 * guard only — a `cannot_delete_managed_directory` server rule is the real control.
 */
function isManagedDirectory(entry: FileEntry): boolean {
  return entry.type === 'directory' && entry.name.startsWith('.');
}

function canDelete(entry: FileEntry): boolean {
  return entry.type !== 'symlink' && !entry.nameLossy && !isManagedDirectory(entry);
}

function onRowClick(entry: FileEntry): void {
  if (isNavigable(entry)) {
    void goTo(joinCurrent(entry.name));
    return;
  }
  if (isOpenable(entry)) {
    emit('openFile', joinCurrent(entry.name));
  }
}

/** Directory navigation target: the entry name joined onto the current breadcrumb path. */
function joinCurrent(name: string): string {
  const current = breadcrumbs.value[breadcrumbs.value.length - 1]?.path ?? '';
  return current ? `${current}/${name}` : name;
}

/** Navigates to `path`, dropping the row filter so the destination is never shown pre-filtered. */
async function goTo(path: string): Promise<void> {
  filterText.value = '';
  await navigateTo(path);
}

function onPreview(entry: FileEntry): void {
  emit('openFile', joinCurrent(entry.name));
}

function onDownload(entry: FileEntry): void {
  void download(entry);
}

/** Re-reads the CURRENT directory. The listing is REST, so an agent writing files while the panel is
 *  open (or a conversation that just got its first sandbox session) needs an explicit refresh. */
function onRefresh(): void {
  void load();
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
  // Ignore new picks while an upload is busy (a batch is running OR an overwrite confirm is pending).
  if (uploadBusy.value) {
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
  if (uploadBusy.value) {
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
  // Ignore drops while an upload is busy (a batch is running OR an overwrite confirm is pending).
  if (!event.dataTransfer || uploadBusy.value) {
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
    setPendingUpload({ files, colliding });
    return;
  }
  void doUpload(files);
}

/**
 * Overwrite confirmed: upload the whole batch (colliding files are replaced, last-writer-wins). The
 * listing is re-checked (reloaded) FIRST so the decision is applied against the current directory rather
 * than a stale snapshot (e.g. after a mixed drop's folder batch mutated it while the confirm was open).
 */
async function confirmOverwrite(): Promise<void> {
  const pending = pendingUpload.value;
  setPendingUpload(null);
  if (pending) {
    await load();
    await doUpload(pending.files);
  }
}

/** Overwrite declined: skip the colliding files and upload only the non-colliding ones (per-file independence). */
function cancelOverwrite(): void {
  const pending = pendingUpload.value;
  setPendingUpload(null);
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

/** Extensions rendered with a picture glyph. Mirrors ArtifactPreviewModal's image list by VALUE — the
 *  two surfaces stay independent components, so the list is copied rather than imported. */
const IMAGE_EXTENSIONS = new Set(['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'svg', 'ico', 'avif']);
const MARKDOWN_EXTENSIONS = new Set(['md', 'markdown', 'mdx']);

type IconKind = 'folder' | 'symlink' | 'markdown' | 'image' | 'file';

/** Which inline SVG glyph a row renders. Emoji are gone: the app draws its own icons everywhere else. */
function iconKind(entry: FileEntry): IconKind {
  if (entry.type === 'directory') {
    return 'folder';
  }
  if (entry.type === 'symlink') {
    return 'symlink';
  }
  const extension = entry.name.includes('.')
    ? (entry.name.split('.').pop() ?? '').toLowerCase()
    : '';
  if (MARKDOWN_EXTENSIONS.has(extension)) {
    return 'markdown';
  }
  if (IMAGE_EXTENSIONS.has(extension)) {
    return 'image';
  }
  return 'file';
}

/** First 8 characters of the workspace id — enough to tell two workspaces apart in a 320px column. */
const workspaceShort = computed(() => (workspaceId.value ?? '').slice(0, 8));
</script>

<template>
  <div class="file-browser" :class="{ embedded }" data-testid="file-browser">
    <!-- Background content: made inert while a destructive confirmation is open so focus + pointer stay
         confined to the confirmation overlay below (which sits OUTSIDE this inert subtree). -->
    <div class="fb-main" :inert="isConfirmOpen || undefined">
      <!-- No conversation yet: an unsent New Chat has no thread, so there is no workspace to list.
           Without this the empty listing would read as "the workspace is empty". -->
      <div v-if="!hasThread" class="fb-empty" data-testid="file-browser-no-thread">
        Send a message to start a workspace.
      </div>

      <!-- No sandbox session yet: the conversation hasn't provisioned a workspace. -->
      <div v-else-if="noSession" class="fb-empty" data-testid="file-browser-no-session">
        No sandbox session yet. Send a message that uses the sandbox to create one.
      </div>

      <template v-else>
        <!-- Breadcrumb + the active workspace as a muted chip (shared by any conversation with the
             same workspace/identity); the full id lives in the tooltip. -->
        <div class="fb-path-row">
          <nav class="fb-breadcrumbs" data-testid="file-browser-breadcrumb" aria-label="Breadcrumb">
            <template v-for="(crumb, idx) in breadcrumbs" :key="crumb.path">
              <button class="fb-crumb" :data-testid="`file-browser-crumb-${idx}`" @click="goTo(crumb.path)">
                {{ crumb.name }}
              </button>
              <span v-if="idx < breadcrumbs.length - 1" class="fb-crumb-sep" aria-hidden="true">/</span>
            </template>
          </nav>
          <span
            v-if="workspaceId"
            class="fb-workspace"
            data-testid="file-browser-workspace"
            :title="`Workspace ${workspaceId}`"
            >{{ workspaceShort }}</span
          >
        </div>

        <!-- Toolbar: directory-level actions as ghost icon buttons, plus the client-side row filter.
             Labels live in `title`/`aria-label` so the row survives a 340px drawer. -->
        <div class="fb-toolbar" data-testid="file-browser-toolbar">
          <button
            class="fb-icon-btn"
            data-testid="file-browser-new-folder"
            title="New folder"
            aria-label="New folder"
            @click="openNewFolder"
          >
            <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
              <path d="M2.5 5.5a1 1 0 0 1 1-1h3.2l1.4 1.6h8.4a1 1 0 0 1 1 1v8.4a1 1 0 0 1-1 1h-13a1 1 0 0 1-1-1z" />
              <path d="M10 9.2v4.2M7.9 11.3h4.2" />
            </svg>
          </button>
          <button
            class="fb-icon-btn"
            data-testid="file-browser-upload"
            title="Upload files"
            aria-label="Upload files"
            :disabled="uploadBusy"
            @click="fileInputRef?.click()"
          >
            <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
              <path d="M10 13.5V3.8M6.4 7.3 10 3.7l3.6 3.6" />
              <path d="M3.5 12.6v2.6a1 1 0 0 0 1 1h11a1 1 0 0 0 1-1v-2.6" />
            </svg>
          </button>
          <button
            class="fb-icon-btn"
            data-testid="file-browser-folder-upload"
            title="Upload folder"
            aria-label="Upload folder"
            :disabled="!folderPickerSupported || uploadBusy"
            @click="folderInputRef?.click()"
          >
            <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
              <path d="M2.5 15.5V5.5a1 1 0 0 1 1-1h3.2l1.4 1.6h8.4a1 1 0 0 1 1 1v8.4a1 1 0 0 1-1 1h-13a1 1 0 0 1-1-1z" />
              <path d="M10 14V8.6M8 10.6 10 8.5l2 2.1" />
            </svg>
          </button>
          <button
            class="fb-icon-btn"
            data-testid="file-browser-refresh"
            title="Refresh"
            aria-label="Refresh"
            @click="onRefresh"
          >
            <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
              <path d="M16.2 10a6.2 6.2 0 1 1-1.9-4.5" />
              <path d="M16.4 3.4v3.4h-3.4" />
            </svg>
          </button>
          <input
            v-model="filterText"
            class="fb-filter"
            type="search"
            placeholder="Filter…"
            aria-label="Filter files"
            data-testid="file-browser-filter"
          />
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
        </div>

        <span
          v-if="!folderPickerSupported"
          class="fb-folder-unsupported"
          data-testid="file-browser-folder-unsupported"
        >
          Folder upload isn’t supported by this browser — drag a folder in or use the upload button.
        </span>

        <div v-if="error" class="fb-error" data-testid="file-browser-error">{{ error }}</div>

        <div v-if="uploadProgress" class="fb-progress" data-testid="file-browser-upload-progress">
          Uploading {{ uploadProgress.completed }}/{{ uploadProgress.total }}<span
            v-if="uploadProgress.activeName"
            class="fb-progress-name"
          >
            — {{ uploadProgress.activeName }}</span
          >
        </div>

        <div v-if="uploadSummary" class="fb-summary" data-testid="file-browser-upload-summary">
          {{ uploadSummary }}
        </div>

        <div v-if="uploadErrors" class="fb-error" data-testid="file-browser-upload-errors">
          {{ uploadErrors }}
        </div>

        <!-- The LIST is the drop target: no permanently expanded dashed zone eating panel height. The
             dashed accent appears only while a drag is over it. -->
        <div
          class="fb-dropzone"
          :class="{ 'fb-dropzone-active': isDragOver }"
          data-testid="file-browser-dropzone"
          @dragover.prevent="isDragOver = true"
          @dragleave.prevent="isDragOver = false"
          @drop.prevent="onDrop"
        >
          <!-- ONE fixed-height, internally-scrolling container that stays mounted in EVERY state (loading,
               empty, populated) so the panel never collapses to a one-line div and re-expands on refresh. -->
          <ul class="fb-list" :style="listScrollStyle" data-testid="file-browser-list">
            <li v-if="isLoading" class="fb-loading" data-testid="file-browser-loading">Loading…</li>
            <li v-else-if="entries.length === 0" class="fb-empty" data-testid="file-browser-empty">
              This directory is empty.
            </li>
            <li v-else-if="visibleEntries.length === 0" class="fb-empty" data-testid="file-browser-empty">
              No files match the filter.
            </li>
            <template v-else>
              <li
                v-for="entry in visibleEntries"
                :key="entry.name"
                class="fb-row"
                :class="{ 'fb-row-symlink': entry.type === 'symlink', 'fb-row-lossy': entry.nameLossy }"
                :title="isManagedDirectory(entry) ? 'Managed folder' : undefined"
                :data-testid="`file-entry-${entry.name}`"
              >
                <button
                  class="fb-name"
                  :class="{ 'fb-name-open': isNavigable(entry) || isOpenable(entry) }"
                  :disabled="!isNavigable(entry) && !isOpenable(entry)"
                  :data-testid="`file-entry-name-${entry.name}`"
                  @click="onRowClick(entry)"
                >
                  <span class="fb-icon" aria-hidden="true">
                    <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
                      <template v-if="iconKind(entry) === 'folder'">
                        <path d="M2.5 15.5V5.5a1 1 0 0 1 1-1h3.2l1.4 1.6h8.4a1 1 0 0 1 1 1v8.4a1 1 0 0 1-1 1h-13a1 1 0 0 1-1-1z" />
                      </template>
                      <template v-else-if="iconKind(entry) === 'symlink'">
                        <path d="M8.6 11.4a2.8 2.8 0 0 0 4 0l2.4-2.4a2.8 2.8 0 1 0-4-4l-.8.8" />
                        <path d="M11.4 8.6a2.8 2.8 0 0 0-4 0L5 11a2.8 2.8 0 1 0 4 4l.8-.8" />
                      </template>
                      <template v-else-if="iconKind(entry) === 'image'">
                        <rect x="3" y="4" width="14" height="12" rx="1.6" />
                        <path d="M3.6 13.4 7.4 9.8l3 2.8 2.2-2 3.6 3.4" />
                        <circle cx="7.6" cy="7.6" r="1.1" />
                      </template>
                      <template v-else>
                        <path d="M5 3.5h6.2L15 7.3v9.2a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1v-12a1 1 0 0 1 1-1z" />
                        <path d="M11 3.6v3.9h3.9" />
                        <path v-if="iconKind(entry) === 'markdown'" d="M6.2 14.4v-3.6l1.7 2 1.7-2v3.6M11.8 10.8v3.6h1.6" />
                      </template>
                    </svg>
                  </span>
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

                <!-- Row actions stay in the DOM at all times (so keyboard focus and tests reach them)
                     and are revealed on hover / focus-within, like the conversation sidebar's. -->
                <span class="fb-actions">
                  <button
                    v-if="isOpenable(entry)"
                    class="fb-action"
                    :data-testid="`file-entry-preview-${entry.name}`"
                    :title="`Open ${entry.name}`"
                    :aria-label="`Open ${entry.name}`"
                    @click="onPreview(entry)"
                  >
                    <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
                      <path d="M1.8 10S4.7 5.2 10 5.2 18.2 10 18.2 10 15.3 14.8 10 14.8 1.8 10 1.8 10z" />
                      <circle cx="10" cy="10" r="2.2" />
                    </svg>
                  </button>
                  <button
                    v-if="canDownload(entry)"
                    class="fb-action"
                    :data-testid="`file-entry-download-${entry.name}`"
                    :title="`Download ${entry.name}`"
                    :aria-label="`Download ${entry.name}`"
                    @click="onDownload(entry)"
                  >
                    <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
                      <path d="M10 3.6v9.7M6.4 9.7 10 13.4l3.6-3.7" />
                      <path d="M3.5 14.4v1.1a1 1 0 0 0 1 1h11a1 1 0 0 0 1-1v-1.1" />
                    </svg>
                  </button>
                  <button
                    v-if="canDelete(entry)"
                    class="fb-action fb-action-danger"
                    :data-testid="`file-entry-delete-${entry.name}`"
                    :title="`Delete ${entry.name}`"
                    :aria-label="`Delete ${entry.name}`"
                    @click="askDelete(entry)"
                  >
                    <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
                      <path d="M4.4 5.9h11.2M8.1 5.9V4.2h3.8v1.7" />
                      <path d="M5.8 5.9l.7 9.5a1 1 0 0 0 1 .9h5a1 1 0 0 0 1-.9l.7-9.5" />
                    </svg>
                  </button>
                </span>
              </li>
            </template>
          </ul>
        </div>

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
            class="fb-text-btn"
            data-testid="file-browser-delete-cancel"
            @click="cancelDelete"
          >
            Cancel
          </button>
          <button
            class="fb-text-btn fb-text-btn-danger"
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
            class="fb-text-btn"
            data-testid="file-browser-overwrite-cancel"
            @click="cancelOverwrite"
          >
            Skip existing
          </button>
          <button
            class="fb-text-btn fb-text-btn-danger"
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
            class="fb-text-btn"
            data-testid="file-browser-new-folder-cancel"
            @click="cancelNewFolder"
          >
            Cancel
          </button>
          <button
            class="fb-text-btn fb-text-btn-primary"
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
  gap: 10px;
  padding: 16px 20px;
  color: #334155;
  font-size: 13px;
  /* Lets the size column drop out when the inspector column itself is narrow, regardless of viewport. */
  container-type: inline-size;
}

/* Hosted as a right-panel disclosure: the section supplies its own spacing, so the browser gives up
   its outer padding and sits flush with the Work / Agents panels above and below it. */
.file-browser.embedded {
  padding: 0 10px 4px;
  gap: 8px;
}

/* The interactive content behind any confirmation overlay. Carries the column layout so the fixed-
   position confirmations can sit outside it (as siblings) and toggle its `inert` state. */
.fb-main {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
}

.fb-path-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.fb-workspace {
  flex: none;
  max-width: 88px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  padding: 1px 7px;
  border-radius: 10px;
  background: #eef1f5;
  color: #64748b;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 11px;
}

.fb-breadcrumbs {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 2px;
  min-width: 0;
  flex: 1;
  font-size: 13px;
}

.fb-crumb {
  background: none;
  border: none;
  color: #2d6cdf;
  cursor: pointer;
  padding: 2px 3px;
  font: inherit;
}

.fb-crumb:hover {
  text-decoration: underline;
}

.fb-crumb-sep {
  color: #94a3b8;
}

.fb-toolbar {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
}

/* Ghost icon button, matching `.inspector-close` / `.artifact-preview-action`. */
.fb-icon-btn {
  display: inline-flex;
  flex: none;
  width: 30px;
  height: 30px;
  align-items: center;
  justify-content: center;
  border: 1px solid #e2e6eb;
  border-radius: 6px;
  background: #fff;
  color: #475569;
  cursor: pointer;
}

.fb-icon-btn:hover:not(:disabled) {
  background: #eef1f5;
  color: #1d4ed8;
}

.fb-icon-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.fb-icon-btn svg,
.fb-action svg {
  width: 17px;
  height: 17px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.fb-filter {
  flex: 1;
  min-width: 0;
  margin-left: 4px;
  padding: 5px 9px;
  border: 1px solid #e2e6eb;
  border-radius: 6px;
  background: #fff;
  color: #334155;
  font: inherit;
}

.fb-filter:focus {
  outline: 2px solid #2d6cdf40;
  border-color: #2d6cdf;
}

/* The list wrapper IS the drop target; the dashed accent only appears mid-drag. */
.fb-dropzone {
  border: 1px solid #e2e6eb;
  border-radius: 8px;
  background: #fff;
  min-width: 0;
}

.fb-dropzone-active {
  border: 1px dashed #2d6cdf;
  background: #f0f6ff;
}

.fb-folder-unsupported {
  color: #94a3b8;
  font-size: 11px;
}

.fb-file-input {
  display: none;
}

.fb-error {
  padding: 8px 12px;
  background: #f8d7da;
  color: #721c24;
  border-radius: 6px;
  /* Long relative-path lists (folder uploads) must wrap, never blow out horizontally. */
  word-break: break-word;
  overflow-wrap: anywhere;
}

.fb-progress {
  padding: 8px 12px;
  background: #e7f1ff;
  color: #1c4a8a;
  border-radius: 6px;
  word-break: break-word;
  overflow-wrap: anywhere;
}

.fb-progress-name {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
}

.fb-summary {
  color: #4a7a4a;
}

.fb-loading,
.fb-empty {
  color: #64748b;
  padding: 10px 12px;
}

/* Stable-height, internally-scrolling list panel. The responsive height + overflow + min-height:0 come
   from the inline `listScrollStyle`; these rules bound it so the section stays compact in a right-panel
   column instead of pushing Work and Agents off screen. */
.fb-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  min-height: 240px;
  max-height: 520px;
}

.fb-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 6px;
  border-bottom: 1px solid #f1f4f7;
  min-width: 0;
}

.fb-row:last-child {
  border-bottom: 0;
}

.fb-row:hover {
  background: #f7f8fa;
}

.fb-row-symlink {
  font-style: italic;
  color: #64748b;
}

.fb-row-lossy {
  opacity: 0.7;
}

.fb-name {
  display: flex;
  align-items: center;
  gap: 7px;
  background: none;
  border: none;
  padding: 3px 2px;
  font: inherit;
  color: #334155;
  cursor: default;
  text-align: left;
  /* Allow the flex child to shrink so a long name ellipsizes instead of overflowing the row. */
  min-width: 0;
  flex: 1;
}

.fb-icon {
  display: inline-flex;
  flex: none;
  color: #64748b;
}

.fb-icon svg {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.fb-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fb-name-open {
  cursor: pointer;
}

.fb-name-open:hover .fb-label {
  color: #2d6cdf;
  text-decoration: underline;
}

.fb-name:disabled {
  cursor: default;
}

.fb-badge {
  flex: none;
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 10px;
  background: #ffe0b2;
  color: #8a5a00;
}

.fb-badge-symlink {
  background: #eef1f5;
  color: #64748b;
}

.fb-size {
  flex: none;
  color: #94a3b8;
  font-size: 11px;
  min-width: 56px;
  text-align: right;
}

/* Hover/focus-revealed row actions (the conversation sidebar's pattern). They stay in the DOM — only
   their opacity changes — so keyboard focus, screen readers and click targets are never removed. */
.fb-actions {
  display: flex;
  flex: none;
  gap: 2px;
  opacity: 0;
  transition: opacity 0.12s ease-in-out;
}

.fb-row:hover .fb-actions,
.fb-row:focus-within .fb-actions {
  opacity: 1;
}

.fb-action {
  display: inline-flex;
  width: 26px;
  height: 26px;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: #64748b;
  cursor: pointer;
}

.fb-action:hover {
  background: #eef1f5;
  color: #1d4ed8;
}

.fb-action-danger {
  margin-left: 4px;
}

.fb-action-danger:hover {
  background: #fdeaec;
  color: #c82333;
}

.fb-more {
  color: #94a3b8;
  font-style: italic;
  padding: 2px 0;
}

/* Below ~420px of panel width the size column is the first thing to go. */
@container (max-width: 420px) {
  .fb-size {
    display: none;
  }
}

.fb-confirm-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.4);
  display: flex;
  align-items: center;
  justify-content: center;
  /* Above the inspector's overlay drawer (z 101) so a confirm is never trapped behind it. */
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
  color: #334155;
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
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  font-size: 14px;
}

/* Text buttons, used only inside the confirmation dialogs. */
.fb-text-btn {
  padding: 5px 12px;
  background: #fff;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  color: #334155;
  font: inherit;
  cursor: pointer;
}

.fb-text-btn:hover:not(:disabled) {
  background: #eef1f5;
}

.fb-text-btn-danger {
  color: #c82333;
  border-color: #f1b0b7;
}

.fb-text-btn-primary {
  background: #2d6cdf;
  color: white;
  border-color: #2d6cdf;
}

.fb-text-btn-primary:hover:not(:disabled) {
  background: #245ac0;
}

.fb-text-btn-primary:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
