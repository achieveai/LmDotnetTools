// chat-file-link-preview.mjs — single-call Playwright check, against the REAL app + REAL sandbox gateway,
// of the two assistant-bubble features:
//   • file links in an assistant message open the in-app ArtifactPreviewModal (markdown rendered, CSV table,
//     image, download-only for binary, folder / outside-workspace messages); web links open in a new tab;
//   • the hover Copy button puts the message's RAW markdown on the clipboard.
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/chat-file-link-preview.mjs" })
//
// Needs: the app on BASE with the Test (Mock) provider and a reachable sandbox gateway. No LLM spend.
// Flow: resolve-or-create a dedicated workspace → provision a Workspace Agent conversation on `test` →
// send the system-prompt echo (this establishes the sandbox session and reveals the absolute HOST path the
// model is told) → upload the fixture files through the file-browser API → send the "Chat file links"
// prompt from PromptExamples.md with <HOST_PATH> substituted → click every link and assert the modal.
//
// Returns { pass, failures, steps, threadId, hostPath, links, resolveCalls, screenshots }.
async (page) => {
  const BASE = 'http://localhost:5077';
  const PROVIDER_ID = 'test';
  const MODE_ID = 'workspace-agent';
  const WORKSPACE_NAME = 'WT5 file-link check';
  const WORKSPACE_DIR = 'wt5-file-link-check';
  const SHOT_DIR = '.logs/chat-file-link-preview';
  // A Windows spelling of a workspace folder. Against the docker-compose gateway the session's HostPath is the
  // in-container `/workspace`, so any Windows path resolves as outside_workspace; against a natively spawned
  // gateway HostPath itself is a Windows folder and is used instead (see step 4). Either way it must render as a
  // link.
  const WINDOWS_HOST_DIR_WHEN_POSIX = 'B:\\sandbox-workspaces\\workspaces\\file-link-check';

  const steps = [];
  const screenshots = [];
  const resolveCalls = [];
  let threadId = null;
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const finish = (extra) => {
    const failures = steps.filter((s) => !s.pass).map((s) => s.name);
    return { pass: failures.length === 0, failures, steps, resolveCalls, screenshots, ...extra };
  };
  const shot = async (name) => {
    const path = `${SHOT_DIR}/${name}.png`;
    await page.screenshot({ path }).catch(() => {});
    screenshots.push(path);
  };

  // Fixtures. PNG is a valid 1x1 RGBA image; blob.dat is deliberately not UTF-8.
  const REPORT_MD = '# Quarterly Report\n\nRevenue is **up**.\n';
  const ITEMS_CSV = 'name,qty\napple,3\npear,5\n';
  const PNG_B64 =
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==';
  const BLOB_BYTES = [0x00, 0x01, 0x02, 0xff, 0xfe, 0x80, 0xc3, 0x28, 0x00, 0x10];

  // Prompt template — keep in sync with "### Chat file links + copy" in PromptExamples.md.
  const isWindowsPath = (p) => /^[A-Za-z]:/.test(p) || p.includes('\\');
  const buildLinkText = (host, windowsDir) => {
    const h = host.replace(/[\\/]+$/, '');
    const sep = isWindowsPath(h) ? '\\' : '/';
    const j = (...parts) => [h, ...parts].join(sep);
    const fileUri = 'file://' + (isWindowsPath(h) ? '/' + h.replace(/\\/g, '/') : h) + '/img/dot.png';
    return [
      'Here are the files I produced:',
      '',
      `- Report (host path as told): [report.md](${j('docs', 'report.md')})`,
      `- Report (angle brackets): [report-angle.md](<${j('docs', 'report.md')}>)`,
      '- Items (relative): [items.csv](data/items.csv)',
      `- Image (file URI): [dot.png](${fileUri})`,
      `- Binary: [blob.dat](${j('bin', 'blob.dat')})`,
      `- Folder: [docs folder](${j('docs')})`,
      `- Report (raw Windows path): [report-win.md](${windowsDir}\\docs\\report.md)`,
      `- Report (Windows, angle brackets): [report-win-angle.md](<${windowsDir}\\docs\\report.md>)`,
      '- Outside: [win.ini](C:\\Windows\\win.ini)',
      '- Web: [example](https://example.com/)',
    ].join('\n');
  };
  const mockPrompt = (id, message) =>
    '<|instruction_start|>' +
    JSON.stringify({ instruction_chain: [{ id, id_message: id, messages: [message] }] }) +
    '<|instruction_end|>';

  const waitIdle = async (timeout = 60000) => {
    await tid('send-button').waitFor({ state: 'visible', timeout });
    await tid('stop-button').waitFor({ state: 'hidden', timeout });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const findString = (node, re) => {
    if (typeof node === 'string') return re.test(node) ? node : null;
    if (node && typeof node === 'object') {
      for (const v of Object.values(node)) {
        const hit = findString(v, re);
        if (hit) return hit;
      }
    }
    return null;
  };

  try {
    await page.context().grantPermissions(['clipboard-read', 'clipboard-write'], { origin: BASE }).catch((e) => {
      record('clipboard-permissions-granted', false, String(e));
    });
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 30000 });

    // 0. Serve-check: the page's bundle is the one carrying the workspace-link code.
    const bundle = await page.evaluate(() => fetch('/dist/src/utils/workspaceLinks.ts').then((r) => r.text()).catch(() => ''));
    record('bundle-has-workspace-links', bundle.includes('#workspace-file?'), bundle.length);

    // 1. Workspace + conversation.
    const workspaceId = await page.evaluate(
      async ({ name, dir }) => {
        const body = await fetch('/api/workspaces').then((r) => r.json());
        const list = Array.isArray(body) ? body : (body.workspaces ?? []);
        const found = list.find((w) => w.directoryRelPath === dir);
        if (found) return found.id;
        const res = await fetch('/api/workspaces', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name, directoryRelPath: dir, marketplaces: [] }),
        });
        if (!res.ok) throw new Error(`workspace create failed: ${res.status} ${await res.text()}`);
        return (await res.json()).id;
      },
      { name: WORKSPACE_NAME, dir: WORKSPACE_DIR }
    );
    record('workspace-resolved', !!workspaceId, workspaceId);

    threadId = await page.evaluate(
      async (body) => {
        const res = await fetch('/api/conversations', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        });
        if (!res.ok) throw new Error(`provision failed: ${res.status} ${await res.text()}`);
        return (await res.json()).threadId;
      },
      { workspaceId, providerId: PROVIDER_ID, modeId: MODE_ID }
    );
    record('provisioned-thread', !!threadId, threadId);
    if (!threadId) return finish({});

    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await tid('chat-input-textarea').waitFor({ timeout: 30000 });
    // The app settles on /dist/index.html?threadId=…; that settled location is the baseline.
    const threadUrl = page.url();
    await page.waitForFunction(
      () => /Workspace/.test(document.querySelector('[data-testid="mode-selector-button"]')?.textContent ?? ''),
      null,
      { timeout: 15000 }
    ).catch(() => {});
    const modeLabel = await tid('mode-selector-button').textContent().catch(() => null);
    record('mode-bound (Workspace Agent)', /Workspace/.test(modeLabel ?? ''), modeLabel);

    // 2. Establish the sandbox session and learn the host path the model is told.
    await send(mockPrompt('echo-sys', { system_prompt_echo: {} }));
    await tid('assistant-text').first().waitFor({ timeout: 90000 });
    await waitIdle(90000);
    const history = await page.evaluate((t) => fetch(`/api/conversations/${t}/messages`).then((r) => r.json()), threadId);
    // The messages API nests serialized message JSON inside strings: unwrap until the marker is plain text.
    let echo = findString(history, /Your workspace directory is: /);
    for (let i = 0; echo && i < 4; i++) {
      try {
        echo = findString(JSON.parse(echo), /Your workspace directory is: /);
      } catch {
        break;
      }
    }
    const hostPath = echo ? /Your workspace directory is: ([^\r\n]+)/.exec(echo)[1].trim() : null;
    record('host-path-from-system-prompt', !!hostPath, hostPath);
    if (!hostPath) return finish({ threadId });

    // 3. Fixtures through the file-browser upload API (relativePath creates the parent folders).
    const uploads = await page.evaluate(
      async ({ t, files }) => {
        const out = [];
        for (const f of files) {
          const bytes = f.b64
            ? Uint8Array.from(atob(f.b64), (c) => c.charCodeAt(0))
            : f.bytes
              ? new Uint8Array(f.bytes)
              : new TextEncoder().encode(f.text);
          const form = new FormData();
          form.append('file', new Blob([bytes]), f.rel.split('/').pop());
          form.append('relativePath', f.rel);
          const res = await fetch(`/api/conversations/${t}/files?path=`, { method: 'POST', body: form });
          out.push({ rel: f.rel, status: res.status, body: (await res.text()).slice(0, 200) });
        }
        return out;
      },
      {
        t: threadId,
        files: [
          { rel: 'docs/report.md', text: REPORT_MD },
          { rel: 'data/items.csv', text: ITEMS_CSV },
          { rel: 'img/dot.png', b64: PNG_B64 },
          { rel: 'bin/blob.dat', bytes: BLOB_BYTES },
        ],
      }
    );
    record('fixtures-uploaded', uploads.every((u) => u.status >= 200 && u.status < 300), uploads);

    // 4. The link message.
    const linkText = buildLinkText(hostPath, isWindowsPath(hostPath) ? hostPath : WINDOWS_HOST_DIR_WHEN_POSIX);
    const bubblesBefore = await tid('assistant-text').count();
    await send(mockPrompt('file-links', { text: linkText }));
    await page.waitForFunction(
      (n) => document.querySelectorAll('[data-testid="assistant-text"]').length > n,
      bubblesBefore,
      { timeout: 60000 }
    );
    await waitIdle(60000);
    const bubble = tid('assistant-text').last();
    await bubble.locator('a', { hasText: 'example' }).waitFor({ timeout: 15000 });

    const links = await bubble.evaluate((el) =>
      [...el.querySelectorAll('a')].map((a) => ({
        text: a.textContent,
        href: a.getAttribute('href'),
        className: a.className,
        target: a.getAttribute('target'),
        rel: a.getAttribute('rel'),
      }))
    );
    const renderedText = await bubble.innerText();
    const byText = (t) => links.find((l) => l.text === t);
    for (const name of ['report.md', 'report-angle.md', 'items.csv', 'dot.png', 'blob.dat', 'docs folder', 'report-win.md', 'report-win-angle.md', 'win.ini']) {
      const l = byText(name);
      record(`rendered-as-workspace-link: ${name}`, !!l && /workspace-link/.test(l.className) && l.href.startsWith('#workspace-file?'), l ?? 'no <a> with this text');
    }
    if (links.length < 10) record('rendered-text (diagnostic)', false, renderedText);
    const web = byText('example');
    record(
      'web-link-new-tab',
      !!web && web.href === 'https://example.com/' && web.target === '_blank' && /noopener/.test(web.rel ?? '') && /noreferrer/.test(web.rel ?? ''),
      web
    );

    // 5. Click each file link and inspect the modal.
    const openLink = async (text) => {
      const respPromise = page
        .waitForResponse((r) => r.url().includes('/files/resolve'), { timeout: 20000 })
        .catch(() => null);
      await bubble.locator('a', { hasText: text }).first().click();
      await tid('artifact-preview-modal').waitFor({ timeout: 10000 });
      const resp = await respPromise;
      let body = null;
      if (resp) body = await resp.text().catch(() => null);
      const call = resp ? { link: text, url: decodeURIComponent(resp.url().replace(BASE, '')), status: resp.status(), body } : { link: text, missing: true };
      resolveCalls.push(call);
      await tid('artifact-preview-loading').waitFor({ state: 'detached', timeout: 20000 }).catch(() => {});
      return call;
    };
    const closeModal = async () => {
      await tid('artifact-preview-modal-close').click();
      await tid('artifact-preview-modal').waitFor({ state: 'detached', timeout: 5000 });
    };
    const modalState = () =>
      page.evaluate(() => {
        const m = document.querySelector('[data-testid="artifact-preview-modal"]');
        const q = (id) => m?.querySelector(`[data-testid="${id}"]`);
        const img = q('artifact-preview-image');
        const table = q('artifact-preview-table');
        return {
          markdownH1: q('artifact-preview-markdown')?.querySelector('h1')?.textContent ?? null,
          tableBodyRows: table ? table.querySelectorAll('tbody tr').length : null,
          tableHead: table ? [...table.querySelectorAll('thead th')].map((th) => th.textContent) : null,
          image: img ? { src: img.getAttribute('src'), complete: img.complete, naturalWidth: img.naturalWidth } : null,
          unavailable: q('artifact-preview-unavailable')?.textContent?.trim() ?? null,
          error: q('artifact-preview-error')?.textContent?.trim() ?? null,
          text: q('artifact-preview-text')?.textContent ?? null,
          download: !!q('artifact-preview-download'),
        };
      });

    // report.md via the host path the model was told, bare and in angle brackets
    for (const name of ['report.md', 'report-angle.md']) {
      const call = await openLink(name);
      const s = await modalState();
      record(`md-preview: ${name}`, call.status === 200 && s.markdownH1 === 'Quarterly Report' && s.download, { call, s });
      if (name === 'report.md') await shot('01-markdown-preview');
      await closeModal();
      record(`url-unchanged-after: ${name}`, page.url() === threadUrl, page.url());
    }

    // The Windows spelling of the same file: a link either way; it resolves only when HostPath is Windows-style.
    for (const name of ['report-win.md', 'report-win-angle.md']) {
      const call = await openLink(name);
      const s = await modalState();
      const expectResolve = isWindowsPath(hostPath);
      record(
        `windows-path: ${name} (${expectResolve ? 'resolves' : 'outside_workspace under POSIX HostPath'})`,
        expectResolve
          ? call.status === 200 && s.markdownH1 === 'Quarterly Report' && s.download
          : call.status === 400 && /outside_workspace/.test(call.body ?? '') && s.error === 'This link points outside the workspace.',
        { call, s }
      );
      await closeModal();
    }

    // items.csv (relative)
    {
      const call = await openLink('items.csv');
      const s = await modalState();
      record('csv-preview: 2 body rows', call.status === 200 && s.tableBodyRows === 2 && s.download, { call, s });
      await closeModal();
      record('url-unchanged-after: items.csv', page.url() === threadUrl, page.url());
    }

    // dot.png (file:/// URI)
    {
      const call = await openLink('dot.png');
      await page
        .waitForFunction(
          () => {
            const img = document.querySelector('[data-testid="artifact-preview-image"]');
            return img && img.complete && img.naturalWidth > 0;
          },
          null,
          { timeout: 10000 }
        )
        .catch(() => {});
      const s = await modalState();
      record(
        'image-preview: blob src loaded',
        call.status === 200 && !!s.image && s.image.src.startsWith('blob:') && s.image.naturalWidth > 0 && s.download,
        { call, s }
      );
      await shot('02-image-preview');
      await closeModal();
      record('url-unchanged-after: dot.png', page.url() === threadUrl, page.url());
    }

    // blob.dat (binary) — unavailable + download, and the UI download's bytes.
    {
      const call = await openLink('blob.dat');
      const s = await modalState();
      record('binary: unavailable + download button', call.status === 200 && !!s.unavailable && s.download, { call, s });
      const downloadEvent = page.waitForEvent('download', { timeout: 15000 }).catch(() => null);
      await tid('artifact-preview-download').click();
      const dl = await downloadEvent;
      let uiBytes = null;
      if (dl) {
        const stream = await dl.createReadStream();
        const chunks = [];
        for await (const chunk of stream) chunks.push(...chunk);
        uiBytes = chunks;
      }
      record(
        'binary: UI download bytes equal fixture',
        !!uiBytes && JSON.stringify(uiBytes) === JSON.stringify(BLOB_BYTES),
        { suggestedFilename: dl?.suggestedFilename() ?? null, uiBytes }
      );
      await closeModal();
      record('url-unchanged-after: blob.dat', page.url() === threadUrl, page.url());
    }

    // docs folder
    {
      const call = await openLink('docs folder');
      const s = await modalState();
      record(
        'folder: folder message, no download',
        call.status === 200 && s.unavailable === 'This link points to a folder, not a file.' && !s.download,
        { call, s }
      );
      await closeModal();
    }

    // outside the workspace
    {
      const call = await openLink('win.ini');
      const s = await modalState();
      record(
        'outside: outside message, no download',
        call.status === 400 && /outside_workspace/.test(call.body ?? '') && s.error === 'This link points outside the workspace.' && !s.download,
        { call, s }
      );
      await closeModal();
      record('url-unchanged-after: win.ini', page.url() === threadUrl, page.url());
    }

    // 6. Direct download endpoint bytes.
    const directBytes = await page.evaluate(
      (t) => fetch(`/api/conversations/${t}/files/download?path=bin/blob.dat`).then(async (r) => ({ status: r.status, bytes: Array.from(new Uint8Array(await r.arrayBuffer())) })),
      threadId
    );
    record('download endpoint bytes equal fixture', directBytes.status === 200 && JSON.stringify(directBytes.bytes) === JSON.stringify(BLOB_BYTES), directBytes);

    // 7. Copy button → clipboard holds the literal markdown.
    const row = page.locator('.text-bubble-row').filter({ has: page.locator('a', { hasText: 'win.ini' }) }).last();
    await row.hover();
    const copyBtn = row.locator('[data-testid="copy-message-button"]');
    await copyBtn.click();
    await page.waitForFunction(
      (el) => /Copied|failed/.test(el.textContent ?? ''),
      await copyBtn.elementHandle(),
      { timeout: 5000 }
    ).catch(() => {});
    const label = (await copyBtn.textContent())?.trim();
    await shot('03-copied');
    const clip = await page.evaluate(() => navigator.clipboard.readText().then((t) => ({ ok: true, t }), (e) => ({ ok: false, e: String(e) })));
    const normalized = clip.ok ? clip.t.replace(/\r\n/g, '\n') : null;
    record('copy: label Copied', label === 'Copied', label);
    record('copy: clipboard equals literal markdown', clip.ok && normalized === linkText, clip.ok ? { equal: normalized === linkText, clipboard: normalized, expected: linkText } : clip);

    return finish({ threadId, hostPath, links, linkText });
  } catch (err) {
    record('exception', false, String(err && err.stack ? err.stack : err));
    return finish({ threadId });
  }
}
