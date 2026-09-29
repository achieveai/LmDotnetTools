import { apiFetch } from '@/api/http';

export type SandboxApp = { kind: 'mini-web-app'; workspaceId: string; id: string; name: string; link: string };
export type SandboxAppLaunch = { url: string; ticket: string };

function appsUrl(threadId: string): string {
  return `/api/conversations/${encodeURIComponent(threadId)}/apps`;
}

export async function listSandboxApps(threadId: string, signal?: AbortSignal): Promise<SandboxApp[]> {
  const response = await apiFetch(appsUrl(threadId), { signal });
  if (response.status === 404) return [];
  if (!response.ok) throw new Error(`Could not list sandbox apps (${response.status})`);
  const body = await response.json() as { apps?: unknown };
  if (!Array.isArray(body.apps)) throw new Error('Invalid sandbox app list');
  return body.apps.filter(isSandboxApp);
}

function isSandboxApp(value: unknown): value is SandboxApp {
  if (typeof value !== 'object' || value === null) return false;
  const app = value as Partial<SandboxApp>;
  return app.kind === 'mini-web-app' && typeof app.workspaceId === 'string'
    && typeof app.id === 'string' && typeof app.name === 'string' && typeof app.link === 'string';
}

export async function resolveMiniWebApp(
  threadId: string, workspaceId: string, appId: string, signal?: AbortSignal,
): Promise<SandboxApp> {
  const url = `${appsUrl(threadId)}/${encodeURIComponent(appId)}?workspace=${encodeURIComponent(workspaceId)}`;
  const response = await apiFetch(url, { signal });
  if (!response.ok) throw new Error(`Could not open Mini Web App (${response.status})`);
  const body: unknown = await response.json();
  if (!isSandboxApp(body) || body.workspaceId !== workspaceId || body.id !== appId)
    throw new Error('Invalid Mini Web App');
  return body;
}

export async function launchSandboxApp(
  threadId: string,
  workspaceId: string,
  appId: string,
  signal?: AbortSignal,
): Promise<SandboxAppLaunch> {
  const response = await apiFetch(`${appsUrl(threadId)}/${encodeURIComponent(appId)}/launch?workspace=${encodeURIComponent(workspaceId)}`, {
    method: 'POST', signal,
  });
  if (!response.ok) throw new Error(`Could not launch sandbox app (${response.status})`);
  const body = await response.json() as Partial<SandboxAppLaunch>;
  let url: URL;
  try { url = new URL(body.url ?? ''); }
  catch { throw new Error('Invalid app launch'); }
  if (url.protocol !== 'https:' || url.pathname !== '/_launch' || url.search || url.hash
    || url.username || url.password || typeof body.ticket !== 'string' || !body.ticket) {
    throw new Error('Invalid app launch');
  }
  return { url: url.href, ticket: body.ticket };
}
