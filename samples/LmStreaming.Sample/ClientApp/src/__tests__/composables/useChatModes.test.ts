import { describe, expect, it, vi } from 'vitest';
import { useChatModes } from '@/composables/useChatModes';
import { listChatModes } from '@/api/chatModesApi';
import type { ChatMode } from '@/types/chatMode';

vi.mock('@/api/chatModesApi', () => ({ listChatModes: vi.fn() }));

const mode = (id: string) => ({ id, name: id, isSystemDefined: true }) as ChatMode;

describe('useChatModes workspace catalog', () => {
  it('drops a builder selection when the next workspace lacks that mode', async () => {
    vi.mocked(listChatModes)
      .mockResolvedValueOnce({ modes: [mode('default'), mode('mini-web-app-builder')], canActivateMiniWebApps: true })
      .mockResolvedValueOnce({ modes: [mode('default')], canActivateMiniWebApps: true });
    const catalog = useChatModes();
    await catalog.loadModes('capable');
    catalog.selectMode('mini-web-app-builder');

    await catalog.loadModes('incapable');

    expect(catalog.currentModeId.value).toBe('default');
    expect(catalog.modes.value.map((item) => item.id)).toEqual(['default']);
  });

  it('ignores an older response after the workspace changes', async () => {
    let resolveOld!: (value: { modes: ChatMode[]; canActivateMiniWebApps: boolean }) => void;
    const oldResponse = new Promise<{ modes: ChatMode[]; canActivateMiniWebApps: boolean }>((resolve) => { resolveOld = resolve; });
    vi.mocked(listChatModes)
      .mockReturnValueOnce(oldResponse)
      .mockResolvedValueOnce({ modes: [mode('default')], canActivateMiniWebApps: true });
    const catalog = useChatModes();

    const oldLoad = catalog.loadModes('capable');
    await catalog.loadModes('incapable');
    resolveOld({ modes: [mode('default'), mode('mini-web-app-builder')], canActivateMiniWebApps: true });
    await oldLoad;

    expect(catalog.modes.value.map((item) => item.id)).toEqual(['default']);
  });
});
