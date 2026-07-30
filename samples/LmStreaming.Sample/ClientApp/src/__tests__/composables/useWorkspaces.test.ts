import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useWorkspaces } from '@/composables/useWorkspaces';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const response = (body: unknown, ok = true) =>
  Promise.resolve({ ok, status: ok ? 200 : 502, statusText: ok ? 'OK' : 'Bad Gateway', json: async () => body });

const gateway = { canonicalBaseUrl: 'http://remote:3000', appId: 'sample', available: true, error: null };
const workspace = (id: string, compatibility: 'compatible' | 'incompatible' | 'unknown' = 'compatible') => ({
  id,
  name: id,
  directoryRelPath: id,
  marketplaces: [],
  isSystemDefined: id === 'default',
  createdAt: 0,
  updatedAt: 0,
  compatibility,
  unsupportedMarketplaces: compatibility === 'incompatible' ? ['old'] : [],
});

describe('useWorkspaces gateway-scoped state', () => {
  beforeEach(() => fetchMock.mockReset());

  it('loads the envelope and selects compatible Default', async () => {
    fetchMock.mockReturnValue(response({ gateway, workspaces: [workspace('default'), workspace('repo')] }));
    const state = useWorkspaces();

    await state.loadWorkspaces();

    expect(state.gateway.value).toEqual(gateway);
    expect(state.workspaces.value.map((w) => w.id)).toEqual(['default', 'repo']);
    expect(state.selectedWorkspaceId.value).toBe('default');
  });

  it('replaces another gateway list and removes old names', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default'), workspace('old')] }))
      .mockReturnValueOnce(response({ gateway: { ...gateway, appId: 'other' }, workspaces: [workspace('default')] }));
    const state = useWorkspaces();

    await state.loadWorkspaces();
    await state.loadWorkspaces();

    expect(state.workspaces.value.map((w) => w.id)).toEqual(['default']);
    expect(state.gateway.value?.appId).toBe('other');
  });

  it('clears stale list and selection when the API fails', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default'), workspace('old')] }))
      .mockRejectedValueOnce(new Error('network down'));
    const state = useWorkspaces();

    await state.loadWorkspaces();
    await state.loadWorkspaces();

    expect(state.workspaces.value).toEqual([]);
    expect(state.gateway.value).toBeNull();
    expect(state.selectedWorkspaceId.value).toBeNull();
    expect(state.error.value).toContain('network down');
  });

  it('does not select incompatible or unavailable workspaces', async () => {
    fetchMock.mockReturnValue(response({
      gateway: { ...gateway, available: false },
      workspaces: [workspace('default', 'unknown'), workspace('bad', 'incompatible')],
    }));
    const state = useWorkspaces();

    await state.loadWorkspaces();
    state.selectWorkspace('bad');

    expect(state.selectedWorkspaceId.value).toBeNull();
  });
});
