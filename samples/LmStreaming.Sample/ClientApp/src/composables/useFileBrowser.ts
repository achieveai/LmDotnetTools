import { ref, computed } from 'vue';
import type {
  DirectoryListing,
  FileEntry,
  PreviewResult,
  UploadItem,
  UploadOutcome,
  UploadProgress,
} from '@/types/fileBrowser';
import { isNoSession } from '@/types/fileBrowser';
import {
  listFiles,
  previewFile,
  downloadFile,
  uploadFile,
  deleteEntry,
  createDirectory as createDirectoryApi,
  FileBrowserError,
  NoSessionError,
} from '@/api/fileBrowserApi';

/** One clickable breadcrumb: `name` is the segment label, `path` is the directory it navigates to. */
export interface Breadcrumb {
  name: string;
  path: string;
}

/** An item queued for the sequential upload loop. `relativePath` is set only for folder uploads. */
interface PendingUpload {
  file: File;
  relativePath?: string;
}

/**
 * Drives the workspace File Browser for a conversation. Mirrors {@link useMarketplaces}: `ref` state,
 * a module-closure {@link AbortController} (so a re-load cancels the prior in-flight listing), and an
 * identity-gated `finally` so a superseded request never flips the loading flag off under the request
 * that replaced it.
 *
 * @param getThreadId Returns the current thread id (or null when there is no active conversation).
 *   A getter keeps the composable robust to the id changing between calls.
 */
export function useFileBrowser(getThreadId: () => string | null) {
  const entries = ref<FileEntry[]>([]);
  const currentPath = ref('');
  const moreCount = ref(0);
  const workspaceId = ref<string | null>(null);
  const isLoading = ref(false);
  const error = ref<string | null>(null);
  const noSession = ref(false);
  // Per-file progress for the active (sequential) upload batch; `null` when no batch is running.
  const uploadProgress = ref<UploadProgress | null>(null);
  // Number of upload batches currently queued/running. Batches are single-flight (serialized): while any
  // is active `isUploading` is true so the component disables its upload entry points and ignores drops,
  // and detached batches (repeated picks/drops, or a mixed drop's two batches) never overlap and clobber
  // the shared `uploadProgress` or race reloads.
  const pendingBatches = ref(0);
  const isUploading = computed(() => pendingBatches.value > 0);

  // Inline preview state (the panel the component renders in a <pre>).
  const previewTarget = ref<FileEntry | null>(null);
  const previewResult = ref<PreviewResult | null>(null);

  /** Root crumb + one crumb per accumulated path segment. */
  const breadcrumbs = computed<Breadcrumb[]>(() => {
    const crumbs: Breadcrumb[] = [{ name: 'root', path: '' }];
    let accumulated = '';
    for (const segment of currentPath.value.split('/').filter(Boolean)) {
      accumulated = accumulated ? `${accumulated}/${segment}` : segment;
      crumbs.push({ name: segment, path: accumulated });
    }
    return crumbs;
  });

  // Tracks the in-flight listing fetch so a re-load cancels the previous request rather than racing
  // to write a stale result.
  let abortController: AbortController | null = null;
  // Aborted on cleanup (unmount): its signal is passed to EVERY mutation/read request so an in-flight
  // upload/delete/preview/download stops sending (up to 64 MiB) and never commits state after the
  // composable is gone. `disposed` gates any state write that could still fire after cleanup.
  const lifecycle = new AbortController();
  let disposed = false;

  /** True for an aborted-request error, which must be swallowed silently (it is cancellation, not a failure). */
  function isCancellation(e: unknown): boolean {
    return e instanceof DOMException && e.name === 'AbortError';
  }

  /** (Re)loads the listing for `path` (defaults to the current path). */
  async function load(path: string = currentPath.value): Promise<void> {
    const threadId = getThreadId();
    if (!threadId) {
      // The conversation went away: abort any in-flight listing so it can't resolve later and
      // repopulate state for a thread that no longer exists, then reset to the empty state.
      abortController?.abort();
      abortController = null;
      entries.value = [];
      moreCount.value = 0;
      workspaceId.value = null;
      previewTarget.value = null;
      previewResult.value = null;
      noSession.value = false;
      error.value = null;
      isLoading.value = false;
      return;
    }

    abortController?.abort();
    const controller = new AbortController();
    abortController = controller;

    isLoading.value = true;
    error.value = null;
    try {
      const result = await listFiles(threadId, path, controller.signal);
      if (isNoSession(result)) {
        noSession.value = true;
        workspaceId.value = result.workspaceId;
        entries.value = [];
        moreCount.value = 0;
        currentPath.value = path;
      } else {
        noSession.value = false;
        applyListing(result);
      }
    } catch (e) {
      if (controller.signal.aborted) {
        return;
      }
      entries.value = [];
      error.value = e instanceof Error ? e.message : 'Failed to load files';
      console.error('Failed to load files:', e);
    } finally {
      // Only the latest request owns the loading flag.
      if (abortController === controller) {
        isLoading.value = false;
      }
    }
  }

  function applyListing(listing: DirectoryListing): void {
    entries.value = listing.entries;
    moreCount.value = listing.moreCount;
    workspaceId.value = listing.workspaceId;
    currentPath.value = listing.path;
  }

  /** Navigates into a directory path and loads its listing. Clears any open preview. */
  async function navigateTo(path: string): Promise<void> {
    clearPreview();
    await load(path);
  }

  // Monotonic token for preview requests. `preview()` captures the token it started under and commits its
  // result only if it is still the latest — so when two previews overlap (click A, then click B), a slow
  // A that resolves AFTER B can no longer overwrite B's newer selection. Bumped on clear/unmount too.
  let previewSeq = 0;
  // The in-flight preview's AbortController. A new preview / clear / thread change / unmount aborts it so
  // obsolete server work (path resolution, gateway read, transfer, JSON parse) is CANCELLED, not merely
  // discarded on arrival. Separate from the listing `abortController` and the whole-composable `lifecycle`.
  let previewAbort: AbortController | null = null;

  /** Loads a text preview for a file entry, populating `previewTarget`/`previewResult`. */
  async function preview(entry: FileEntry): Promise<PreviewResult | null> {
    const threadId = getThreadId();
    if (!threadId) {
      return null;
    }
    const path = joinPath(currentPath.value, entry.name);
    const seq = ++previewSeq;
    // Supersede any in-flight preview: abort it so its server-side work stops instead of running to
    // completion behind the newer selection.
    previewAbort?.abort();
    const controller = new AbortController();
    previewAbort = controller;
    try {
      const result = await previewFile(threadId, path, controller.signal);
      // Supersession guard: a newer preview (or a clear/unmount) has taken over — drop this stale result.
      if (disposed || seq !== previewSeq) {
        return null;
      }
      previewTarget.value = entry;
      previewResult.value = result;
      return result;
    } catch (e) {
      if (isCancellation(e) || disposed || seq !== previewSeq) {
        return null;
      }
      if (e instanceof NoSessionError) {
        noSession.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to preview file';
      }
      console.error('Failed to preview file:', e);
      return null;
    } finally {
      // Only clear the handle if this call still owns it (a newer preview may have replaced it).
      if (previewAbort === controller) {
        previewAbort = null;
      }
    }
  }

  /** Clears the inline preview panel. Also supersedes + aborts any in-flight preview so it cannot commit. */
  function clearPreview(): void {
    previewSeq++;
    previewAbort?.abort();
    previewAbort = null;
    previewTarget.value = null;
    previewResult.value = null;
  }

  /** Triggers a browser download for a file entry. */
  async function download(entry: FileEntry): Promise<void> {
    const threadId = getThreadId();
    if (!threadId) {
      return;
    }
    try {
      await downloadFile(threadId, joinPath(currentPath.value, entry.name), lifecycle.signal);
    } catch (e) {
      if (isCancellation(e) || disposed) {
        return;
      }
      if (e instanceof NoSessionError) {
        noSession.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to download file';
      }
      console.error('Failed to download file:', e);
    }
  }

  /** Uploads flat files (no relative paths) — the drag-drop/file-picker path. */
  async function upload(files: File[]): Promise<UploadOutcome[]> {
    return runUploadBatch(files.map((file) => ({ file })));
  }

  /**
   * Uploads a folder / relative-path batch: each item carries its `relativePath` (INCLUDING the leaf
   * name), sent as the multipart `relativePath` field so the server recreates the tree. Duplicate
   * basenames in different directories upload independently.
   */
  async function uploadFolder(items: UploadItem[]): Promise<UploadOutcome[]> {
    return runUploadBatch(items);
  }

  // Gate for the in-flight batch (a promise that resolves when it finishes). A new batch queues behind it
  // (single-flight) so two batches never share the single `uploadProgress` or race reloads. When idle it
  // is null, so the next batch runs WITHOUT an extra tick — its first request still fires synchronously.
  let inFlightBatch: Promise<void> | null = null;

  /**
   * Enqueues an upload batch behind any in-flight one (single-flight) and tracks it for `isUploading`.
   * The destination is snapshotted here (at enqueue) so navigating mid-batch never re-targets later files.
   */
  async function runUploadBatch(items: PendingUpload[]): Promise<UploadOutcome[]> {
    const threadId = getThreadId();
    if (!threadId || items.length === 0) {
      return [];
    }
    const startPath = currentPath.value;
    pendingBatches.value += 1;
    const predecessor = inFlightBatch;
    let release!: () => void;
    const gate = new Promise<void>((resolve) => (release = resolve));
    inFlightBatch = gate;
    try {
      // Only WAIT when another batch is already running; when idle, execute immediately so the first
      // request is issued synchronously (preserving the eager single-batch behavior).
      if (predecessor) {
        await predecessor;
      }
      return await executeUploadBatch(threadId, startPath, items);
    } finally {
      pendingBatches.value -= 1;
      release();
      if (inFlightBatch === gate) {
        inFlightBatch = null;
      }
    }
  }

  /**
   * Uploads N files into `startPath` as N INDEPENDENT sequential requests (one in flight at a time so a
   * large batch never retains multiple 64 MiB buffers). The loop stops if the composable is disposed or
   * the conversation switches. A per-file failure never aborts the batch; only a session-level failure
   * does. `outcomes` is accumulated OUTSIDE the try so a mid-batch throw preserves the per-file outcomes
   * already collected (files that genuinely uploaded stay `success`) and marks ONLY the active +
   * not-yet-attempted files failed — never the whole batch. Progress is reported per file.
   */
  async function executeUploadBatch(
    threadId: string,
    startPath: string,
    items: PendingUpload[]
  ): Promise<UploadOutcome[]> {
    const labelOf = (item: PendingUpload): string => item.relativePath ?? item.file.name;
    const outcomes: UploadOutcome[] = [];
    try {
      uploadProgress.value = { completed: 0, total: items.length, activeName: null };
      for (const item of items) {
        // Stop the batch if the browser was torn down or the conversation changed mid-upload.
        if (disposed || getThreadId() !== threadId) {
          break;
        }
        uploadProgress.value = { completed: outcomes.length, total: items.length, activeName: labelOf(item) };
        outcomes.push(await uploadFile(threadId, startPath, item.file, lifecycle.signal, item.relativePath));
        uploadProgress.value = { completed: outcomes.length, total: items.length, activeName: null };
      }
      return outcomes;
    } catch (e) {
      const cancelled = isCancellation(e) || disposed;
      if (!cancelled) {
        if (e instanceof NoSessionError) {
          noSession.value = true;
        } else {
          error.value = e instanceof Error ? e.message : 'Failed to upload files';
        }
        console.error('Failed to upload files:', e);
      }
      // Preserve the outcomes already collected; only the active + remaining items are failed/cancelled.
      const reason = cancelled ? 'cancelled' : e instanceof Error ? e.message : 'upload_failed';
      for (let i = outcomes.length; i < items.length; i += 1) {
        outcomes.push({ name: labelOf(items[i]), success: false, error: reason });
      }
      return outcomes;
    } finally {
      uploadProgress.value = null;
      await reloadIfCurrent(threadId, startPath);
    }
  }

  /**
   * Creates a directory named `name` inside the current directory, then reloads so it appears. Server
   * error codes are mapped to a user-facing {@link error} message (or {@link noSession} for a lost
   * session). Returns true on success.
   */
  async function createDirectory(name: string): Promise<boolean> {
    const threadId = getThreadId();
    if (!threadId) {
      return false;
    }
    const startPath = currentPath.value;
    error.value = null;
    try {
      await createDirectoryApi(threadId, startPath, name, lifecycle.signal);
    } catch (e) {
      if (isCancellation(e) || disposed) {
        return false;
      }
      if (e instanceof NoSessionError) {
        noSession.value = true;
      } else {
        error.value = describeCreateDirectoryError(e);
      }
      console.error('Failed to create directory:', e);
      // A failed create changed nothing, so DON'T reload: reloading would clobber the error/noSession
      // state just set here (a successful listing resets both).
      return false;
    }
    // Success: reload so the new folder appears in the current listing.
    await reloadIfCurrent(threadId, startPath);
    return true;
  }

  /** Deletes an entry (from the directory the mutation started in) then reloads the listing. */
  async function remove(entry: FileEntry): Promise<void> {
    const threadId = getThreadId();
    if (!threadId) {
      return;
    }
    const startPath = currentPath.value;
    try {
      await deleteEntry(threadId, joinPath(startPath, entry.name), lifecycle.signal);
      if (!disposed && previewTarget.value?.name === entry.name) {
        clearPreview();
      }
    } catch (e) {
      if (isCancellation(e) || disposed) {
        return;
      }
      if (e instanceof NoSessionError) {
        noSession.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to delete entry';
      }
      console.error('Failed to delete entry:', e);
    } finally {
      await reloadIfCurrent(threadId, startPath);
    }
  }

  /**
   * Reloads the listing after a mutation, but ONLY if the composable is still live AND both the active
   * thread and the current directory still match the ones the mutation ran under. A late upload/delete
   * must not restart a listing after unmount, a conversation switch, OR an in-thread navigation to a
   * different directory (which would abort the newer navigation and pull the user back to the old path).
   */
  async function reloadIfCurrent(startThreadId: string, startPath: string): Promise<void> {
    if (!disposed && getThreadId() === startThreadId && currentPath.value === startPath) {
      await load(startPath);
    }
  }

  /** Cancels any in-flight listing fetch AND in-flight mutations; call from the consumer's unmount hook. */
  function cleanup(): void {
    disposed = true;
    previewSeq++;
    previewAbort?.abort();
    previewAbort = null;
    abortController?.abort();
    lifecycle.abort();
  }

  return {
    entries,
    currentPath,
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
  };
}

/** Joins a directory path and an entry name into a workspace-relative path. */
function joinPath(dir: string, name: string): string {
  return dir ? `${dir}/${name}` : name;
}

/** Maps a create-directory failure to a concise, user-facing message based on the server's `code`. */
function describeCreateDirectoryError(e: unknown): string {
  const code = e instanceof FileBrowserError ? e.code : null;
  switch (code) {
    case 'invalid_folder_name':
      return 'That folder name isn’t allowed. Use a name without “/” or “..”.';
    case 'not_found':
      return 'The current folder no longer exists.';
    case 'not_a_directory':
      return 'The target path is not a folder.';
    case 'ambiguous_path':
    case 'invalid_path':
      return 'The folder path is invalid.';
    case 'create_directory_failed':
      return 'The folder could not be created.';
    case 'target_busy':
      return 'The workspace is busy. Try again in a moment.';
    case 'gateway_error':
      return 'The sandbox gateway is unavailable. Try again shortly.';
    default:
      return e instanceof Error ? e.message : 'Failed to create folder';
  }
}
