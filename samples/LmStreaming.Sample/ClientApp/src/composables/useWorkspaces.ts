import { ref, computed } from 'vue';
import type { Workspace, WorkspaceCreate, WorkspaceUpdate } from '@/types/workspace';
import {
  listWorkspaces,
  createWorkspace as apiCreateWorkspace,
  updateWorkspace as apiUpdateWorkspace,
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
  const selectedWorkspaceId = ref<string | null>(DEFAULT_WORKSPACE_ID);
  const isLoading = ref(false);
  const error = ref<string | null>(null);

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
    isLoading.value = true;
    error.value = null;
    try {
      workspaces.value = await listWorkspaces();

      const hasSelection =
        selectedWorkspaceId.value !== null &&
        workspaces.value.some((w) => w.id === selectedWorkspaceId.value);
      if (!hasSelection) {
        const initial =
          workspaces.value.find((w) => w.id === DEFAULT_WORKSPACE_ID)?.id
          ?? workspaces.value[0]?.id
          ?? null;
        selectedWorkspaceId.value = initial;
      }
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to load workspaces';
      console.error('Failed to load workspaces:', e);
    } finally {
      isLoading.value = false;
    }
  }

  /**
   * Selects a workspace for new conversations. No-op for unknown ids so the UI
   * can defensively pass user input without leaving the dropdown stale.
   */
  function selectWorkspace(id: string): void {
    if (!workspaces.value.some((w) => w.id === id)) {
      return;
    }
    selectedWorkspaceId.value = id;
  }

  /**
   * Creates a new workspace, reloads the catalog, and selects the new entry.
   */
  async function createWorkspace(dto: WorkspaceCreate): Promise<Workspace> {
    try {
      const workspace = await apiCreateWorkspace(dto);
      await loadWorkspaces();
      selectedWorkspaceId.value = workspace.id;
      return workspace;
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to create workspace';
      console.error('Failed to create workspace:', e);
      throw e;
    }
  }

  /**
   * Updates a workspace's marketplaces, then reloads the catalog.
   */
  async function updateWorkspace(id: string, dto: WorkspaceUpdate): Promise<Workspace> {
    try {
      const workspace = await apiUpdateWorkspace(id, dto);
      await loadWorkspaces();
      return workspace;
    } catch (e) {
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
