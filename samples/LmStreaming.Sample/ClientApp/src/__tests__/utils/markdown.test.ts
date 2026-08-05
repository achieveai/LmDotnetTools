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
    expect(html).toContain('var s = &quot;a &amp; b&quot;;');
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
    expect(html).toContain('&lt;script&gt;');
    expect(html).toContain('&quot;x&quot;');
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
