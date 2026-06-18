import type { Workspace, WorkspaceCreate, WorkspaceUpdate } from '@/types/workspace';

/**
 * Fetches all workspaces from the backend.
 */
export async function listWorkspaces(): Promise<Workspace[]> {
  const response = await fetch('/api/workspaces');
  if (!response.ok) {
    throw new Error(`Failed to fetch workspaces: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Fetches a specific workspace by ID. Returns null on 404.
 */
export async function getWorkspace(id: string): Promise<Workspace | null> {
  const response = await fetch(`/api/workspaces/${encodeURIComponent(id)}`);
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    throw new Error(`Failed to fetch workspace: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Creates a new user-defined workspace. Surfaces the server's `error` body on a
 * non-ok response.
 */
export async function createWorkspace(dto: WorkspaceCreate): Promise<Workspace> {
  const response = await fetch('/api/workspaces', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(dto),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || `Failed to create workspace: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Updates a workspace's marketplaces. Surfaces the server's `error` body on a
 * non-ok response.
 */
export async function updateWorkspace(
  id: string,
  dto: WorkspaceUpdate
): Promise<Workspace> {
  const response = await fetch(`/api/workspaces/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(dto),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || `Failed to update workspace: ${response.statusText}`);
  }
  return response.json();
}
