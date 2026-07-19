import { ref, computed } from 'vue';
import type {
  DirectoryListing,
  FileEntry,
  PreviewResult,
  UploadOutcome,
} from '@/types/fileBrowser';
import { isNoSession } from '@/types/fileBrowser';
import {
  listFiles,
  previewFile,
  downloadFile,
  uploadFile,
  deleteEntry,
  NoSessionError,
} from '@/api/fileBrowserApi';

/** One clickable breadcrumb: `name` is the segment label, `path` is the directory it navigates to. */
export interface Breadcrumb {
  name: string;
  path: string;
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

  /** Loads a text preview for a file entry, populating `previewTarget`/`previewResult`. */
  async function preview(entry: FileEntry): Promise<PreviewResult | null> {
    const threadId = getThreadId();
    if (!threadId) {
      return null;
    }
    const path = joinPath(currentPath.value, entry.name);
    try {
      const result = await previewFile(threadId, path, lifecycle.signal);
      if (disposed) {
        return null;
      }
      previewTarget.value = entry;
      previewResult.value = result;
      return result;
    } catch (e) {
      if (isCancellation(e) || disposed) {
        return null;
      }
      if (e instanceof NoSessionError) {
        noSession.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to preview file';
      }
      console.error('Failed to preview file:', e);
      return null;
    }
  }

  /** Clears the inline preview panel. */
  function clearPreview(): void {
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

  /**
   * Uploads N files into the directory the batch STARTED in, as N INDEPENDENT sequential requests
   * (one in flight at a time so a large batch never retains multiple 64 MiB buffers). The destination is
   * snapshotted before the loop, so navigating mid-batch never re-targets later files; the loop also stops
   * if the composable is disposed or the conversation switches.
   */
  async function upload(files: File[]): Promise<UploadOutcome[]> {
    const threadId = getThreadId();
    if (!threadId || files.length === 0) {
      return [];
    }
    const startPath = currentPath.value;
    try {
      const outcomes: UploadOutcome[] = [];
      for (const file of files) {
        // Stop the batch if the browser was torn down or the conversation changed mid-upload.
        if (disposed || getThreadId() !== threadId) {
          break;
        }
        outcomes.push(await uploadFile(threadId, startPath, file, lifecycle.signal));
      }
      return outcomes;
    } catch (e) {
      if (isCancellation(e) || disposed) {
        return files.map((file) => ({ name: file.name, success: false, error: 'cancelled' }));
      }
      if (e instanceof NoSessionError) {
        noSession.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to upload files';
      }
      console.error('Failed to upload files:', e);
      return files.map((file) => ({
        name: file.name,
        success: false,
        error: e instanceof Error ? e.message : 'upload_failed',
      }));
    } finally {
      await reloadIfCurrent(threadId, startPath);
    }
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
    previewTarget,
    previewResult,
    load,
    navigateTo,
    preview,
    clearPreview,
    download,
    upload,
    remove,
    cleanup,
  };
}

/** Joins a directory path and an entry name into a workspace-relative path. */
function joinPath(dir: string, name: string): string {
  return dir ? `${dir}/${name}` : name;
}
