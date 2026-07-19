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
  // root carry NO ?path; delete carries ?path=<name>). We capture the live threadId from the first files
  // request and match uploads/deletes/reloads to the EXACT conversation + path so a stray request can
  // never be mistaken for this test's mutation.
  let threadId = null;
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
    if (p && !threadId) {
      threadId = p.convId;
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

  let sessionEstablished = false;
  let workspaceLabel = '';
  let initialEntryCount = 0;
  let uploadedName = '';
  let uploadVisible = false;
  let previewText = '';
  let deleteConfirmed = false;
  // Every filename we ever POST (even ones whose row timed out): a server-side upload can succeed after
  // the row-wait times out, so we must reconcile ALL of them in cleanup, not just the final uploadedName.
  const attemptedNames = [];
  // {name, status} for each awaited upload POST — proves each POST resolved before cleanup runs.
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
        ok('6 file browser modal opened + listing resolved (attempt ' + attempt + ')');
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

  // 8. Upload a fresh file. Await the EXACT upload POST each attempt so NO POST is still in flight when
  //    the step ends (an unawaited POST can complete during cleanup and orphan a file). Record every POST
  //    + its status so a server-side success is reconciled in cleanup even if its row never rendered.
  if (sessionEstablished) {
    try {
      let attempt = 0;
      while (attempt < 3 && !uploadVisible) {
        attempt++;
        const name = `e2e-live-${Date.now()}-${Math.floor(Math.random() * 1e6)}.txt`;
        attemptedNames.push(name);
        // Arm the exact upload-POST matcher BEFORE dispatching change so we never miss it.
        const postP = page
          .waitForResponse((r) => r.request().method() === 'POST' && isFilesReq(r.url(), null), {
            timeout: 20000,
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
        // Name collision -> overwrite confirm; no POST fires, so skip existing and retry a new name.
        const overwrite = tid('file-browser-overwrite-confirm');
        if (await overwrite.isVisible().catch(() => false)) {
          await tid('file-browser-overwrite-cancel').click().catch(() => {});
          continue;
        }
        // Await the POST so it is fully resolved before we proceed (closes the in-flight-upload race).
        const postResp = await postP;
        const status = postResp ? postResp.status() : 0;
        uploadResults.push({ name, status });
        if (status === 200 || status === 201) {
          // The file exists server-side now; its row may still be settling. Record it either way so
          // cleanup reconciles it, and only flip uploadVisible once the row actually renders.
          uploadedName = name;
          try {
            await tid(`file-entry-${name}`).waitFor({ state: 'visible', timeout: 15000 });
            uploadVisible = true;
          } catch {
            // POST succeeded but the row didn't render in time; cleanup still reconciles it.
          }
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
      // Wait for the reload to finish RENDERING (loading gone + list back) so the row check runs against
      // the completed listing, not the momentarily-unmounted one.
      await tid('file-browser-loading').waitFor({ state: 'detached', timeout: 15000 }).catch(() => {});
      await tid('file-browser-list').waitFor({ state: 'visible', timeout: 15000 }).catch(() => {});
      const rowGone = !(await tid(`file-entry-${uploadedName}`).isVisible().catch(() => false));
      const errVisible = await tid('file-browser-error').isVisible().catch(() => false);
      if (delStatus === 204 && reloadOk && rowGone && !errVisible) {
        deleteConfirmed = true;
        ok('10 deleted ' + uploadedName + ' (DELETE 204, reload ok, row gone, no error)');
      } else {
        fail(
          `10 delete: delStatus=${delStatus} reloadOk=${reloadOk} rowGone=${rowGone} errorVisible=${errVisible} — not a confirmed delete`
        );
      }
    } catch (e) {
      fail('10 delete: ' + (e?.message ?? e));
    }
  } else {
    fail('10 delete skipped — nothing uploaded');
  }

  // 10b. Cleanup: reconcile EVERY uploaded artifact regardless of row visibility (a POST can succeed
  //      server-side after its row-wait timed out, and the UI delete above only touches the one row).
  //      Issue an exact-path DELETE via fetch for each attempted name (skipping the primary already
  //      confirmed-deleted); 204 (deleted) and 404 (never existed / already gone) are both resolved.
  //      Anything else is an unresolved orphan and is pushed to failures so pass:true can't hide a leak.
  try {
    const toClean = attemptedNames.filter((n) => !(n === uploadedName && deleteConfirmed));
    if (toClean.length > 0 && !threadId) {
      fail('10b cleanup: threadId never captured — cannot reconcile ' + toClean.join(', '));
    } else {
      for (const name of toClean) {
        const status = await page.evaluate(
          async ({ t, nm }) => {
            const url = `/api/conversations/${encodeURIComponent(t)}/files?path=${encodeURIComponent(nm)}`;
            try {
              return (await fetch(url, { method: 'DELETE' })).status;
            } catch {
              return -1;
            }
          },
          { t: threadId, nm: name }
        );
        if (status !== 204 && status !== 404) {
          fail(`10b cleanup: ${name} not reconciled (DELETE status ${status}) — possible orphan in workspace`);
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
