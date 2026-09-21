import type { Ref } from 'vue';

/**
 * The in-page href a workspace file link is rewritten to (see `parseMarkdown`'s `workspaceLinks`).
 *
 * A model links files by whatever path it was told -- usually the workspace's absolute HOST path
 * (`B:\ws\docs\a.md`), sometimes a `file://` URI or a relative path. The client cannot turn that into a
 * workspace-relative path: only the server knows the host path. So the link keeps the raw target plus
 * the conversation whose workspace it belongs to, and the server resolves it on click
 * (`GET /api/conversations/{threadId}/files/resolve?target=`).
 *
 * It is a `#fragment` on purpose: DOMPurify keeps it (a `file:` or `B:\` href is dropped), and an
 * un-intercepted click -- or a middle-click -- never navigates the chat away.
 */
export const WORKSPACE_LINK_PREFIX = '#workspace-file?';

/** The class the renderer puts on a rewritten link; the click handler selects on it. */
export const WORKSPACE_LINK_CLASS = 'workspace-link';

/**
 * provide/inject key (mirrors GO_TO_AGENT_TAB): ChatLayout provides the conversation whose workspace
 * links resolve against and the function that opens the preview. TextMessage injects it; without a
 * provider (or before a conversation exists) links render plain.
 */
export const WORKSPACE_FILE_LINKS = 'workspaceFileLinks';

export interface WorkspaceFileLinksContext {
  /** The conversation owning the workspace; null while a new chat has no conversation yet. */
  threadId: Readonly<Ref<string | null>>;
  open: (link: WorkspaceLinkRef) => void;
}

export interface WorkspaceLinkRef {
  threadId: string;
  /** The link destination exactly as the renderer received it (marked may have percent-encoded it). */
  target: string;
}

export function buildWorkspaceLinkHref({ threadId, target }: WorkspaceLinkRef): string {
  const query = new URLSearchParams({ thread: threadId, target });
  return WORKSPACE_LINK_PREFIX + query.toString();
}

/** Returns null for anything that is not a well-formed workspace link href. */
export function parseWorkspaceLinkHref(href: string): WorkspaceLinkRef | null {
  if (!href.startsWith(WORKSPACE_LINK_PREFIX)) return null;
  const query = new URLSearchParams(href.slice(WORKSPACE_LINK_PREFIX.length));
  const threadId = query.get('thread');
  const target = query.get('target');
  return threadId && target ? { threadId, target } : null;
}

// A protocol-relative `//host/...` is a web link too: the browser resolves it against the page's scheme.
const WEB_URL = /^(?:https?:|\/\/)/i;
const OTHER_SCHEME_LEFT_IN_PLACE = /^(?:mailto:|tel:)/i;

/**
 * True for http(s) and protocol-relative links, which open in a new tab. Leading and trailing whitespace
 * is ignored, as the browser ignores it when it follows the href.
 */
export function isWebUrl(href: string): boolean {
  return WEB_URL.test(href.trim());
}

/**
 * True when an href in an assistant message should be treated as a workspace file. Web, mailto and tel
 * links and genuine in-page anchors are left alone; a `#workspace-file?` anchor the MODEL wrote is not
 * trusted as-is (it could name another conversation) and is rewritten like any other target.
 */
export function isWorkspaceLinkCandidate(href: string): boolean {
  const trimmed = href.trim();
  if (trimmed.length === 0 || isWebUrl(trimmed) || OTHER_SCHEME_LEFT_IN_PLACE.test(trimmed)) return false;
  if (trimmed.startsWith('#')) return trimmed.startsWith(WORKSPACE_LINK_PREFIX);
  return true;
}

/**
 * An href that already names its own location, so a base directory must never be prefixed to it: a rooted
 * POSIX or Windows path, a drive-qualified path, any URI scheme (`file:`, `sandbox:`, …), or an in-page
 * anchor. Everything else is a plain relative path.
 *
 * A single letter before the colon is a DRIVE, and the scheme alternative needs two or more characters, so
 * `B:/ws/a.md` matches as a drive rather than as a scheme. A colon later in the string is an ordinary POSIX
 * file-name character (`notes/a:b.md`) and is not matched at all -- the pattern is anchored.
 */
const ALREADY_ROOTED = /^(?:[/\\]|[A-Za-z]:|[A-Za-z][A-Za-z0-9+.-]+:|#)/;

/**
 * Joins `baseDir` onto a PLAIN RELATIVE `href`, which is how a link written inside a previewed file is meant
 * to be read -- `evidence/x.md` inside `docs/rdb/spec.md` names `docs/rdb/evidence/x.md`, not a file at the
 * workspace root. Only the client knows which file a rendered link came from, so the join has to happen here;
 * the server resolves whatever it is handed against the workspace root.
 *
 * The join is deliberately naive concatenation: `..` segments are left in place for the server's
 * `WorkspaceLinkResolver`, which normalises them and REFUSES one that climbs above the workspace root. So the
 * containment rule stays server-side and the worst a misjudgement here can do is fail to resolve.
 *
 * `baseDir` is a server-resolved path rather than model text, while `href` arrives percent-encoded from
 * `marked` and the server decodes the joined string exactly once -- so the base's segments are encoded on the
 * way in, or a directory whose real name contains `%20` would come back out as a space.
 */
export function resolveAgainstBaseDir(href: string, baseDir?: string): string {
  if (!baseDir) return href;
  let relative = href.trim();
  if (ALREADY_ROOTED.test(relative)) return href;
  while (relative.startsWith('./')) relative = relative.slice(2);
  const base = baseDir
    .split('/')
    .filter(Boolean)
    .map(encodeURIComponent)
    .join('/');
  return base.length === 0 ? relative : `${base}/${relative}`;
}
