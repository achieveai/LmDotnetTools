import { describe, it, expect } from 'vitest';
import { parseMarkdown } from '@/utils/markdown';

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

  it('strips data-* and target attributes', () => {
    expect(parseMarkdown('<p data-track="1">hi</p>')).not.toContain('data-track');
    expect(parseMarkdown('<a href="https://x.example" target="_blank">x</a>')).not.toContain(
      'target'
    );
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
