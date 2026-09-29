import type { Ref } from 'vue';

/** Stable chat link. The host resolves the app within the current conversation's workspace. */
export const MINI_WEB_APP_LINK_PREFIX = '#mini-app?';
export const MINI_WEB_APP_LINKS = 'miniWebAppLinks';

export interface MiniWebAppLinkRef {
  threadId: string;
  workspaceId: string;
  appId: string;
}

export interface MiniWebAppLinksContext {
  threadId: Readonly<Ref<string | null>>;
  open: (link: MiniWebAppLinkRef) => void;
}

export function parseMiniWebAppHref(href: string): Pick<MiniWebAppLinkRef, 'workspaceId' | 'appId'> | null {
  if (!href.startsWith(MINI_WEB_APP_LINK_PREFIX)) return null;
  const query = new URLSearchParams(href.slice(MINI_WEB_APP_LINK_PREFIX.length));
  const appId = query.get('app');
  const workspaceId = query.get('workspace');
  return query.size === 2 && appId !== null && workspaceId !== null
    && /^[A-Za-z0-9_-]{1,64}$/.test(appId) && /^[A-Za-z0-9_-]{1,128}$/.test(workspaceId)
    ? { workspaceId, appId }
    : null;
}
