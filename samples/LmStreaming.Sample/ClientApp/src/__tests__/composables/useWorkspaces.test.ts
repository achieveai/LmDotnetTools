import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useWorkspaces } from '@/composables/useWorkspaces';
import { WorkspaceRevisionConflictError } from '@/api/workspacesApi';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const response = (body: unknown, ok = true) =>
  Promise.resolve({ ok, status: ok ? 200 : 502, statusText: ok ? 'OK' : 'Bad Gateway', json: async () => body });

const gateway = { canonicalBaseUrl: 'http://remote:3000', appId: 'sample', available: true, error: null };
const workspace = (
  id: string,
  compatibility: 'compatible' | 'incompatible' | 'unavailable' | 'unknown' = 'compatible'
) => ({
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

/**
 * Named function with a void return, NOT `beforeEach(() => fetchMock.mockReset())`: `mockReset()`
 * returns the mock itself, and Vitest treats a function returned from `beforeEach` as a TEARDOWN
 * callback — so that concise form calls `fetch()` after every test and awaits what it returns,
 * which hangs the whole file for the hook timeout as soon as one test leaves a deferred response
 * installed.
 */
function resetFetch(): void {
  fetchMock.mockReset();
}

/**
 * Drains the microtask queue AND one timer turn, so anything that was going to settle on the
 * responses released so far already has. Placing one of these between two releases is what stops a
 * concurrency test passing by accident: without it both responses land in the same drain, and an
 * implementation that reads state too early still happens to read the right value.
 */
const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

/** A fetch whose response the test releases by hand, so requests can be settled out of order. */
function deferred() {
  let release!: (body: unknown) => void;
  const promise = new Promise((resolve) => {
    release = (body) => resolve({ ok: true, status: 200, statusText: 'OK', json: async () => body });
  });
  return { promise, release };
}

describe('useWorkspaces gateway-scoped state', () => {
  beforeEach(resetFetch);

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

  /**
   * The list and the gateway envelope are cleared because they are now known to be stale. The
   * SELECTION is not: a request that failed reported nothing about the workspace the user picked.
   * See the constraint-3 block below for why keeping it matters at the write.
   */
  it('clears stale list but keeps the selection when the API fails', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default'), workspace('old')] }))
      .mockRejectedValueOnce(new Error('network down'));
    const state = useWorkspaces();

    await state.loadWorkspaces();
    await state.loadWorkspaces();

    expect(state.workspaces.value).toEqual([]);
    expect(state.gateway.value).toBeNull();
    expect(state.selectedWorkspaceId.value).toBe('default');
    expect(state.error.value).toContain('network down');
  });

  it('refuses to select a workspace the catalog checked and rejected', async () => {
    fetchMock.mockReturnValue(response({
      gateway,
      workspaces: [workspace('default'), workspace('bad', 'incompatible')],
    }));
    const state = useWorkspaces();

    await state.loadWorkspaces();
    state.selectWorkspace('bad');

    expect(state.selectedWorkspaceId.value).toBe('default');
  });
});

/**
 * #459. `unavailable` (and the retired `unknown` spelling an older backend still sends) means the
 * gateway catalog could not be read, so NOTHING about the workspace was checked. That is not a
 * refusal, and the picker must stay usable on a host where it is the permanent answer — a
 * gateway-less runner, where `/api/marketplaces` returns 503 forever.
 */
describe('useWorkspaces unavailable-vs-incompatible', () => {
  beforeEach(resetFetch);

  it.each(['unavailable', 'unknown'] as const)(
    'selects and keeps a %s workspace, because nothing was checked',
    async (compatibility) => {
      fetchMock.mockReturnValue(response({
        gateway: { ...gateway, available: false, error: 'gateway offline' },
        workspaces: [workspace('default', compatibility), workspace('repo', compatibility)],
      }));
      const state = useWorkspaces();

      await state.loadWorkspaces();
      expect(state.selectedWorkspaceId.value).toBe('default');

      state.selectWorkspace('repo');
      expect(state.selectedWorkspaceId.value).toBe('repo');
    }
  );

  it('still withholds a workspace the catalog checked and rejected', async () => {
    fetchMock.mockReturnValue(response({
      gateway,
      workspaces: [workspace('bad', 'incompatible'), workspace('repo', 'unavailable')],
    }));
    const state = useWorkspaces();

    await state.loadWorkspaces();

    // The reconciliation skipped `bad` and landed on the unverified row instead — which is the whole
    // point: only `incompatible` is a reason to pass a workspace over.
    expect(state.selectedWorkspaceId.value).toBe('repo');

    state.selectWorkspace('bad');
    expect(state.selectedWorkspaceId.value).toBe('repo');
  });
});

/**
 * #459 binding constraint: no flow may erase an existing workspace binding because the picker had no
 * rows. An empty picker and an explicit "not this one" must stay distinguishable all the way to the
 * write — `ChatLayout.provisionThread` reads a null `selectedWorkspaceId` as "no preference" and
 * binds the new conversation to the default workspace, so a selection silently dropped here is a
 * stored binding silently rewritten there.
 *
 * The cases are asserted TOGETHER on purpose. Any one alone is satisfied by an implementation that
 * never clears, or by one that always clears; only the set pins that the difference between "could
 * not check" and "checked and refused" is what drives the decision.
 */
describe('useWorkspaces empty picker cannot clobber a stored workspace binding', () => {
  beforeEach(resetFetch);

  it('keeps the binding when the catalog can vouch for nothing', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default'), workspace('repo')] }))
      .mockReturnValueOnce(response({
        gateway: { ...gateway, available: false, error: 'gateway offline' },
        workspaces: [workspace('default', 'unavailable'), workspace('repo', 'unavailable')],
      }));
    const state = useWorkspaces();

    await state.loadWorkspaces();
    state.selectWorkspace('repo');
    expect(state.selectedWorkspaceId.value).toBe('repo');

    // The gateway goes away underneath the user: every row is now unverifiable.
    await state.loadWorkspaces();

    expect(state.selectedWorkspaceId.value).toBe('repo');
  });

  it('drops the binding when the catalog checked that workspace and refused it', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default'), workspace('repo')] }))
      .mockReturnValueOnce(response({
        gateway,
        workspaces: [workspace('default'), workspace('repo', 'incompatible')],
      }));
    const state = useWorkspaces();

    await state.loadWorkspaces();
    state.selectWorkspace('repo');
    expect(state.selectedWorkspaceId.value).toBe('repo');

    await state.loadWorkspaces();

    expect(state.selectedWorkspaceId.value).toBe('default');
  });

  it('drops the binding when the workspace is gone from the catalog entirely', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default'), workspace('repo')] }))
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default')] }));
    const state = useWorkspaces();

    await state.loadWorkspaces();
    state.selectWorkspace('repo');
    expect(state.selectedWorkspaceId.value).toBe('repo');

    await state.loadWorkspaces();

    expect(state.selectedWorkspaceId.value).toBe('default');
  });
});

describe('useWorkspaces plugin-selection conflicts', () => {
  beforeEach(resetFetch);

  const conflict = () =>
    Promise.resolve({
      ok: false,
      status: 409,
      statusText: 'Conflict',
      json: async () => ({
        error: 'stale',
        code: 'workspace_revision_conflict',
        expectedRevision: 1,
        actualRevision: 4,
      }),
    });

  /**
   * RED if `updateWorkspace` stops re-listing on conflict (drop the `loadWorkspaces()` call in the
   * `WorkspaceRevisionConflictError` branch): only the initial load + the failed PUT would be
   * observed, so a retry would replay the same stale revision forever.
   */
  it('refreshes the workspace list after a 409 so a retry carries the current revision', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 1 }] }))
      .mockReturnValueOnce(conflict())
      .mockReturnValueOnce(response({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 4 }] }));
    const state = useWorkspaces();
    await state.loadWorkspaces();

    const error = await state
      .updateWorkspace('repo', { marketplaces: [], pluginSelection: [], pluginsRevision: 1 })
      .catch((e: unknown) => e);

    expect(error).toBeInstanceOf(WorkspaceRevisionConflictError);
    // The refresh actually happened: three fetches (load, failed PUT, re-load) and the revision the
    // form will now read back is the server's current one.
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(state.workspaces.value[0].pluginsRevision).toBe(4);
    // Actionable, not a bare code — this string is what ChatLayout hands to showFormError().
    expect(error).toBeInstanceOf(Error);
    expect((error as Error).message).toContain('changed elsewhere');
    // ...and it states the consequence the user actually experiences: ChatLayout re-seeds the open
    // form from the refreshed workspace (the only way to stop the next click clobbering the other
    // writer through a now-valid CAS token), which DISCARDS their pending change. A message that
    // only said the list was "refreshed" would leave that discard silent.
    expect((error as Error).message).toContain('discarded');
    expect((error as Error).message).toContain('re-apply');
  });

  it('does not silently retry the update after a conflict', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('repo')] }))
      .mockReturnValueOnce(conflict())
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('repo')] }));
    const state = useWorkspaces();
    await state.loadWorkspaces();

    await state
      .updateWorkspace('repo', { marketplaces: [], pluginSelection: [], pluginsRevision: 1 })
      .catch(() => undefined);

    const puts = fetchMock.mock.calls.filter((call) => call[1]?.method === 'PUT');
    expect(puts).toHaveLength(1);
  });

  /**
   * The reload the 409 branch performs can be superseded, and then it applies NOTHING. Awaiting it
   * is therefore not enough: `updateWorkspace` would reject while the list still held the
   * pre-conflict `pluginsRevision`, ChatLayout would `reseedEditForm()` from that stale data, the
   * winning load would land afterwards with nobody left to re-seed the form, and the user's retry
   * would submit the revision that already conflicted.
   *
   * The assertion is taken AT REJECTION TIME, not afterwards, because that is the instant the caller
   * re-seeds from. Note the `flush()` between the two releases: it is what makes this test
   * non-vacuous. Releasing both back to back lets the winner apply in the same microtask drain in
   * which the broken code rejects, so the broken code sees the winning revision by luck and passes.
   * The flush parks the run at exactly the moment the broken code has rejected and the winner has
   * not yet answered.
   */
  it('does not reject the conflict until the winning load has applied', async () => {
    const supersededReload = deferred();
    const winningLoad = deferred();
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 1 }] }))
      .mockReturnValueOnce(conflict())
      .mockReturnValueOnce(supersededReload.promise)
      .mockReturnValueOnce(winningLoad.promise);
    const state = useWorkspaces();
    await state.loadWorkspaces();

    let revisionAtRejection: number | undefined;
    const update = state
      .updateWorkspace('repo', { marketplaces: [], pluginSelection: [], pluginsRevision: 1 })
      .catch((e: unknown) => {
        revisionAtRejection = state.workspaces.value[0].pluginsRevision;
        return e;
      });

    // The PUT has failed and the 409 branch's reload is in flight; supersede it.
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    const newerLoad = state.loadWorkspaces();

    // The superseded reload answers first, carrying the revision the conflict was about, and
    // everything that was going to settle on it settles now.
    supersededReload.release({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 1 }] });
    await flush();

    // Only now does the winner answer with the revision a retry must carry.
    winningLoad.release({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 9 }] });
    await newerLoad;

    expect(await update).toBeInstanceOf(WorkspaceRevisionConflictError);
    expect(revisionAtRejection).toBe(9);
  });

  /**
   * Converging once is not enough: the wait must follow the chain. Here the 409's reload is
   * superseded, and the load that supersedes it is superseded in turn, so a fix that waits exactly
   * one step further still re-seeds the form from a list nobody applied.
   */
  it('follows a chain of supersessions until one load actually applies', async () => {
    const first = deferred();
    const second = deferred();
    const third = deferred();
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 1 }] }))
      .mockReturnValueOnce(conflict())
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise)
      .mockReturnValueOnce(third.promise);
    const state = useWorkspaces();
    await state.loadWorkspaces();

    let revisionAtRejection: number | undefined;
    const update = state
      .updateWorkspace('repo', { marketplaces: [], pluginSelection: [], pluginsRevision: 1 })
      .catch((e: unknown) => {
        revisionAtRejection = state.workspaces.value[0].pluginsRevision;
        return e;
      });
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));

    // Supersede the 409's own reload, then let it answer and everything settle on it.
    const secondLoad = state.loadWorkspaces();
    first.release({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 1 }] });
    await flush();

    // Supersede its successor too, before that one answers: a one-step wait stops here, on a list
    // nobody applied.
    const thirdLoad = state.loadWorkspaces();
    second.release({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 4 }] });
    await flush();

    third.release({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 12 }] });
    await Promise.all([secondLoad, thirdLoad]);

    expect(await update).toBeInstanceOf(WorkspaceRevisionConflictError);
    expect(revisionAtRejection).toBe(12);
  });

  /**
   * The bound. If loads keep starting, following the chain never converges — and an unbounded wait
   * inside `updateWorkspace` means ChatLayout's `catch` never runs, so the form stays open over a
   * failed save with no error shown and no way to know it failed. Give up after a fixed number of
   * passes and still surface the conflict.
   *
   * Against an unbounded implementation this test does not fail an assertion, it TIMES OUT — the
   * `await update` never settles. That is the defect, so it is the right failure.
   */
  it('gives up after a bounded number of supersessions and still surfaces the conflict', async () => {
    const pending: ReturnType<typeof deferred>[] = [];
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 1 }] }))
      .mockReturnValueOnce(conflict())
      .mockImplementation(() => {
        const next = deferred();
        pending.push(next);
        return next.promise;
      });
    const state = useWorkspaces();
    await state.loadWorkspaces();

    const update = state
      .updateWorkspace('repo', { marketplaces: [], pluginSelection: [], pluginsRevision: 1 })
      .catch((e: unknown) => e);
    await vi.waitFor(() => expect(pending).toHaveLength(1));

    // Every time the awaited load answers, a newer one has already started: the chain never settles.
    // More rounds than the bound, so the wait must be what stops, not the test.
    const started: Promise<void>[] = [];
    for (let round = 0; round < 8; round++) {
      started.push(state.loadWorkspaces());
      pending[round].release({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 1 }] });
      await flush();
    }

    expect(await update).toBeInstanceOf(WorkspaceRevisionConflictError);
    pending[8]?.release({ gateway, workspaces: [{ ...workspace('repo'), pluginsRevision: 1 }] });
    await Promise.all(started);
  });
});

/**
 * Two loads in flight at once is the normal case, not an exotic one: a mount racing a post-409
 * reload, or two reloads from overlapping mutations. Responses can land in any order, so every write
 * is gated on still being the latest load.
 *
 * These tests resolve the requests DELIBERATELY OUT OF ORDER — the first request completes last —
 * which is the only ordering that distinguishes a guarded implementation from an unguarded one. A
 * test that resolves them in order passes either way.
 */
describe('useWorkspaces concurrent load ordering', () => {
  beforeEach(resetFetch);

  it('ignores a stale response that lands after a newer one', async () => {
    const first = deferred();
    const second = deferred();
    fetchMock.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const state = useWorkspaces();

    const firstLoad = state.loadWorkspaces();
    const secondLoad = state.loadWorkspaces();

    // Newer request answers first and wins.
    second.release({ gateway, workspaces: [workspace('default'), workspace('fresh')] });
    await secondLoad;
    expect(state.workspaces.value.map((w) => w.id)).toEqual(['default', 'fresh']);

    // The older one lands afterwards carrying a list that is no longer true. It must not be applied.
    first.release({ gateway, workspaces: [workspace('default'), workspace('stale')] });
    await firstLoad;

    expect(state.workspaces.value.map((w) => w.id)).toEqual(['default', 'fresh']);
  });

  /**
   * The failure mode with teeth: the stale response carries an older `pluginsRevision`. Applying it
   * makes the next edit submit a revision the server has already moved past, and the user gets a
   * conflict with nothing on screen to explain it.
   */
  it('keeps the newer pluginsRevision when a stale response arrives last', async () => {
    const first = deferred();
    const second = deferred();
    fetchMock.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const state = useWorkspaces();

    const firstLoad = state.loadWorkspaces();
    const secondLoad = state.loadWorkspaces();

    second.release({ gateway, workspaces: [{ ...workspace('default'), pluginsRevision: 9 }] });
    await secondLoad;

    first.release({ gateway, workspaces: [{ ...workspace('default'), pluginsRevision: 4 }] });
    await firstLoad;

    expect(state.workspaces.value[0].pluginsRevision).toBe(9);
  });

  /**
   * `isLoading` is gated too. A stale response clearing it would advertise "settled" while the
   * newest request is still in flight — and that flag is what the workspace form's interaction
   * guards key off, so the user would be handed back a form over a list still being replaced.
   */
  it('does not clear isLoading when a stale response settles before the newest request', async () => {
    const first = deferred();
    const second = deferred();
    fetchMock.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const state = useWorkspaces();

    const firstLoad = state.loadWorkspaces();
    const secondLoad = state.loadWorkspaces();
    expect(state.isLoading.value).toBe(true);

    first.release({ gateway, workspaces: [workspace('default')] });
    await firstLoad;

    expect(state.isLoading.value).toBe(true);

    second.release({ gateway, workspaces: [workspace('default')] });
    await secondLoad;

    expect(state.isLoading.value).toBe(false);
  });

  /**
   * A stale FAILURE is the worst of the three: its catch clears the workspace list and the selection
   * outright, so an older request failing after a newer one succeeded would blank a healthy UI.
   */
  it('ignores a stale rejection that lands after a newer success', async () => {
    let failFirst!: (e: Error) => void;
    const first = new Promise((_, reject) => {
      failFirst = reject;
    });
    const second = deferred();
    fetchMock.mockReturnValueOnce(first).mockReturnValueOnce(second.promise);
    const state = useWorkspaces();

    const firstLoad = state.loadWorkspaces();
    const secondLoad = state.loadWorkspaces();

    second.release({ gateway, workspaces: [workspace('default'), workspace('fresh')] });
    await secondLoad;

    failFirst(new Error('stale network failure'));
    await firstLoad;

    expect(state.workspaces.value.map((w) => w.id)).toEqual(['default', 'fresh']);
    expect(state.selectedWorkspaceId.value).toBe('default');
    expect(state.error.value).toBeNull();
  });
});

/**
 * `createWorkspace` selects what it just created — but `await loadWorkspaces()` resolving is NOT a
 * promise that the created workspace is selectable. Two distinct ways it is not, covered separately
 * below, because they are different bugs behind the same line:
 *
 *  (a) the reload was SUPERSEDED and applied nothing, so the catalog on screen is a different
 *      load's, chosen without any knowledge of the new workspace;
 *  (b) the reload applied normally, but the catalog it applied does not list the new workspace, or
 *      lists it as incompatible with the gateway.
 *
 * Either way an unconditional `selectedWorkspaceId.value = workspace.id` overwrites the selection
 * the winning load reconciled with one that breaks that load's invariant. The reviewer's reported
 * symptom — `selectedWorkspace` going null — only happens in the ABSENT case; in the incompatible
 * case the computed matches on id alone, so it stays non-null and the UI shows a usable-looking
 * workspace that `useChat` will then submit as the workspace for the next conversation.
 */
describe('useWorkspaces created-workspace selection', () => {
  beforeEach(resetFetch);

  const create = () => ({ name: 'new', marketplaces: [] });

  /**
   * (a) Superseded reload. Resolved deliberately out of order: the NEWER load answers first and
   * wins, and the reload `createWorkspace` is awaiting lands afterwards carrying a catalog that does
   * list the new workspace — but that response is discarded, so it cannot justify the selection.
   */
  it('does not select the created workspace when its reload was superseded', async () => {
    const supersededReload = deferred();
    const winningLoad = deferred();
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default')] }))
      .mockReturnValueOnce(response(workspace('new')))
      .mockReturnValueOnce(supersededReload.promise)
      .mockReturnValueOnce(winningLoad.promise);
    const state = useWorkspaces();
    await state.loadWorkspaces();

    const created = state.createWorkspace(create());
    // Wait for the POST to have completed and the reload to be in flight before racing it.
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    const newerLoad = state.loadWorkspaces();

    // The newer load answers first and wins; its catalog has no 'new'.
    winningLoad.release({ gateway, workspaces: [workspace('default')] });
    await newerLoad;

    // The superseded reload lands last. It "knows" about 'new', but it applied nothing.
    supersededReload.release({ gateway, workspaces: [workspace('default'), workspace('new')] });
    await created;

    expect(state.workspaces.value.map((w) => w.id)).toEqual(['default']);
    expect(state.selectedWorkspaceId.value).toBe('default');
  });

  /**
   * (b) The reload applied, and the catalog it applied marks the new workspace incompatible. This is
   * the worse of the two symptoms: the selection is non-null and renders a name, so nothing on
   * screen says the workspace cannot be used.
   */
  it('does not select a created workspace the applied catalog marks incompatible', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default')] }))
      .mockReturnValueOnce(response(workspace('new')))
      .mockReturnValueOnce(response({
        gateway,
        workspaces: [workspace('default'), workspace('new', 'incompatible')],
      }));
    const state = useWorkspaces();
    await state.loadWorkspaces();

    await state.createWorkspace(create());

    expect(state.selectedWorkspaceId.value).toBe('default');
    // The invariant that matters, stated directly: whatever is selected is usable.
    expect(state.selectedWorkspace.value?.compatibility).toBe('compatible');
  });

  /**
   * The positive control. Without it, simply never selecting anything would satisfy both tests
   * above, and `createWorkspace` would silently stop doing the one thing it exists to do.
   */
  it('selects the created workspace when the applied catalog lists it as compatible', async () => {
    fetchMock
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default')] }))
      .mockReturnValueOnce(response(workspace('new')))
      .mockReturnValueOnce(response({ gateway, workspaces: [workspace('default'), workspace('new')] }));
    const state = useWorkspaces();
    await state.loadWorkspaces();

    await state.createWorkspace(create());

    expect(state.selectedWorkspaceId.value).toBe('new');
  });
});
