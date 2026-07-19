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
      const result = await previewFile(threadId, path);
      previewTarget.value = entry;
      previewResult.value = result;
      return result;
    } catch (e) {
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
      await downloadFile(threadId, joinPath(currentPath.value, entry.name));
    } catch (e) {
      if (e instanceof NoSessionError) {
        noSession.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to download file';
      }
      console.error('Failed to download file:', e);
    }
  }

  /**
   * Uploads N files into the current directory as N INDEPENDENT requests, preserving each file's
   * success/failure, then reloads the listing. A session-level failure (no session / credential
   * conflict) rejects the whole batch.
   */
  async function upload(files: File[]): Promise<UploadOutcome[]> {
    const threadId = getThreadId();
    if (!threadId || files.length === 0) {
      return [];
    }
    try {
      const outcomes = await Promise.all(
        files.map((file) => uploadFile(threadId, currentPath.value, file))
      );
      return outcomes;
    } catch (e) {
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
      await load();
    }
  }

  /** Deletes an entry then reloads the listing. */
  async function remove(entry: FileEntry): Promise<void> {
    const threadId = getThreadId();
    if (!threadId) {
      return;
    }
    try {
      await deleteEntry(threadId, joinPath(currentPath.value, entry.name));
      if (previewTarget.value?.name === entry.name) {
        clearPreview();
      }
    } catch (e) {
      if (e instanceof NoSessionError) {
        noSession.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to delete entry';
      }
      console.error('Failed to delete entry:', e);
    } finally {
      await load();
    }
  }

  /** Cancels any in-flight listing fetch; call from the consumer's unmount hook. */
  function cleanup(): void {
    abortController?.abort();
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
