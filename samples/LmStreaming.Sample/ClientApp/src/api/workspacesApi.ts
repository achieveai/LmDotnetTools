import type {
  Workspace,
  WorkspaceCreate,
  WorkspaceListResponse,
  WorkspaceUpdate,
} from '@/types/workspace';

/**
 * Raised on HTTP 409 `workspace_revision_conflict`: the `pluginsRevision` we echoed back is stale
 * (someone else changed the selection first) or was omitted entirely. The backend reports an
 * omitted revision as {@link expectedRevision} `-1` — no real revision can equal the sentinel, so
 * "omitted" stays distinguishable from "stale".
 */
export class WorkspaceRevisionConflictError extends Error {
  readonly expectedRevision: number | null;
  readonly actualRevision: number | null;
  constructor(
    message = 'This workspace was changed elsewhere.',
    expectedRevision: number | null = null,
    actualRevision: number | null = null
  ) {
    super(message);
    this.name = 'WorkspaceRevisionConflictError';
    this.expectedRevision = expectedRevision;
    this.actualRevision = actualRevision;
  }
}

/** Raised on HTTP 400 `unsupported_plugins`: a selected plugin is not published by any enabled marketplace. */
export class UnsupportedPluginsError extends Error {
  readonly unsupportedPlugins: string[];
  constructor(message: string, unsupportedPlugins: string[] = []) {
    super(message);
    this.name = 'UnsupportedPluginsError';
    this.unsupportedPlugins = unsupportedPlugins;
  }
}

/** Best-effort parse of a JSON error body; returns null when unreadable. */
async function readBody(response: Response): Promise<Record<string, unknown> | null> {
  try {
    return (await response.json()) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function stringOf(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function numberOf(value: unknown): number | null {
  return typeof value === 'number' ? value : null;
}

/** Reads `unsupportedPlugins` as a list of `"<marketplace>/<plugin>"`-ish display strings. */
function unsupportedPluginLabels(body: Record<string, unknown> | null): string[] {
  const raw = body?.unsupportedPlugins;
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    if (typeof entry === 'string') return entry;
    const ref = entry as { marketplace?: unknown; plugin?: unknown };
    const marketplace = stringOf(ref?.marketplace);
    const plugin = stringOf(ref?.plugin);
    if (marketplace && plugin) return `${marketplace}/${plugin}`;
    return plugin ?? marketplace ?? JSON.stringify(entry);
  });
}

/**
 * Maps a structured workspace failure to its typed error, consistently for create and update:
 * 409 `workspace_revision_conflict` → {@link WorkspaceRevisionConflictError},
 * 400 `unsupported_plugins` → {@link UnsupportedPluginsError}. Anything else falls back to the
 * server's `error` text, preserving the pre-existing generic message.
 */
async function classifyFailure(response: Response, operation: string): Promise<Error> {
  const body = await readBody(response);
  const code = stringOf(body?.code);

  if (response.status === 409 && code === 'workspace_revision_conflict') {
    return new WorkspaceRevisionConflictError(
      'This workspace was changed elsewhere, so your plugin selection was not saved.',
      numberOf(body?.expectedRevision),
      numberOf(body?.actualRevision)
    );
  }
  if (response.status === 400 && code === 'unsupported_plugins') {
    const labels = unsupportedPluginLabels(body);
    return new UnsupportedPluginsError(
      labels.length > 0
        ? `These plugins are not available in the selected marketplaces: ${labels.join(', ')}.`
        : 'One or more selected plugins are not available in the selected marketplaces.',
      labels
    );
  }
  return new Error(stringOf(body?.error) || `Failed to ${operation}: ${response.statusText}`);
}

/**
 * Fetches all workspaces from the backend.
 */
export async function listWorkspaces(): Promise<WorkspaceListResponse> {
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
 * Creates a new user-defined workspace.
 *
 * The DTO is serialized as-is so `pluginSelection` keeps its tri-state meaning: `JSON.stringify`
 * drops a key whose value is `undefined`, so an absent selection stays absent on the wire while an
 * explicit `null` and an explicit `[]` are both transmitted verbatim. Do NOT normalise it here.
 *
 * @throws {UnsupportedPluginsError} on 400 unsupported_plugins.
 */
export async function createWorkspace(dto: WorkspaceCreate): Promise<Workspace> {
  const response = await fetch('/api/workspaces', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(dto),
  });
  if (!response.ok) {
    throw await classifyFailure(response, 'create workspace');
  }
  return response.json();
}

/**
 * Updates a workspace's marketplaces and/or plugin selection.
 *
 * Serialized as-is, for the same reason as {@link createWorkspace} — and here the absent state is
 * load-bearing: an omitted `pluginSelection` means "leave the stored selection unchanged", which is
 * how a marketplace-only edit (or the whole UI when the gateway can't filter plugins) avoids
 * clobbering a selection it never showed the user.
 *
 * @throws {WorkspaceRevisionConflictError} on 409 workspace_revision_conflict.
 * @throws {UnsupportedPluginsError} on 400 unsupported_plugins.
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
    throw await classifyFailure(response, 'update workspace');
  }
  return response.json();
}
