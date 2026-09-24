import { afterEach, describe, expect, it, vi } from 'vitest';
import { launchSandboxApp, listSandboxApps, resolveMiniWebApp } from '@/api/sandboxAppsApi';

describe('sandboxAppsApi', () => {
  afterEach(() => vi.restoreAllMocks());

  it('lists only the apps returned for the current conversation', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ apps: [{ kind: 'mini-web-app', workspaceId: 'workspace-1', id: 'budget', name: 'Budget explorer', link: '#mini-app?workspace=workspace-1&app=budget' }] }), { status: 200 }),
    );

    await expect(listSandboxApps('thread/one')).resolves.toEqual([{ kind: 'mini-web-app', workspaceId: 'workspace-1', id: 'budget', name: 'Budget explorer', link: '#mini-app?workspace=workspace-1&app=budget' }]);
    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread%2Fone/apps', { signal: undefined });
  });

  it('resolves a link against the conversation workspace before opening it', async () => {
    const app = { kind: 'mini-web-app', workspaceId: 'workspace-1', id: 'budget', name: 'Budget', link: '#mini-app?workspace=workspace-1&app=budget' };
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify(app), { status: 200 }));

    await expect(resolveMiniWebApp('thread-1', 'workspace-1', 'budget')).resolves.toEqual(app);
    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/apps/budget?workspace=workspace-1', { signal: undefined });
  });

  it('hides Apps when the feature is disabled on the host', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 404 }));
    await expect(listSandboxApps('thread-1')).resolves.toEqual([]);
  });

  it('pins launch to the resolved workspace without putting a ticket in a URL', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ url: 'https://instance.apps.example/_launch', ticket: 'one-use' }), { status: 200 }),
    );

    await expect(launchSandboxApp('thread-1', 'workspace-1', 'budget')).resolves.toEqual({
      url: 'https://instance.apps.example/_launch', ticket: 'one-use',
    });
    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/apps/budget/launch?workspace=workspace-1', {
      method: 'POST', signal: undefined,
    });
  });

  it('rejects an unsafe launch URL before a browser form can submit the ticket', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ url: 'http://example.net/_launch', ticket: 'one-use' }), { status: 200 }),
    );

    await expect(launchSandboxApp('thread-1', 'workspace-1', 'budget')).rejects.toThrow('Invalid app launch');
  });
});
