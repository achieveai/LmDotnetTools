import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  createWorkspace,
  updateWorkspace,
  UnsupportedPluginsError,
  WorkspaceRevisionConflictError,
} from '@/api/workspacesApi';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const ok = (body: unknown = { id: 'ws' }) =>
  Promise.resolve({ ok: true, status: 200, statusText: 'OK', json: async () => body });

const fail = (status: number, statusText: string, body: unknown) =>
  Promise.resolve({ ok: false, status, statusText, json: async () => body });

/** The literal string handed to `fetch` — assertions run against the WIRE, not an intermediate object. */
function sentBody(): string {
  return fetchMock.mock.calls[0][1].body as string;
}

function sentJson(): Record<string, unknown> {
  return JSON.parse(sentBody()) as Record<string, unknown>;
}

describe('workspacesApi plugin-selection tri-state serialization', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    fetchMock.mockReturnValue(ok());
  });

  // RED when the production line becomes `pluginSelection: dto.pluginSelection ?? []` (or `|| []`):
  // the null would serialize as `[]`, silently turning "all plugins" into "no plugins".
  it('transmits an explicit null as null, not as an empty list', async () => {
    await updateWorkspace('ws', { marketplaces: ['demo'], pluginSelection: null, pluginsRevision: 3 });

    expect(sentBody()).toContain('"pluginSelection":null');
    expect(sentJson().pluginSelection).toBeNull();
  });

  // RED when the production line becomes `pluginSelection: dto.pluginSelection ?? null`: the empty
  // list survives `??` unchanged, so this case pins the OTHER direction — that `[]` is not dropped
  // or widened back into "no preference".
  it('transmits an explicit empty list as [], distinct from null', async () => {
    await updateWorkspace('ws', { marketplaces: ['demo'], pluginSelection: [], pluginsRevision: 3 });

    expect(sentBody()).toContain('"pluginSelection":[]');
    const parsed = sentJson();
    expect(parsed.pluginSelection).toEqual([]);
    expect(parsed.pluginSelection).not.toBeNull();
  });

  it('transmits a non-empty subset verbatim', async () => {
    await updateWorkspace('ws', {
      marketplaces: ['demo'],
      pluginSelection: [{ marketplace: 'demo', plugin: 'toolkit' }],
      pluginsRevision: 7,
    });

    expect(sentJson().pluginSelection).toEqual([{ marketplace: 'demo', plugin: 'toolkit' }]);
    expect(sentJson().pluginsRevision).toBe(7);
  });

  // The four-state contract's fourth state. RED when the production line becomes
  // `pluginSelection: dto.pluginSelection ?? null` — the key would then always be PRESENT, so the
  // backend could never distinguish "leave unchanged" from "clear to legacy all-plugins", and every
  // marketplace-only edit would wipe the stored selection.
  it('OMITS pluginSelection from the body entirely when the caller does not supply it', async () => {
    await updateWorkspace('ws', { marketplaces: ['demo'] });

    expect('pluginSelection' in sentJson()).toBe(false);
    expect(sentBody()).not.toContain('pluginSelection');
  });

  it('omits pluginSelection on create when unset, and keeps null distinct from [] when set', async () => {
    await createWorkspace({ name: 'A', marketplaces: [] });
    expect('pluginSelection' in sentJson()).toBe(false);

    fetchMock.mockReset();
    fetchMock.mockReturnValue(ok());
    await createWorkspace({ name: 'B', marketplaces: [], pluginSelection: [] });
    expect(sentBody()).toContain('"pluginSelection":[]');

    fetchMock.mockReset();
    fetchMock.mockReturnValue(ok());
    await createWorkspace({ name: 'C', marketplaces: [], pluginSelection: null });
    expect(sentBody()).toContain('"pluginSelection":null');
  });

  it('always sends marketplaces on update (an absent list clears them server-side)', async () => {
    await updateWorkspace('ws', { marketplaces: ['demo', 'superpowers'] });

    expect(sentJson().marketplaces).toEqual(['demo', 'superpowers']);
  });
});

describe('workspacesApi typed failures', () => {
  beforeEach(() => fetchMock.mockReset());

  // RED if the 409 branch is dropped: the caller would get a bare Error and could not tell a stale
  // revision from any other failure, so it would never refresh.
  it('maps 409 workspace_revision_conflict to WorkspaceRevisionConflictError with both revisions', async () => {
    fetchMock.mockReturnValue(
      fail(409, 'Conflict', {
        error: 'stale',
        code: 'workspace_revision_conflict',
        expectedRevision: 2,
        actualRevision: 5,
      })
    );

    const error = await updateWorkspace('ws', {
      marketplaces: [],
      pluginSelection: [],
      pluginsRevision: 2,
    }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(WorkspaceRevisionConflictError);
    expect((error as WorkspaceRevisionConflictError).expectedRevision).toBe(2);
    expect((error as WorkspaceRevisionConflictError).actualRevision).toBe(5);
  });

  it('reports an omitted revision (sentinel -1) as a conflict, distinct from a stale one', async () => {
    fetchMock.mockReturnValue(
      fail(409, 'Conflict', {
        error: 'missing',
        code: 'workspace_revision_conflict',
        expectedRevision: -1,
        actualRevision: 4,
      })
    );

    const error = (await updateWorkspace('ws', {
      marketplaces: [],
      pluginSelection: [],
    }).catch((e: unknown) => e)) as WorkspaceRevisionConflictError;

    expect(error).toBeInstanceOf(WorkspaceRevisionConflictError);
    expect(error.expectedRevision).toBe(-1);
  });

  // RED if the 400 branch is dropped: the message would be the raw `error` text with no plugin names.
  it('maps 400 unsupported_plugins to UnsupportedPluginsError naming the offending plugins', async () => {
    fetchMock.mockReturnValue(
      fail(400, 'Bad Request', {
        error: 'nope',
        code: 'unsupported_plugins',
        unsupportedPlugins: [{ marketplace: 'demo', plugin: 'ghost' }],
        availablePlugins: [{ marketplace: 'demo', plugin: 'toolkit' }],
      })
    );

    const error = (await updateWorkspace('ws', {
      marketplaces: ['demo'],
      pluginSelection: [{ marketplace: 'demo', plugin: 'ghost' }],
      pluginsRevision: 0,
    }).catch((e: unknown) => e)) as UnsupportedPluginsError;

    expect(error).toBeInstanceOf(UnsupportedPluginsError);
    expect(error.unsupportedPlugins).toEqual(['demo/ghost']);
    expect(error.message).toContain('demo/ghost');
  });

  it('maps 400 unsupported_plugins on create too', async () => {
    fetchMock.mockReturnValue(
      fail(400, 'Bad Request', { error: 'nope', code: 'unsupported_plugins', unsupportedPlugins: [] })
    );

    const error = await createWorkspace({
      name: 'A',
      pluginSelection: [{ marketplace: 'demo', plugin: 'ghost' }],
    }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(UnsupportedPluginsError);
  });

  it('falls back to the server error text for an unrecognised failure', async () => {
    fetchMock.mockReturnValue(fail(400, 'Bad Request', { error: 'Name is required.' }));

    const error = (await updateWorkspace('ws', { marketplaces: [] }).catch(
      (e: unknown) => e
    )) as Error;

    expect(error).not.toBeInstanceOf(UnsupportedPluginsError);
    expect(error.message).toBe('Name is required.');
  });
});
