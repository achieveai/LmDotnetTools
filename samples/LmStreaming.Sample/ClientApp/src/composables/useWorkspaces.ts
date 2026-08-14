import { ref, computed } from 'vue';
import type {
  Workspace,
  WorkspaceCreate,
  WorkspaceGateway,
  WorkspaceUpdate,
} from '@/types/workspace';
import {
  listWorkspaces,
  createWorkspace as apiCreateWorkspace,
  updateWorkspace as apiUpdateWorkspace,
  WorkspaceRevisionConflictError,
} from '@/api/workspacesApi';

const DEFAULT_WORKSPACE_ID = 'default';

/**
 * Composable that loads the workspace catalog and tracks the user's currently
 * selected workspace for the next new conversation.
 *
 * Mirrors useProviders: the selection is process-local and only matters until a
 * thread is created, after which the backend treats the workspace as immutable.
 * After the first message we read the locked value from the conversation's
 * metadata instead.
 */
export function useWorkspaces() {
  const workspaces = ref<Workspace[]>([]);
  const gateway = ref<WorkspaceGateway | null>(null);
  const selectedWorkspaceId = ref<string | null>(DEFAULT_WORKSPACE_ID);
  const isLoading = ref(false);
  const error = ref<string | null>(null);

  /**
   * Monotonic id of the most recently STARTED load. Concurrent `loadWorkspaces()` calls are routine
   * — a mount racing a post-409 reload, or two reloads from overlapping mutations — and responses
   * can arrive in any order. Without this, a slow earlier request that lands last overwrites the
   * newer list and, worse, the newer `pluginsRevision`; the next edit then submits a stale revision
   * and fails with a conflict the user cannot explain.
   *
   * Every write below is gated on still being the latest, INCLUDING the `isLoading` reset: a stale
   * response clearing the flag would advertise "settled" while the newest request is still in
   * flight, which is exactly the window the form-interaction guards depend on.
   */
  let loadGeneration = 0;

  /**
   * Workspace currently chosen for the next new conversation.
   */
  const selectedWorkspace = computed(() =>
    workspaces.value.find((w) => w.id === selectedWorkspaceId.value) ?? null
  );

  /**
   * Loads the workspace catalog. Keeps the current selection if it still exists,
   * otherwise falls back to the default workspace (or the first available).
   */
  async function loadWorkspaces(): Promise<void> {
    const generation = ++loadGeneration;
    isLoading.value = true;
    error.value = null;
    try {
      const response = await listWorkspaces();
      if (generation !== loadGeneration) return;
      gateway.value = response.gateway ?? null;
      workspaces.value = Array.isArray(response.workspaces) ? response.workspaces : [];

      const hasSelection =
        selectedWorkspaceId.value !== null &&
        workspaces.value.some(
          (w) => w.id === selectedWorkspaceId.value && w.compatibility === 'compatible'
        );
      if (!hasSelection) {
        const initial =
          workspaces.value.find(
            (w) => w.id === DEFAULT_WORKSPACE_ID && w.compatibility === 'compatible'
          )?.id
          ?? workspaces.value.find((w) => w.compatibility === 'compatible')?.id
          ?? null;
        selectedWorkspaceId.value = initial;
      }
    } catch (e) {
      if (generation !== loadGeneration) return;
      workspaces.value = [];
      gateway.value = null;
      selectedWorkspaceId.value = null;
      error.value = e instanceof Error ? e.message : 'Failed to load workspaces';
      console.error('Failed to load workspaces:', e);
    } finally {
      if (generation === loadGeneration) {
        isLoading.value = false;
      }
    }
  }

  /**
   * Selects a workspace for new conversations. No-op for unknown ids so the UI
   * can defensively pass user input without leaving the dropdown stale.
   */
  function selectWorkspace(id: string): void {
    if (
      !gateway.value?.available
      || !workspaces.value.some((w) => w.id === id && w.compatibility === 'compatible')
    ) {
      return;
    }
    selectedWorkspaceId.value = id;
  }

  /**
   * Creates a new workspace, reloads the catalog, and selects the new entry.
   *
   * Selection goes through `selectWorkspace` rather than assigning the id directly, because
   * `await loadWorkspaces()` resolving does NOT mean the created workspace is selectable. The reload
   * may have been superseded by a newer load and applied nothing, or the catalog that did win may
   * not list the new workspace at all (another writer removed it) or may mark it incompatible with
   * the gateway. Assigning the id unconditionally in those cases would overwrite the selection the
   * winning load just reconciled with one that violates the invariant that load maintains — and the
   * damage is not cosmetic: `selectedWorkspaceId` is what ChatLayout hands to `useChat` as the
   * workspace for the next conversation, so an incompatible id is submitted to the backend.
   * Reusing `selectWorkspace` keeps exactly one definition of "selectable"; when it declines, the
   * reconciled selection stands.
   */
  async function createWorkspace(dto: WorkspaceCreate): Promise<Workspace> {
    try {
      const workspace = await apiCreateWorkspace(dto);
      await loadWorkspaces();
      selectWorkspace(workspace.id);
      return workspace;
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to create workspace';
      console.error('Failed to create workspace:', e);
      throw e;
    }
  }

  /**
   * Updates a workspace's marketplaces and/or plugin selection, then reloads the catalog.
   *
   * A revision conflict (HTTP 409) means someone else changed the selection between our read and
   * our write, so our `pluginsRevision` is stale. We reload the catalog — that is what makes a
   * retry carry the CURRENT revision instead of replaying the same doomed one. Deliberately NOT
   * retried automatically: re-submitting against a selection we never showed the user would
   * silently overwrite their change.
   *
   * The reload alone is not sufficient, and on its own is actively dangerous: it refreshes the CAS
   * token while the open form still holds the pre-conflict selection, so one further click would
   * pass compare-and-swap and clobber the other writer. The caller MUST also re-seed the form from
   * the refreshed workspace (ChatLayout does, via `reseedEditForm`), which is why the message below
   * says the pending change was discarded rather than merely that the list was refreshed.
   */
  async function updateWorkspace(id: string, dto: WorkspaceUpdate): Promise<Workspace> {
    try {
      const workspace = await apiUpdateWorkspace(id, dto);
      await loadWorkspaces();
      return workspace;
    } catch (e) {
      if (e instanceof WorkspaceRevisionConflictError) {
        await loadWorkspaces();
        const refreshed = new WorkspaceRevisionConflictError(
          'This workspace was changed elsewhere, so your plugin selection was not saved. '
            + 'The form has been reloaded with the current selection and your pending change was '
            + 'discarded — re-apply it and save again.',
          e.expectedRevision,
          e.actualRevision
        );
        error.value = refreshed.message;
        console.error('Failed to update workspace:', e);
        throw refreshed;
      }
      error.value = e instanceof Error ? e.message : 'Failed to update workspace';
      console.error('Failed to update workspace:', e);
      throw e;
    }
  }

  /**
   * Look up a workspace by id. Returns null if unknown — useful for rendering a
   * locked-thread badge when the persisted workspace has since been removed.
   */
  function getWorkspaceById(id: string | null | undefined): Workspace | null {
    if (!id) return null;
    return workspaces.value.find((w) => w.id === id) ?? null;
  }

  return {
    workspaces,
    gateway,
    selectedWorkspaceId,
    selectedWorkspace,
    isLoading,
    error,
    loadWorkspaces,
    selectWorkspace,
    createWorkspace,
    updateWorkspace,
    getWorkspaceById,
  };
}
