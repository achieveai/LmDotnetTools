// file-browser-live.mjs — single-call LIVE end-to-end UX test of the workspace File Browser.
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/file-browser-live.mjs" })
//
// Drives the REAL running app (:5000) + REAL sandbox gateway (:3000) with a REAL Anthropic call.
// Exercises: new chat -> provider(anthropic) -> mode(workspace-agent) -> send "hi" (provisions the
// sandbox session) -> open Files modal -> upload -> preview -> delete -> close. Every step is
// defensive (try/catch + timeouts) and records a failure instead of throwing, so the return object
// shows exactly WHICH steps passed. Assert only deterministic browser-observable DOM/testids.
async (page) => {
  const BASE = 'http://127.0.0.1:5000/';
  const steps = [];
  const failures = [];
  const ok = (m) => steps.push('ok: ' + m);
  const fail = (m) => {
    steps.push('FAIL: ' + m);
    failures.push(m);
  };
  const tid = (id) => page.locator(`[data-testid="${id}"]`);

  // --- exact network correlation (predicates run in the Playwright/Node scope) ---
  // The file endpoints are /api/conversations/{threadId}/files[?path=<rel>] (upload POST + reload GET at
  // root carry NO ?path; delete carries ?path=<name>). `threadId` is bound to the conversation whose
  // listing resolved the modal (step 6), NOT the first response seen — a stale response from a reused
  // page must not latch the wrong thread. Until then we only track the LATEST files conversation id.
  let threadId = null;
  let latestConvId = null;
  const parseFilesUrl = (url) => {
    try {
      const u = new URL(url);
      const m = u.pathname.match(/\/api\/conversations\/([^/]+)\/files$/);
      return m ? { convId: decodeURIComponent(m[1]), path: u.searchParams.get('path') } : null;
    } catch {
      return null;
    }
  };
  page.on('response', (resp) => {
    const p = parseFilesUrl(resp.url());
    if (p) {
      latestConvId = p.convId;
    }
  });
  // expectPath: undefined = any files request; null = root (no ?path); string = that exact ?path value.
  const isFilesReq = (url, expectPath) => {
    const p = parseFilesUrl(url);
    if (!p || (threadId && p.convId !== threadId)) {
      return false;
    }
    if (expectPath === undefined) {
      return true;
    }
    return expectPath === null ? p.path === null : p.path === expectPath;
  };

  // Upload-POST settlement tracking. A `waitForResponse` timeout only ABANDONS the waiter; the browser's
  // POST can still be in flight and land AFTER cleanup, re-creating a just-deleted file. So we track every
  // upload POST request to a terminal state (response finished / request failed) and DRAIN them before
  // cleanup — no reconcile happens while any upload is unresolved.
  const uploadInFlight = new Set();
  const uploadSettlers = [];
  page.on('request', (req) => {
    if (req.method() === 'POST' && isFilesReq(req.url(), null)) {
      uploadInFlight.add(req);
      const settled = req
        .response()
        .then((r) => (r ? r.finished() : null))
        .catch(() => null)
        .then(() => uploadInFlight.delete(req));
      // Cap the wait so a truly hung request leaves req in the set (→ cleanup fails loudly) instead of
      // hanging Promise.all forever.
      uploadSettlers.push(Promise.race([settled, new Promise((res) => setTimeout(res, 30000))]));
    }
  });

  // Names of the file rows currently rendered (row testids only, not the per-action buttons).
  const currentEntryNames = () =>
    page.evaluate(() =>
      [...document.querySelectorAll('[data-testid^="file-entry-"]')]
        .map((n) => n.getAttribute('data-testid'))
        .filter((t) => t && !/^file-entry-(name|preview|delete|download|lossy)-/.test(t))
        .map((t) => t.replace(/^file-entry-/, ''))
    );

  let sessionEstablished = false;
  let workspaceLabel = '';
  let initialEntryCount = 0;
  let uploadedName = '';
  let uploadVisible = false;
  let previewText = '';
  let deleteConfirmed = false;
  // {name, status} for each awaited upload POST — observability only; cleanup is driven off the server's
  // authoritative listing (below), never off client-recorded names.
  const uploadResults = [];

  // 1. Load the chat UI.
  try {
    await page.goto(BASE, { waitUntil: 'domcontentloaded' });
    await tid('chat-input-textarea').waitFor({ state: 'visible', timeout: 30000 });
    ok('1 chat UI loaded');
  } catch (e) {
    fail('1 chat UI load: ' + (e?.message ?? e));
  }

  // 2. Fresh conversation (sets the active thread the Files button is gated on).
  try {
    const newChat = page.getByRole('button', { name: '+ New Chat' });
    if (await newChat.count()) {
      await newChat.first().click();
      ok('2 clicked + New Chat');
    } else {
      ok('2 + New Chat not present (composer already active)');
    }
  } catch (e) {
    fail('2 new chat: ' + (e?.message ?? e));
  }

  // 3. Select provider anthropic BEFORE sending.
  try {
    await tid('provider-selector-button').click({ timeout: 10000 });
    await tid('provider-option-anthropic').click({ timeout: 10000 });
    ok('3 selected provider anthropic');
  } catch (e) {
    fail('3 select provider: ' + (e?.message ?? e));
  }

  // 4. Select Workspace Agent mode.
  try {
    await tid('mode-selector-button').click({ timeout: 10000 });
    await tid('mode-option-workspace-agent').click({ timeout: 10000 });
    ok('4 selected mode workspace-agent');
  } catch (e) {
    fail('4 select mode: ' + (e?.message ?? e));
  }

  // 5. Send "hi" to provision the sandbox session; wait for stop-button OR assistant bubble (<=40s).
  try {
    await tid('chat-input-textarea').fill('hi');
    await tid('send-button').click({ timeout: 10000 });
    const stop = tid('stop-button').waitFor({ state: 'visible', timeout: 40000 }).then(() => 'stop');
    const bubble = tid('assistant-text').first().waitFor({ state: 'visible', timeout: 40000 }).then(() => 'bubble');
    const which = await Promise.race([stop, bubble]).catch((e) => 'none:' + (e?.message ?? e));
    if (which === 'stop' || which === 'bubble') {
      ok('5 send provisioned (' + which + ' appeared)');
    } else {
      fail('5 send: neither stop-button nor assistant-text within 40s (' + which + ')');
    }
  } catch (e) {
    fail('5 send: ' + (e?.message ?? e));
  }

  // 6. Open the file browser once the button is ENABLED, and resolve the listing. The workspace
  //    binding + sandbox session are persisted asynchronously at agent construction (a couple seconds
  //    AFTER send), and the FileBrowser only queries once on mount — so if the modal opens before the
  //    binding lands it renders a stale no-session. Beat that race deterministically: re-open the modal
  //    (which re-mounts FileBrowser and re-queries) until the listing resolves.
  try {
    await page
      .locator('[data-testid="file-browser-button"]:not([disabled])')
      .waitFor({ state: 'visible', timeout: 20000 });
    const deadline = Date.now() + 40000;
    let attempt = 0;
    let opened = false;
    while (Date.now() < deadline && !opened) {
      attempt++;
      if (!(await tid('file-browser-modal').isVisible().catch(() => false))) {
        await tid('file-browser-button').click();
        await tid('file-browser-modal').waitFor({ state: 'visible', timeout: 10000 });
      }
      await Promise.race([
        tid('file-browser-list').waitFor({ state: 'visible', timeout: 8000 }).catch(() => {}),
        tid('file-browser-no-session').waitFor({ state: 'visible', timeout: 8000 }).catch(() => {}),
      ]);
      if (
        (await tid('file-browser-list').isVisible().catch(() => false)) &&
        !(await tid('file-browser-error').isVisible().catch(() => false))
      ) {
        opened = true;
        // Bind threadId to the conversation whose listing JUST resolved the modal (the latest files
        // response), NOT the first response ever seen — this is the correlation anchor for every later
        // exact matcher and for cleanup.
        threadId = latestConvId;
        ok('6 file browser modal opened + listing resolved (attempt ' + attempt + ', thread ' + threadId + ')');
        break;
      }
      // Still no-session: close + reopen to re-mount FileBrowser and re-query after the binding lands.
      await tid('file-browser-modal-close').click().catch(() => {});
      await tid('file-browser-modal').waitFor({ state: 'detached', timeout: 5000 }).catch(() => {});
      await page.waitForTimeout(2500);
    }
    if (!opened) {
      fail('6 open modal: listing never resolved (still no-session after ' + attempt + ' re-opens)');
    }
  } catch (e) {
    fail('6 open modal: ' + (e?.message ?? e));
  }

  // 7. Capture session state, workspace label, and initial entries.
  try {
    // Give the initial listing a moment to resolve (session vs no-session vs list).
    await Promise.race([
      tid('file-browser-list').waitFor({ state: 'visible', timeout: 15000 }).catch(() => {}),
      tid('file-browser-no-session').waitFor({ state: 'visible', timeout: 15000 }).catch(() => {}),
    ]);
    const state = await page.evaluate(() => {
      const noSession = !!document.querySelector('[data-testid="file-browser-no-session"]');
      const hasList = !!document.querySelector('[data-testid="file-browser-list"]');
      // The list can render ALONGSIDE an error (load() clears entries + sets error), so a visible list
      // is NOT proof of success — a 500/503 must not be reported as "session OK".
      const hasError = !!document.querySelector('[data-testid="file-browser-error"]');
      const ws = document.querySelector('[data-testid="file-browser-workspace"]');
      const entries = [...document.querySelectorAll('[data-testid^="file-entry-"]')]
        .map((n) => n.getAttribute('data-testid'))
        // keep only the ROW testids (file-entry-<name>), not file-entry-name/preview/delete/download-*
        .filter((t) => t && !/^file-entry-(name|preview|delete|download|lossy)-/.test(t))
        .map((t) => t.replace(/^file-entry-/, ''));
      return {
        noSession,
        hasList,
        hasError,
        workspace: ws ? ws.textContent.replace(/\s+/g, ' ').trim() : '',
        entries,
      };
    });
    sessionEstablished = state.hasList && !state.noSession && !state.hasError;
    workspaceLabel = state.workspace;
    initialEntryCount = state.entries.length;
    if (state.noSession) {
      fail('7 NO SESSION — file-browser-no-session present (sandbox session NOT established)');
    } else if (state.hasError) {
      fail('7 listing ERROR — file-browser-error visible; a failed request is not a resolved session');
    } else if (state.hasList) {
      ok(`7 session OK (list present, no error, ${initialEntryCount} entries, workspace="${workspaceLabel}")`);
    } else {
      fail('7 indeterminate — neither list nor no-session rendered');
    }
  } catch (e) {
    fail('7 capture state: ' + (e?.message ?? e));
  }

  // 8. Upload a fresh file. The name is made unique against the CURRENT listing so no overwrite dialog
  //    can fire — every dispatch results in exactly one POST, which we await (settlement is also tracked
  //    globally for the cleanup barrier). Cleanup does NOT rely on the names recorded here; it reconciles
  //    against the server's authoritative listing, so a POST that lands late is still caught.
  if (sessionEstablished) {
    try {
      let attempt = 0;
      while (attempt < 3 && !uploadVisible) {
        attempt++;
        const existing = await currentEntryNames();
        let name = `e2e-live-${Date.now()}-${Math.floor(Math.random() * 1e6)}.txt`;
        while (existing.includes(name)) {
          name = `e2e-live-${Date.now()}-${Math.floor(Math.random() * 1e6)}.txt`;
        }
        // Arm the exact upload-POST matcher BEFORE dispatching change so a fast response is not missed.
        const postP = page
          .waitForResponse((r) => r.request().method() === 'POST' && isFilesReq(r.url(), null), {
            timeout: 25000,
          })
          .catch(() => null);
        // Inject the file via a browser-side DataTransfer — the Playwright runner has no Node `Buffer`
        // and no dynamic import, but the page has File/Blob/DataTransfer. Assigning input.files and
        // dispatching 'change' drives the Vue @change upload handler exactly like a real file pick.
        await page.evaluate((fileName) => {
          const input = document.querySelector('[data-testid="file-browser-file-input"]');
          const file = new File(['hello from a live e2e upload\nsecond line'], fileName, {
            type: 'text/plain',
          });
          const dt = new DataTransfer();
          dt.items.add(file);
          input.files = dt.files;
          input.dispatchEvent(new Event('change', { bubbles: true }));
        }, name);
        // A unique name must NOT collide — an overwrite dialog here means a bug. Fail fast (and stop, so
        // the armed waiter can't bleed into a later attempt).
        if (await tid('file-browser-overwrite-confirm').isVisible().catch(() => false)) {
          await tid('file-browser-overwrite-cancel').click().catch(() => {});
          fail('8 upload: unexpected overwrite dialog for unique name ' + name);
          break;
        }
        const postResp = await postP;
        const status = postResp ? postResp.status() : 0;
        uploadResults.push({ name, status });
        if (status === 200 || status === 201) {
          uploadedName = name;
          uploadVisible = await tid(`file-entry-${name}`)
            .waitFor({ state: 'visible', timeout: 15000 })
            .then(() => true)
            .catch(() => false);
        }
      }
      if (uploadVisible) {
        ok('8 uploaded ' + uploadedName + ' (POST 2xx + row visible)');
      } else if (uploadedName) {
        fail('8 upload: POST succeeded for ' + uploadedName + ' but its row never rendered');
      } else {
        fail('8 upload: no successful upload POST after ' + attempt + ' attempt(s)');
      }
    } catch (e) {
      fail('8 upload: ' + (e?.message ?? e));
    }
  } else {
    fail('8 upload skipped — no session');
  }

  // 9. Preview the uploaded file and assert its text.
  if (uploadVisible) {
    try {
      await tid(`file-entry-preview-${uploadedName}`).click({ timeout: 10000 });
      await tid('file-preview-text').waitFor({ state: 'visible', timeout: 10000 });
      previewText = (await tid('file-preview-text').first().textContent())?.trim() ?? '';
      if (previewText.includes('hello from a live e2e upload')) {
        ok('9 preview text matched');
      } else {
        fail('9 preview: text did not contain expected marker (got: ' + previewText.slice(0, 80) + ')');
      }
    } catch (e) {
      fail('9 preview: ' + (e?.message ?? e));
    }
  } else {
    fail('9 preview skipped — nothing uploaded');
  }

  // 10. Delete via the UI. Row detachment ALONE is not an oracle: remove() reloads the listing in its
  //     finally, and while that GET is loading the list unmounts (v-if="isLoading"), so the row vanishes
  //     BEFORE the reload's fate is known — a failed reload would then read as a passing delete. Require:
  //     the EXACT DELETE == 204, the post-delete root reload GET == ok, loading cleared + list re-mounted,
  //     no error banner, and the target absent from the RE-RENDERED listing.
  if (uploadVisible) {
    try {
      await tid(`file-entry-delete-${uploadedName}`).click({ timeout: 10000 });
      await tid('file-browser-delete-confirm').waitFor({ state: 'visible', timeout: 10000 });
      // Arm both matchers before confirming: the exact DELETE for THIS file, and the reload GET that
      // remove()'s finally fires immediately after the DELETE resolves.
      const delP = page
        .waitForResponse(
          (r) => r.request().method() === 'DELETE' && isFilesReq(r.url(), uploadedName),
          { timeout: 15000 }
        )
        .catch(() => null);
      const reloadP = page
        .waitForResponse((r) => r.request().method() === 'GET' && isFilesReq(r.url(), null), {
          timeout: 15000,
        })
        .catch(() => null);
      await tid('file-browser-delete-confirm-btn').click({ timeout: 10000 });
      const delResp = await delP;
      const reloadResp = await reloadP;
      const delStatus = delResp ? delResp.status() : 0;
      const reloadOk = !!(reloadResp && reloadResp.ok());
      // Require the reload to actually finish RENDERING — loading gone AND the list re-mounted — as
      // explicit results in the success condition. A 200 reload that never re-mounts the list would
      // otherwise leave the row absent + no error and be mistaken for success.
      const loadingGone = await tid('file-browser-loading')
        .waitFor({ state: 'detached', timeout: 15000 })
        .then(() => true)
        .catch(() => false);
      const listVisible = await tid('file-browser-list')
        .waitFor({ state: 'visible', timeout: 15000 })
        .then(() => true)
        .catch(() => false);
      const rowGone = !(await tid(`file-entry-${uploadedName}`).isVisible().catch(() => false));
      const errVisible = await tid('file-browser-error').isVisible().catch(() => false);
      if (delStatus === 204 && reloadOk && loadingGone && listVisible && rowGone && !errVisible) {
        deleteConfirmed = true;
        ok('10 deleted ' + uploadedName + ' (DELETE 204, reload ok, list re-rendered, row gone, no error)');
      } else {
        fail(
          `10 delete: delStatus=${delStatus} reloadOk=${reloadOk} loadingGone=${loadingGone} listVisible=${listVisible} rowGone=${rowGone} errorVisible=${errVisible} — not a confirmed delete`
        );
      }
    } catch (e) {
      fail('10 delete: ' + (e?.message ?? e));
    }
  } else {
    fail('10 delete skipped — nothing uploaded');
  }

  // 10b. Authoritative cleanup. Two safety properties:
  //   (1) SETTLEMENT BARRIER — wait for every upload POST to reach a terminal state first, so a delayed
  //       POST can't create a file AFTER we reconcile (the timeout-path orphan race).
  //   (2) GROUND TRUTH — delete exactly the `e2e-live-*` files the SERVER reports (not client-recorded
  //       names, which can be stale/unobserved), then re-list and require none remain. A 404 counts as
  //       resolved ONLY when it is entry-level (`not_found`); `unknown_thread` (wrong thread) FAILS.
  try {
    await Promise.all(uploadSettlers);
    if (uploadInFlight.size > 0) {
      fail('10b cleanup: ' + uploadInFlight.size + ' upload POST(s) never settled — cannot safely reconcile');
    } else if (!threadId) {
      fail('10b cleanup: threadId never captured — cannot reconcile workspace');
    } else {
      const result = await page.evaluate(async (convId) => {
        const base = `/api/conversations/${encodeURIComponent(convId)}/files`;
        const listOnce = async () => {
          const r = await fetch(base);
          if (!r.ok) {
            return { ok: false, status: r.status, names: [] };
          }
          const body = await r.json();
          const names = (body.entries || [])
            .map((e) => e.name)
            .filter((n) => typeof n === 'string' && n.startsWith('e2e-live-'));
          return { ok: true, status: r.status, names };
        };
        const before = await listOnce();
        if (!before.ok) {
          return { phase: 'list', status: before.status };
        }
        const deletions = [];
        for (const n of before.names) {
          const dr = await fetch(`${base}?path=${encodeURIComponent(n)}`, { method: 'DELETE' });
          let code = null;
          if (dr.status !== 204) {
            try {
              code = (await dr.json()).code;
            } catch {
              /* non-JSON body */
            }
          }
          deletions.push({ name: n, status: dr.status, code });
        }
        const after = await listOnce();
        return {
          phase: 'done',
          deletions,
          remaining: after.ok ? after.names : null,
          afterStatus: after.status,
        };
      }, threadId);

      if (result.phase === 'list') {
        fail('10b cleanup: authoritative listing failed (status ' + result.status + ')');
      } else {
        for (const d of result.deletions) {
          const resolved = d.status === 204 || (d.status === 404 && d.code === 'not_found');
          if (!resolved) {
            fail(`10b cleanup: ${d.name} not reconciled (status ${d.status}, code ${d.code ?? 'n/a'})`);
          }
        }
        if (result.remaining === null) {
          fail('10b cleanup: post-cleanup listing failed (status ' + result.afterStatus + ')');
        } else if (result.remaining.length > 0) {
          fail('10b cleanup: e2e-live artifacts remain after cleanup: ' + result.remaining.join(', '));
        }
      }
    }
  } catch (e) {
    fail('10b cleanup: ' + (e?.message ?? e));
  }

  // 11. Close the modal.
  try {
    await tid('file-browser-modal-close').click({ timeout: 10000 });
    await tid('file-browser-modal').waitFor({ state: 'detached', timeout: 10000 });
    ok('11 modal closed');
  } catch (e) {
    fail('11 close modal: ' + (e?.message ?? e));
  }

  return {
    pass: failures.length === 0,
    sessionEstablished,
    workspaceLabel,
    initialEntryCount,
    threadId,
    uploadedName,
    uploadVisible,
    uploadResults,
    previewText,
    deleteConfirmed,
    steps,
    failures,
  };
}
