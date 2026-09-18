<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import BaseModal from './BaseModal.vue';
import TextMessage from './TextMessage.vue';
import {
  FileBrowserError,
  NoSessionError,
  downloadFile,
  fetchFileBlob,
  listFiles,
  previewFile,
  resolveWorkspaceLink,
} from '@/api/fileBrowserApi';
import { isNoSession, type PreviewResult } from '@/types/fileBrowser';
import { MessageType, type TextMessage as TextMessageModel } from '@/types';
import { isMarkdownArtifact } from '@/utils/todoBoard';
import { delimiterForPath, parseDelimitedText } from '@/utils/delimitedText';
import { logger } from '@/utils';

/**
 * Read-only preview surface for a workspace file. It can be embedded in the inspector or retain its
 * legacy modal presentation. Two openers:
 *
 *   - a task's artifact chip on the work board (#583, PR 5) passes a workspace-relative `path`;
 *   - a file link in an assistant message passes the raw link `target` (a host path, `file://` URI or
 *     relative path), which the server first maps onto the workspace (`GET files/resolve?target=`).
 *
 * Content comes from the EXISTING file-browser endpoints, which own the policy: `preview` (256 KiB /
 * 5000-line cap, UTF-8-only, dot-directory exclusions) for text, `download` (64 MiB) for image bytes.
 *
 * Viewers, by extension: `.md`/`.markdown` through the app's completed `TextMessage` pipeline (including
 * diagrams); `.csv`/`.tsv` as a table; common images as `<img>` over a re-typed blob; any other
 * previewable text as `<pre>`. A non-previewable file shows the server's `reason`. Every resolved file
 * also gets a Download action.
 */
const log = logger.forComponent('ArtifactPreviewModal');

const props = defineProps<{
  /** The conversation whose workspace the file lives in. */
  threadId: string;
  /** Workspace-relative path, exactly as carried on the task row. */
  path?: string;
  /** Raw file link from a chat message; resolved on the server. Used when `path` is absent. */
  target?: string;
  /**
   * True while the layout reserves an expanded sidebar column on the left (#594 D6, #603 F-001):
   * the backdrop then stops at that column's edge so conversation switching stays a single click.
   * See `.artifact-preview-beside-sidebar` below for why this is geometry, not z-index.
   */
  besideSidebar?: boolean;
  /** Render as a persistent preview region instead of a focus-trapping modal. */
  embedded?: boolean;
  /** Whether the surrounding workspace currently presents this preview expanded. */
  expanded?: boolean;
}>();

const emit = defineEmits<{
  close: [];
  toggleExpand: [];
  /** Fired once a `target` opener resolves to its canonical workspace path (F-001, #784): lets the
   * parent reconcile this tab's identity onto the server-resolved path so it dedupes against a tab
   * opened the other way (by `path`) for the same file. Never fired when `path` was given directly —
   * that identity is already canonical. */
  resolved: [path: string];
}>();

/** Images above this are not pulled into the page; the Download button still works. */
const MAX_INLINE_IMAGE_BYTES = 16 * 1024 * 1024;

const IMAGE_TYPES: Record<string, string> = {
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  gif: 'image/gif',
  webp: 'image/webp',
  bmp: 'image/bmp',
  // Rendered only through <img>, where an SVG's scripts never run.
  svg: 'image/svg+xml',
};

const isLoading = ref(true);
const resolvedPath = ref<string | null>(props.path ?? null);
const result = ref<PreviewResult | null>(null);
const imageUrl = ref<string | null>(null);
/** A client-side reason the file is not shown inline (folder, image too large). */
const unavailableText = ref<string | null>(null);
const errorText = ref<string | null>(null);

const displayPath = computed(() => resolvedPath.value ?? props.target ?? '');
const fileName = computed(() => {
  const segments = displayPath.value.split(/[\\/]/).filter(Boolean);
  return segments[segments.length - 1] ?? displayPath.value;
});
const surfaceAttributes = computed(() => props.embedded
  ? {
      'data-testid': 'artifact-preview-surface',
      role: 'region',
      'aria-label': `File preview: ${fileName.value}`,
    }
  : {
      title: displayPath.value,
      dataTestId: 'artifact-preview-modal',
    }
);

const imageType = computed(() => {
  const ext = /\.([a-z0-9]+)$/i.exec(resolvedPath.value ?? '')?.[1]?.toLowerCase();
  return ext ? (IMAGE_TYPES[ext] ?? null) : null;
});

const isMarkdown = computed(() => isMarkdownArtifact(resolvedPath.value ?? ''));
const delimiter = computed(() => delimiterForPath(resolvedPath.value ?? ''));

const previewText = computed(() =>
  result.value?.previewable && result.value.text !== undefined ? result.value.text : null
);

const markdownMessage = computed<TextMessageModel>(() => ({
  $type: MessageType.Text,
  role: 'assistant',
  text: previewText.value ?? '',
}));

const table = computed(() =>
  previewText.value !== null && delimiter.value
    ? parseDelimitedText(previewText.value, delimiter.value)
    : null
);

const isFolder = ref(false);

/** Offered once the path is known and the entry is a file — including when its preview is unavailable. */
const canDownload = computed(
  () => !isLoading.value && resolvedPath.value !== null && !errorText.value && !isFolder.value
);

/**
 * Cancels in-flight reads when the modal unmounts (596/F-005). Unmounting already prevented a stale
 * paint — a late response writes into dead refs — but the request itself ran to completion and was
 * discarded, up to a 256 KiB read for nothing on every conversation switch.
 */
const abort = new AbortController();

function describeFailure(e: unknown): string {
  if (e instanceof NoSessionError) {
    return 'This conversation has no workspace session, so the file cannot be previewed right now.';
  }
  if (e instanceof FileBrowserError) {
    switch (e.code) {
      case 'outside_workspace':
        return 'This link points outside the workspace.';
      case 'invalid_path':
        return 'This link is not a valid workspace path.';
      case 'not_found':
        return 'This file was not found in the workspace.';
    }
  }
  return 'Could not load the preview.';
}

/**
 * The size of the file at a workspace-relative `path`, read from its parent's listing: the artifact chip
 * passes a bare path, so nothing has reported the size yet. Null when the listing cannot say (the entry is
 * past the server's row cap, or has no size).
 */
async function sizeFromListing(path: string): Promise<number | null> {
  const slash = path.lastIndexOf('/');
  const listing = await listFiles(props.threadId, slash < 0 ? '' : path.slice(0, slash), abort.signal);
  if (isNoSession(listing)) throw new NoSessionError();
  const name = path.slice(slash + 1);
  const entry = listing.entries.find((e) => e.name === name);
  if (entry) return entry.size;
  if (listing.moreCount > 0) return null;
  throw new FileBrowserError('File not found', 404, 'not_found');
}

async function load(): Promise<void> {
  // undefined: not reported yet (the artifact chip's bare path); null: reported as unknown.
  let size: number | null | undefined;
  if (resolvedPath.value === null && props.target !== undefined) {
    const resolved = await resolveWorkspaceLink(props.threadId, props.target, abort.signal);
    resolvedPath.value = resolved.path;
    emit('resolved', resolved.path);
    size = resolved.size;
    if (resolved.type === 'directory') {
      isFolder.value = true;
      unavailableText.value = 'This link points to a folder, not a file.';
      return;
    }
  }

  const path = resolvedPath.value;
  if (path === null) return;

  if (imageType.value) {
    // Checked before any bytes move: the download endpoint would otherwise pull up to 64 MiB into the page.
    if (size === undefined) size = await sizeFromListing(path);
    if (size === null) {
      unavailableText.value = "This image's size could not be checked, so it is not shown here.";
      return;
    }
    if (size > MAX_INLINE_IMAGE_BYTES) {
      unavailableText.value = 'This image is too large to show here.';
      return;
    }
    const blob = await fetchFileBlob(props.threadId, path, abort.signal);
    // The download endpoint answers application/octet-stream + nosniff; an <img> needs the real type.
    imageUrl.value = URL.createObjectURL(new Blob([blob], { type: imageType.value }));
    return;
  }

  result.value = await previewFile(props.threadId, path, abort.signal);
}

onMounted(async () => {
  try {
    await load();
  } catch (e) {
    // Our own unmount-time abort is not a failure — and the component is gone, so there is nothing
    // to say it to. (`fetch` rejects an aborted call with DOMException 'AbortError'.)
    if (abort.signal.aborted) return;
    // The preview is an accessory: a failure degrades to a message inside the modal, never to an
    // error banner over the chat. Recorded at debug like the board's own load failures.
    errorText.value = describeFailure(e);
    log.debug('Artifact preview failed', { path: displayPath.value, error: e });
  } finally {
    if (!abort.signal.aborted) isLoading.value = false;
  }
});

const isDownloading = ref(false);

async function download(): Promise<void> {
  if (resolvedPath.value === null) return;
  isDownloading.value = true;
  try {
    await downloadFile(props.threadId, resolvedPath.value, abort.signal);
  } catch (e) {
    if (abort.signal.aborted) return;
    log.debug('Artifact download failed', { path: resolvedPath.value, error: e });
    errorText.value = e instanceof FileBrowserError && e.code === 'file_too_large'
      ? 'This file is too large to download.'
      : 'Could not download the file.';
  } finally {
    if (!abort.signal.aborted) isDownloading.value = false;
  }
}

onBeforeUnmount(() => {
  abort.abort();
  if (imageUrl.value) URL.revokeObjectURL(imageUrl.value);
});
</script>

<template>
  <component
    :is="props.embedded ? 'section' : BaseModal"
    v-bind="surfaceAttributes"
    :class="{
      'artifact-preview-surface': props.embedded,
      'artifact-preview-surface-expanded': props.embedded && props.expanded,
      'artifact-preview-beside-sidebar': !props.embedded && props.besideSidebar,
    }"
    @close="emit('close')"
  >
    <header v-if="props.embedded" class="artifact-preview-header">
      <div class="artifact-preview-heading">
        <strong class="artifact-preview-filename" data-testid="artifact-preview-filename">
          {{ fileName }}
        </strong>
        <span class="artifact-preview-path" data-testid="artifact-preview-path" :title="displayPath">
          {{ displayPath }}
        </span>
      </div>
      <div class="artifact-preview-actions">
        <button
          type="button"
          class="artifact-preview-action"
          :aria-label="props.expanded ? 'Restore file preview' : 'Expand file preview'"
          :title="props.expanded ? 'Restore preview' : 'Expand preview'"
          data-testid="artifact-preview-expand"
          @click="emit('toggleExpand')"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true">
            <path v-if="props.expanded" d="M3.5 8h4.5V3.5M16.5 12H12v4.5M8 8 3.5 3.5M12 12l4.5 4.5" />
            <path v-else d="M8 3.5H3.5V8M12 16.5h4.5V12M3.5 3.5 8 8M16.5 16.5 12 12" />
          </svg>
        </button>
        <button
          v-if="canDownload"
          type="button"
          class="artifact-preview-action"
          :disabled="isDownloading"
          :aria-label="isDownloading ? 'Downloading file' : 'Download file'"
          :title="isDownloading ? 'Downloading…' : 'Download'"
          data-testid="artifact-preview-download"
          @click="download"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true">
            <path d="M10 3v9M6.5 8.5 10 12l3.5-3.5M4 16h12" />
          </svg>
        </button>
        <button
          type="button"
          class="artifact-preview-action"
          aria-label="Close file preview"
          title="Close preview"
          data-testid="artifact-preview-close"
          @click="emit('close')"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true">
            <path d="m5 5 10 10M15 5 5 15" />
          </svg>
        </button>
      </div>
    </header>

    <div class="artifact-preview">
      <div v-if="isLoading" class="artifact-preview-message" data-testid="artifact-preview-loading">
        Loading preview…
      </div>

      <div
        v-else-if="errorText"
        class="artifact-preview-message"
        data-testid="artifact-preview-error"
      >
        {{ errorText }}
      </div>

      <div
        v-else-if="unavailableText"
        class="artifact-preview-message"
        data-testid="artifact-preview-unavailable"
      >
        {{ unavailableText }}
      </div>

      <img
        v-else-if="imageUrl"
        class="artifact-preview-image"
        :src="imageUrl"
        :alt="displayPath"
        data-testid="artifact-preview-image"
      />

      <div
        v-else-if="previewText !== null && isMarkdown"
        class="markdown-content artifact-preview-markdown"
        data-testid="artifact-preview-markdown"
      >
        <TextMessage :message="markdownMessage" :is-complete="true" :workspace-links="false" />
      </div>

      <div v-else-if="table" class="artifact-preview-table-wrap">
        <table class="artifact-preview-table" data-testid="artifact-preview-table">
          <thead v-if="table.rows.length > 0">
            <tr>
              <th v-for="(cell, c) in table.rows[0]" :key="c">{{ cell }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(row, r) in table.rows.slice(1)" :key="r">
              <td v-for="(cell, c) in row" :key="c">{{ cell }}</td>
            </tr>
          </tbody>
        </table>
        <div v-if="table.truncated" class="artifact-preview-message">
          Showing the first {{ table.rows.length }} rows. Download the file to see all of it.
        </div>
      </div>

      <pre
        v-else-if="previewText !== null"
        class="artifact-preview-text"
        data-testid="artifact-preview-text"
        >{{ previewText }}</pre
      >

      <div v-else class="artifact-preview-message" data-testid="artifact-preview-unavailable">
        Preview unavailable<span v-if="result?.reason"> ({{ result.reason }})</span>.
      </div>
    </div>

    <div v-if="canDownload && !props.embedded" class="artifact-preview-footer">
      <button
        type="button"
        class="artifact-preview-download"
        :disabled="isDownloading"
        data-testid="artifact-preview-download"
        @click="download"
      >
        {{ isDownloading ? 'Downloading…' : 'Download' }}
      </button>
    </div>
  </component>
</template>

<style scoped>
/* #594 D6, reworked for #603 F-001: keep single-click conversation switching while the preview is
   open — by GEOMETRY, not stacking. The first cut lifted the sidebar to z-index 1001, which also
   reordered painting: below 1200px viewport width the opaque 280px sidebar painted over the
   centred 640px dialog's left third and stole its clicks (including the per-row delete buttons
   sitting under the dialog's rectangle). Instead, THIS modal's backdrop simply starts at the
   sidebar column's right edge: sidebar and dialog are disjoint at every width, nothing is lifted
   above any other modal's z-1000 backdrop, and backdrop-click-to-close keeps working on the
   backdrop that exists. The class lands on BaseModal's root (`.modal-backdrop`) via the child-root
   scope-id fallthrough; repeating `.modal-backdrop` in the selector out-specifies BaseModal's own
   `inset: 0` (0,3,0 vs 0,2,0), so bundle source order never decides the cascade. 280px mirrors
   `.conversation-sidebar`'s width — the pair is cross-checked by a source guard in
   ChatLayout.test.ts. */
.modal-backdrop.artifact-preview-beside-sidebar {
  left: 280px;
}

/* At ConversationSidebar's own <=768px breakpoint the sidebar is a self-overlaying drawer, not a
   reserved column, so the backdrop returns to full viewport and covers it like every other modal.
   Same compound selector, later in this block: wins over the base rule by order within ONE file,
   which the compiler preserves. */
@media (max-width: 768px) {
  .modal-backdrop.artifact-preview-beside-sidebar {
    left: 0;
  }
}

.artifact-preview {
  min-width: 320px;
  max-width: 720px;
  max-height: 60vh;
  overflow: auto;
}

.artifact-preview-surface {
  display: flex;
  min-width: 0;
  min-height: 0;
  height: 100%;
  box-sizing: border-box;
  flex-direction: column;
  overflow: hidden;
  background: #fff;
  color: #334155;
}

.artifact-preview-surface .artifact-preview {
  min-width: 0;
  min-height: 0;
  max-width: none;
  max-height: none;
  flex: 1;
}

.artifact-preview-header {
  display: flex;
  min-height: 52px;
  align-items: center;
  gap: 12px;
  padding: 8px 10px 8px 16px;
  border-bottom: 1px solid #e2e6eb;
  background: #f7f8fa;
}

.artifact-preview-heading {
  min-width: 0;
  flex: 1;
}

.artifact-preview-filename,
.artifact-preview-path {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.artifact-preview-filename {
  font-size: 14px;
  font-weight: 600;
  color: #334155;
}

.artifact-preview-path {
  margin-top: 2px;
  font-size: 11px;
  color: #64748b;
}

.artifact-preview-actions {
  display: flex;
  flex: none;
  gap: 2px;
}

.artifact-preview-action {
  display: inline-grid;
  width: 30px;
  height: 30px;
  padding: 0;
  place-items: center;
  border: 1px solid transparent;
  border-radius: 5px;
  background: transparent;
  color: #64748b;
  font-size: 17px;
  line-height: 1;
  cursor: pointer;
}

.artifact-preview-action svg {
  width: 17px;
  height: 17px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.artifact-preview-action:hover,
.artifact-preview-action:focus-visible {
  border-color: #e2e6eb;
  background: #fff;
  color: #334155;
}

.artifact-preview-action:focus-visible {
  outline: 2px solid #2563eb;
  outline-offset: 1px;
}

.artifact-preview-action:disabled {
  opacity: 0.55;
  cursor: default;
}

.artifact-preview-message {
  padding: 24px 16px;
  text-align: center;
  color: #666;
  font-size: 13px;
}

.artifact-preview-markdown {
  width: 100%;
  box-sizing: border-box;
  padding: 20px 24px 40px;
  font-size: 16px;
  line-height: 1.65;
}

.artifact-preview-markdown :deep(.markdown-content > :is(p, h1, h2, h3, h4, h5, h6, ul, ol, blockquote, pre)) {
  max-width: 48rem;
  margin-right: auto;
  margin-left: auto;
}

.artifact-preview-text {
  margin: 0;
  padding: 8px;
  font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', Menlo, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  background: #f8f9fa;
  border-radius: 6px;
}

.artifact-preview-image {
  display: block;
  max-width: 100%;
  margin: 8px auto;
}

.artifact-preview-table-wrap {
  padding: 8px;
}

.artifact-preview-table {
  border-collapse: collapse;
  font-size: 12px;
}

.artifact-preview-table th,
.artifact-preview-table td {
  border: 1px solid #e0e0e0;
  padding: 4px 8px;
  text-align: left;
  vertical-align: top;
  white-space: pre-wrap;
}

.artifact-preview-table th {
  position: sticky;
  top: 0;
  background: #f3f4f6;
  font-weight: 600;
}

.artifact-preview-footer {
  display: flex;
  justify-content: flex-end;
  padding: 10px 16px;
  border-top: 1px solid #eee;
}

.artifact-preview-download {
  padding: 6px 14px;
  border: 1px solid #007bff;
  border-radius: 6px;
  background: #007bff;
  color: #fff;
  font-size: 13px;
  cursor: pointer;
}

.artifact-preview-download:disabled {
  opacity: 0.6;
  cursor: default;
}
</style>
