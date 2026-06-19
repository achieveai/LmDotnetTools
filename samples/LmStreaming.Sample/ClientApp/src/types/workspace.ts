/**
 * A workspace groups a working directory and a set of enabled marketplaces. The
 * directory + name are immutable after creation; only the marketplace selection
 * can be edited (and only on user-defined workspaces).
 */
export interface Workspace {
  id: string;
  name: string;
  directoryRelPath: string;
  marketplaces: string[];
  isSystemDefined: boolean;
  createdAt: number;
  updatedAt: number;
}

/**
 * Request body for POST /api/workspaces. Directory + marketplaces are optional;
 * the backend derives a directory from the name when one is not supplied.
 */
export interface WorkspaceCreate {
  name: string;
  directoryRelPath?: string;
  marketplaces?: string[];
}

/**
 * Request body for PUT /api/workspaces/{id}. Editing is marketplaces-only — name
 * and directory cannot change after creation.
 */
export interface WorkspaceUpdate {
  marketplaces: string[];
}

/**
 * A marketplace that can be enabled on a workspace. The options are sourced at runtime from the
 * gateway catalog (GET /api/marketplaces); `id` is the marketplace alias the sandbox-create request
 * expects, and `displayName` is what the multi-select renders.
 */
export interface MarketplaceDescriptor {
  id: string;
  displayName: string;
}
