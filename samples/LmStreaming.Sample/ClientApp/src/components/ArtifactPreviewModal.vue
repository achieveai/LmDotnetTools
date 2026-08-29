<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import BaseModal from './BaseModal.vue';
import { NoSessionError, previewFile } from '@/api/fileBrowserApi';
import type { PreviewResult } from '@/types/fileBrowser';
import { parseMarkdown } from '@/utils/markdown';
import { isMarkdownArtifact } from '@/utils/todoBoard';
import { logger } from '@/utils';

/**
 * Read-only preview popup for a task's file artifact (#583, PR 5). Opened by clicking an artifact
 * chip on the work board; fetches the file through the EXISTING file-browser preview endpoint
 * (`GET /api/conversations/{threadId}/files/preview?path=...`), which owns the policy — the
 * 256 KiB / 5000-line cap, the UTF-8-only guard, and the dot-directory exclusions.
 *
 * Rendering: a `.md`/`.markdown` artifact goes through the app's existing `parseMarkdown` pipeline
 * (marked + DOMPurify, styled by the global `markdown.css` via `.markdown-content`); anything else
 * previewable renders as plain preformatted text. A non-previewable file shows the server's
 * `reason` rather than a blank box, and a conversation with no sandbox session says so — the chip
 * is data either way, so the modal explains instead of silently failing.
 */
const log = logger.forComponent('ArtifactPreviewModal');

const props = defineProps<{
  /** The conversation whose workspace the artifact lives in. */
  threadId: string;
  /** Workspace-relative path, exactly as carried on the task row. */
  path: string;
  /**
   * True while the layout reserves an expanded sidebar column on the left (#594 D6, #603 F-001):
   * the backdrop then stops at that column's edge so conversation switching stays a single click.
   * See `.artifact-preview-beside-sidebar` below for why this is geometry, not z-index.
   */
  besideSidebar?: boolean;
}>();

const emit = defineEmits<{ close: [] }>();

const isLoading = ref(true);
const result = ref<PreviewResult | null>(null);
const errorText = ref<string | null>(null);

const isMarkdown = computed(() => isMarkdownArtifact(props.path));

const previewText = computed(() =>
  result.value?.previewable && result.value.text !== undefined ? result.value.text : null
);

/** Sanitized by `parseMarkdown` itself (DOMPurify allowlist), so binding via v-html is safe. */
const renderedMarkdown = computed(() =>
  previewText.value !== null && isMarkdown.value ? parseMarkdown(previewText.value) : ''
);

/**
 * Cancels the in-flight preview read when the modal unmounts (596/F-005). Unmounting already
 * prevented a stale paint — a late response writes into dead refs — but the request itself ran to
 * completion and was discarded, up to a 256 KiB read for nothing on every conversation switch.
 */
const abort = new AbortController();

onMounted(async () => {
  try {
    result.value = await previewFile(props.threadId, props.path, abort.signal);
  } catch (e) {
    // Our own unmount-time abort is not a failure — and the component is gone, so there is nothing
    // to say it to. (`fetch` rejects an aborted call with DOMException 'AbortError'.)
    if (abort.signal.aborted) return;
    // The board is an accessory: a failed preview degrades to a message inside the modal, never
    // to an error banner over the chat. Recorded at debug like the board's own load failures.
    errorText.value =
      e instanceof NoSessionError
        ? 'This conversation has no workspace session, so the artifact cannot be previewed right now.'
        : 'Could not load the preview.';
    log.debug('Artifact preview failed', { path: props.path, error: e });
  } finally {
    if (!abort.signal.aborted) isLoading.value = false;
  }
});

onBeforeUnmount(() => abort.abort());
</script>

<template>
  <BaseModal
    :title="props.path"
    :class="{ 'artifact-preview-beside-sidebar': props.besideSidebar }"
    data-test-id="artifact-preview-modal"
    @close="emit('close')"
  >
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

      <!-- eslint-disable-next-line vue/no-v-html -- parseMarkdown sanitizes via DOMPurify -->
      <div
        v-else-if="previewText !== null && isMarkdown"
        class="markdown-content artifact-preview-markdown"
        data-testid="artifact-preview-markdown"
        v-html="renderedMarkdown"
      ></div>

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
  </BaseModal>
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

.artifact-preview-message {
  padding: 24px 16px;
  text-align: center;
  color: #666;
  font-size: 13px;
}

.artifact-preview-markdown {
  padding: 4px 8px;
  font-size: 13px;
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
</style>
