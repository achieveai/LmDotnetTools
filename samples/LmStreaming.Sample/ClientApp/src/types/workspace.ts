/**
 * One plugin within one marketplace. Mirrors the backend `PluginRef` exactly — the gateway keys a
 * plugin by the pair, because two marketplaces may publish the same plugin name.
 */
export interface PluginRef {
  marketplace: string;
  plugin: string;
}

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
  /**
   * Whether the gateway catalog vouches for this workspace's marketplaces — and, just as
   * importantly, whether it was consulted at all:
   * - `compatible` — checked, and every selected marketplace exists;
   * - `incompatible` — checked, and it names marketplaces the gateway does not offer. The ONLY
   *   value that is a reason to withhold the row (see {@link isWorkspaceWithheld});
   * - `unavailable` — the catalog could not be read, so nothing was checked. Not a "no".
   * - `unknown` — the retired value a server predating the split emits for what is now
   *   `unavailable`. Kept in the union so an older backend is read correctly rather than falling
   *   into whatever the last `else` happens to be; treated identically to `unavailable` everywhere.
   *
   * Do not re-conflate these by testing `!== 'compatible'`: that is exactly the read that left a
   * gateway-less host with a permanently unselectable picker.
   */
  compatibility: 'compatible' | 'incompatible' | 'unavailable' | 'unknown';
  unsupportedMarketplaces: string[];
  /**
   * Explicit per-plugin selection. **TRI-STATE — never coerce with `?? []` or `|| []`:**
   * - `null` (or absent, from a backend that predates the field) = the workspace expressed no
   *   preference, so ALL plugins of its enabled marketplaces are on (legacy behaviour);
   * - `[]` = explicitly NO plugins;
   * - non-empty = exactly that subset.
   *
   * Collapsing `null` to `[]` silently turns "all plugins" into "no plugins".
   */
  pluginSelection?: PluginRef[] | null;
  /**
   * Monotonic revision of {@link pluginSelection}, bumped by the backend on every explicit
   * selection change. Echoed back as the compare-and-swap token on update (see
   * {@link WorkspaceUpdate.pluginsRevision}); a stale value is rejected with HTTP 409.
   */
  pluginsRevision?: number;
}

/**
 * True only when the catalog was actually consulted and REFUSED this workspace.
 *
 * The one definition of "withhold this row", shared by the picker's `disabled` binding and by
 * `useWorkspaces`' selection reconciliation, so the two can never drift into disagreeing about what
 * is choosable. `unavailable`/`unknown` are deliberately NOT withheld: the check did not run, and a
 * check that did not run is not a rejection.
 */
export function isWorkspaceWithheld(workspace: Workspace): boolean {
  return workspace.compatibility === 'incompatible';
}

/** Negation of {@link isWorkspaceWithheld}, for call sites that read better in the positive. */
export function isWorkspaceSelectable(workspace: Workspace): boolean {
  return !isWorkspaceWithheld(workspace);
}

/**
 * True when the catalog could not be consulted, so the row is shown with a caveat rather than
 * silently presented as verified. Covers the retired `unknown` spelling as well as `unavailable`.
 */
export function isWorkspaceUnverified(workspace: Workspace): boolean {
  return workspace.compatibility === 'unavailable' || workspace.compatibility === 'unknown';
}

export interface WorkspaceGateway {
  canonicalBaseUrl: string;
  appId: string;
  available: boolean;
  error: string | null;
}

export interface WorkspaceListResponse {
  gateway: WorkspaceGateway;
  workspaces: Workspace[];
}

/**
 * Request body for POST /api/workspaces. Directory + marketplaces are optional;
 * the backend derives a directory from the name when one is not supplied.
 */
export interface WorkspaceCreate {
  name: string;
  directoryRelPath?: string;
  marketplaces?: string[];
  /**
   * Seed plugin selection, tri-state exactly as on {@link Workspace.pluginSelection}. Omitting the
   * property and sending `null` both mean "no preference" at creation time; `[]` means explicitly
   * no plugins and is NOT the same thing.
   */
  pluginSelection?: PluginRef[] | null;
}

/**
 * Request body for PUT /api/workspaces/{id}. Editing is marketplaces + plugin selection only —
 * name and directory cannot change after creation.
 *
 * `pluginSelection` is **FOUR-STATE**, and the fourth state is expressed by the property being
 * ABSENT from the serialized JSON (which is why it is optional AND nullable here, and why
 * `workspacesApi` must never write `pluginSelection: dto.pluginSelection ?? null`):
 * - absent → leave the stored selection unchanged;
 * - `null` → clear back to legacy "all plugins of the enabled marketplaces";
 * - `[]` → explicitly no plugins;
 * - non-empty → exactly that subset.
 *
 * `marketplaces`, by contrast, is NOT four-state on the wire: the backend DTO defaults it to an
 * empty list, so omitting it CLEARS every marketplace. It stays REQUIRED here so that wipe can
 * never happen by accident.
 */
export interface WorkspaceUpdate {
  marketplaces: string[];
  pluginSelection?: PluginRef[] | null;
  /**
   * Compare-and-swap token echoed from {@link Workspace.pluginsRevision}. The backend makes it
   * MANDATORY whenever `pluginSelection` is set and ignores it otherwise; omitting it there is
   * reported back as a conflict against the sentinel revision `-1`.
   */
  pluginsRevision?: number;
}

/**
 * A marketplace that can be enabled on a workspace. The options are sourced at runtime from the
 * gateway catalog (GET /api/marketplaces); `id` is the marketplace alias the sandbox-create request
 * expects, and `displayName` is what the multi-select renders.
 */
export interface MarketplaceDescriptor {
  id: string;
  displayName: string;
  /**
   * Plugin names this marketplace publishes, in catalog order. Empty when the gateway failed to
   * load the marketplace (`CatalogMarketplace.error` non-null) — an empty list therefore means
   * "nothing to choose from", never "no plugins enabled".
   */
  plugins: string[];
  /**
   * The gateway's load error for this marketplace, or `null`. Non-null means {@link plugins} is
   * empty because enumeration FAILED, not because the marketplace is empty — a distinction the UI
   * must show, since materializing a selection while this marketplace is enabled writes down every
   * OTHER marketplace's plugins and silently leaves this one's out.
   */
  error: string | null;
}
