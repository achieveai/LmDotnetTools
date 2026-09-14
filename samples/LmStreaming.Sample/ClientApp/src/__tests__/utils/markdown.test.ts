import { describe, it, expect } from 'vitest';
import { parseMarkdown } from '@/utils/markdown';
import { parseWorkspaceLinkHref } from '@/utils/workspaceLinks';

/**
 * `parseMarkdown` re-runs for a streaming message on EVERY delta, and highlighting a fence is
 * O(fence length) -- so a growing code block gets highlighted from scratch per chunk. The
 * `highlight: false` opt-out is what keeps that off the main thread while a message is live;
 * these tests pin both halves of the contract: no token spans, but still ESCAPED (the output
 * goes straight into `v-html`) and with an unchanged class list so finalizing does not restyle.
 */
describe('parseMarkdown', () => {
  const FENCE = [
    '# Heading',
    '',
    '```csharp',
    'var s = "a & b";',
    'if (msg.RunId is null) { }',
    '```',
  ].join('\n');

  // Counts highlight.js TOKEN classes (`hljs-keyword`, ...) without matching the block's own
  // `hljs language-csharp` class, which both paths must keep.
  const tokenSpans = (html: string) => (html.match(/class="hljs-/g) || []).length;

  it('highlights fenced code by default', () => {
    const html = parseMarkdown(FENCE);
    expect(tokenSpans(html)).toBeGreaterThan(0);
    expect(html).toContain('class="hljs language-csharp"');
  });

  it('highlights fenced code when highlight is explicitly true', () => {
    expect(tokenSpans(parseMarkdown(FENCE, { highlight: true }))).toBeGreaterThan(0);
  });

  it('emits ZERO token spans when highlight is false', () => {
    const html = parseMarkdown(FENCE, { highlight: false });
    expect(tokenSpans(html)).toBe(0);
    // `&` MUST stay an entity -- a bare `&` followed by text can start an entity reference and
    // change what the browser parses. A bare `"` inside a text node cannot, and the sanitizer's
    // DOM round-trip normalizes `&quot;` back to `"` there, so only `&amp;` is asserted.
    expect(html).toContain('var s = "a &amp; b";');
  });

  it('keeps the code block class list identical across both paths', () => {
    const classOf = (html: string) => (html.match(/<code class="([^"]*)"/) || [])[1];
    expect(classOf(parseMarkdown(FENCE, { highlight: false }))).toBe('hljs language-csharp');
    expect(classOf(parseMarkdown(FENCE, { highlight: false }))).toBe(classOf(parseMarkdown(FENCE)));
  });

  it('escapes HTML inside an un-highlighted fence', () => {
    const md = ['```html', '<script>alert("x")</script>', '```'].join('\n');
    const html = parseMarkdown(md, { highlight: false });
    expect(html).not.toContain('<script>');
    // The angle brackets are what matter -- they are the only characters that can end the
    // `<code>` element and start a real tag. See the note above about `"`.
    expect(html).toContain('&lt;script&gt;alert("x")&lt;/script&gt;');
  });

  it('escapes HTML in a fence with no language, both paths', () => {
    const md = ['```', '<img src=x onerror=alert(1)>', '```'].join('\n');
    for (const html of [parseMarkdown(md, { highlight: false }), parseMarkdown(md)]) {
      expect(html).not.toContain('<img src=x');
      expect(html).toContain('&lt;img src=x');
    }
  });

  it('renders the surrounding markdown identically apart from token spans', () => {
    const strip = (html: string) => html.replace(/<span class="hljs-[^"]*">/g, '').replace(/<\/span>/g, '');
    expect(strip(parseMarkdown(FENCE))).toBe(parseMarkdown(FENCE, { highlight: false }));
  });

  it('returns an empty string for empty input on both paths', () => {
    expect(parseMarkdown('')).toBe('');
    expect(parseMarkdown('', { highlight: false })).toBe('');
  });

  it('degrades an unknown fence language to escaped plain text', () => {
    const md = ['```mermaid', 'graph TD; A-->B;', '```'].join('\n');
    expect(parseMarkdown(md)).toContain('A--&gt;B');
    expect(parseMarkdown(md, { highlight: false })).toContain('A--&gt;B');
  });
});

/**
 * `marked` passes raw HTML in a markdown document straight through by design, and `parseMarkdown`
 * output is bound with `v-html` -- so before sanitization every one of the payloads below reached
 * the DOM verbatim from a model response, a tool result or a pasted document. Each "strips" case
 * fails without the DOMPurify call in `parseMarkdown`; that is the RED half of the proof.
 *
 * The "keeps" cases are the other half: an allowlist that is too tight silently breaks rendering
 * rather than throwing, and two of these constructs (`align`, hljs `class`) are bugs this PR
 * series already fixed once.
 *
 * NOTE these run under jsdom, not happy-dom -- see the comment in `vitest.config.ts`. Under
 * happy-dom DOMPurify reports success while letting `<script>` through, so every assertion here
 * would pass on unsanitized output.
 */
describe('parseMarkdown sanitization', () => {
  it('strips a script tag embedded as raw HTML', () => {
    const html = parseMarkdown('Hello\n\n<script>alert(1)</script>\n\nBye');
    expect(html).not.toContain('<script');
    expect(html).not.toContain('alert(1)');
    expect(html).toContain('Hello');
    expect(html).toContain('Bye');
  });

  it('strips event handler attributes', () => {
    const html = parseMarkdown('<img src="x" onerror="alert(1)">');
    expect(html).not.toContain('onerror');
    expect(html).not.toContain('alert(1)');
  });

  it('strips an iframe', () => {
    const html = parseMarkdown('<iframe src="https://evil.example"></iframe>');
    expect(html).not.toContain('<iframe');
    expect(html).not.toContain('evil.example');
  });

  it('strips a javascript: URL from a markdown link', () => {
    const html = parseMarkdown('[click me](javascript:alert(1))');
    expect(html).not.toContain('javascript:');
    expect(html).toContain('click me'); // text survives, the navigation does not
  });

  it('strips tags outside the markdown allowlist that DOMPurify would otherwise keep', () => {
    // These four are ALLOWED by DOMPurify's defaults (measured, 3.4.13). They are gone only
    // because the allowlist is explicit -- which is the reason it is explicit.
    const html = parseMarkdown(
      [
        '<style>body{display:none}</style>',
        '<form action="/steal"><input name="pw" value="x"></form>',
        '<svg><circle r="1"/></svg>',
        '<math><mi>x</mi></math>',
      ].join('\n\n')
    );
    for (const forbidden of ['<style', '<form', 'name="pw"', '<svg', '<circle', '<math']) {
      expect(html).not.toContain(forbidden);
    }
  });

  it('strips data-* attributes and any target/rel the markdown chose itself', () => {
    expect(parseMarkdown('<p data-track="1">hi</p>')).not.toContain('data-track');
    // Raw HTML cannot pick its own target or rel: a non-web link keeps neither...
    const anchor = parseMarkdown('<a href="#top" target="_top" rel="opener">x</a>');
    expect(anchor).not.toContain('target');
    expect(anchor).not.toContain('rel=');
    // ...and a web link gets OUR target/rel, never the document's.
    const web = parseMarkdown('<a href="https://x.example" target="_top" rel="opener">x</a>');
    expect(web).toContain('target="_blank"');
    expect(web).toContain('rel="noopener noreferrer"');
    expect(web).not.toContain('_top');
    expect(web).not.toContain('"opener"');
  });

  it('keeps GFM table column alignment', () => {
    const md = ['| a | b |', '| ---: | :---: |', '| 1 | 2 |'].join('\n');
    const html = parseMarkdown(md);
    expect(html).toContain('align="right"');
    expect(html).toContain('align="center"');
  });

  it('keeps highlight.js block and token classes', () => {
    const html = parseMarkdown(FENCE_FOR_SANITIZE);
    expect(html).toContain('class="hljs language-csharp"');
    expect(html).toMatch(/class="hljs-[a-z]+"/);
  });

  it('keeps task-list checkboxes', () => {
    const html = parseMarkdown('- [x] done\n- [ ] todo');
    expect(html).toContain('type="checkbox"');
    expect(html).toContain('disabled');
    expect(html).toContain('checked');
  });

  it('keeps ordinary markdown structure', () => {
    const html = parseMarkdown(
      '# H\n\n[link](https://x.example)\n\n![alt](https://x.example/i.png)\n\n> quote\n\n1. one'
    );
    expect(html).toContain('<h1>H</h1>');
    expect(html).toContain('href="https://x.example"');
    expect(html).toContain('src="https://x.example/i.png"');
    expect(html).toContain('alt="alt"');
    expect(html).toContain('<blockquote>');
    expect(html).toContain('<ol>');
  });
});

const FENCE_FOR_SANITIZE = ['```csharp', 'var s = "a";', '```'].join('\n');

/** Every `<a>` in the output as { href, cls, target, rel }, read through the DOM rather than regex. */
function anchors(html: string) {
  const host = document.createElement('div');
  host.innerHTML = html;
  return [...host.querySelectorAll('a')].map((a) => ({
    href: a.getAttribute('href'),
    cls: a.getAttribute('class'),
    target: a.getAttribute('target'),
    rel: a.getAttribute('rel'),
    text: a.textContent,
  }));
}

describe('parseMarkdown web links open in a new tab', () => {
  it.each(['https://x.example/a', 'http://x.example/a', 'HTTPS://X.EXAMPLE'])(
    '%s gets target=_blank and rel=noopener noreferrer',
    (url) => {
      const [a] = anchors(parseMarkdown(`[x](${url})`));
      expect(a.target).toBe('_blank');
      expect(a.rel).toBe('noopener noreferrer');
    }
  );

  it('applies with and without workspace links and on the un-highlighted path', () => {
    for (const html of [
      parseMarkdown('[x](https://x.example)', { highlight: false }),
      parseMarkdown('[x](https://x.example)', { workspaceLinks: { threadId: 't1' } }),
    ]) {
      expect(anchors(html)[0].target).toBe('_blank');
    }
  });

  it.each(['mailto:a@b.example', 'tel:+123', '#section'])('%s stays in place (no target)', (href) => {
    const [a] = anchors(parseMarkdown(`[x](${href})`));
    expect(a.href).toBe(href);
    expect(a.target).toBeNull();
  });
});

describe('parseMarkdown workspace links (opt-in)', () => {
  const opts = { workspaceLinks: { threadId: 'thread-123' } };

  it.each([
    ['windows host path', 'B:\\ws\\docs\\a.md'],
    ['forward-slash drive path', 'B:/ws/docs/a.md'],
    ['file URI', 'file:///B:/ws/docs/a.md'],
    ['posix absolute', '/workspace/docs/a.md'],
    ['relative', 'docs/a.md'],
    ['dot relative', './docs/a.md'],
    ['with fragment', 'docs/a.md#L10'],
  ])('%s becomes a workspace link carrying thread and target', (_, target) => {
    const [a] = anchors(parseMarkdown(`[Report](${target})`, opts));
    expect(a.cls).toBe('workspace-link');
    expect(a.target).toBeNull();
    const parsed = parseWorkspaceLinkHref(a.href ?? '');
    expect(parsed?.threadId).toBe('thread-123');
    // marked percent-encodes the destination (`\` -> %5C); the raw decoded target is what reaches the
    // server, which decodes exactly once.
    expect(decodeURI(parsed?.target ?? '')).toBe(target);
    expect(a.text).toBe('Report');
  });

  it('keeps a path with spaces as one percent-encoded target when written in angle brackets', () => {
    const [a] = anchors(parseMarkdown('[Notes](<docs/my notes.md>)', opts));
    expect(a.cls).toBe('workspace-link');
    // The server's resolver decodes %20 back to a space (WorkspaceLinkResolverTests).
    expect(parseWorkspaceLinkHref(a.href ?? '')?.target).toBe('docs/my%20notes.md');
  });

  it('does not rewrite web, mailto, tel or in-page anchor links', () => {
    const html = parseMarkdown(
      '[w](https://x.example) [m](mailto:a@b.example) [t](tel:1) [h](#top)',
      opts
    );
    expect(anchors(html).map((a) => a.cls)).toEqual([null, null, null, null]);
  });

  it('re-encodes a model-authored #workspace-file anchor instead of trusting its thread', () => {
    const forged = '#workspace-file?thread=other-thread&target=secret.md';
    const [a] = anchors(parseMarkdown(`[x](${forged})`, opts));
    expect(parseWorkspaceLinkHref(a.href ?? '')?.threadId).toBe('thread-123');
  });

  it('is off by default: a host path is still dropped and no workspace-link class appears', () => {
    const html = parseMarkdown('[a](B:\\ws\\a.md) [b](docs/a.md)');
    const [a, b] = anchors(html);
    expect(a.href).toBeNull();
    expect(b.href).toBe('docs/a.md');
    expect(html).not.toContain('workspace-link');
  });

  it('strips a raw-HTML workspace-link class when the option is off', () => {
    const html = parseMarkdown('<a class="workspace-link" href="#workspace-file?thread=t&target=x">x</a>');
    expect(html).not.toContain('workspace-link');
  });

  it('strips only the forged workspace-link class, keeping the anchor\'s other classes', () => {
    const html = parseMarkdown('<a class="workspace-link note" href="https://x.example">x</a>', opts);
    expect(anchors(html)[0].cls).toBe('note');
  });

  it('leaves no hook behind: a later plain parse is unaffected', () => {
    parseMarkdown('[a](docs/a.md)', opts);
    expect(anchors(parseMarkdown('[a](docs/a.md)'))[0].href).toBe('docs/a.md');
  });
});
