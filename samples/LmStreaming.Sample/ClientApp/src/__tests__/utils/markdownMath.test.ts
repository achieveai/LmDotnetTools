import { beforeEach, describe, expect, it } from 'vitest';
import { parseMarkdown } from '@/utils/markdown';
import { mathCacheStats, sanitizeMathHtml } from '@/utils/mathMarkdown';

/*
 * Math, chemistry and the sanitizer boundary that carries them.
 *
 * Every assertion here runs through `parseMarkdown`, i.e. through the real DOMPurify pass, because
 * the thing under test is not "does KaTeX produce markup" but "does that markup survive the
 * Markdown sanitizer AND arrive with nothing a model could have smuggled into it".
 */
describe('math rendering', () => {
  it('renders inline $…$ as KaTeX markup', () => {
    const html = parseMarkdown('Energy $E = mc^2$ here');
    expect(html).toContain('class="katex"');
    expect(html).toContain('Energy');
    expect(html).not.toContain('$E = mc^2$');
  });

  it('renders $$…$$ as display math', () => {
    const html = parseMarkdown('$$\\int_0^1 x^2 dx$$');
    expect(html).toContain('katex-display');
    expect(html).toContain('<mo>∫</mo>');
  });

  it('renders ```math and ```latex fences as display math instead of code blocks', () => {
    for (const lang of ['math', 'latex', 'tex']) {
      const html = parseMarkdown('```' + lang + '\n\\frac{1}{2}\n```');
      expect(html, lang).toContain('katex-display');
      expect(html, lang).not.toContain('<pre>');
    }
  });

  /*
   * `$$…$$` mid-sentence stays INSIDE its paragraph. Besides being what the author meant, this is
   * the structural form of a performance trap: while the block extension's `start` reported every
   * `$$`, marked cut the paragraph there, the block tokenizer declined the fragment, and the
   * remainder was re-lexed -- 0.35 ms of parsing became 42 ms on a 10 KB message.
   */
  it('keeps an inline $$…$$ inside its paragraph and a line-leading one as its own block', () => {
    const inline = parseMarkdown('before $$x^2$$ after');
    expect(inline.match(/<p[ >]/g)).toHaveLength(1);
    expect(inline).toContain('before');
    expect(inline).toContain('after');

    const block = parseMarkdown('before\n\n$$x^2$$\n\nafter');
    expect(block).toContain('md-math-display');
    expect(block.match(/<p[ >]/g)!.length).toBeGreaterThanOrEqual(3);
  });

  it('renders mhchem \\ce{} and \\pu{} without falling back to an error span', () => {
    const html = parseMarkdown('Water $\\ce{2H2 + O2 -> 2H2O}$ releases $\\pu{286 kJ//mol}$.');
    expect(html).not.toContain('katex-error');
    expect(html).toContain('class="katex"');
    expect(html).toContain('kJ');
  });

  it('degrades a malformed formula to inline error text without throwing', () => {
    let html = '';
    expect(() => { html = parseMarkdown('Broken $\\frac{$ rest'); }).not.toThrow();
    expect(html).toContain('katex-error');
    expect(html).toContain('rest');
  });

  it('leaves currency and code-span dollars alone', () => {
    expect(parseMarkdown('It costs $5 and $10 total')).not.toContain('katex');
    expect(parseMarkdown('use `$x$` here')).not.toContain('katex');
    expect(parseMarkdown('Escaped \\$9 only')).not.toContain('katex');
  });

  /*
   * The placeholder body is HTML-escaped on the way out of marked and unescaped on the way into
   * KaTeX. `&` is the column separator of every matrix environment and `<`/`>` are real relations,
   * so a lost or doubled unescape silently corrupts exactly the formulas people write by hand.
   */
  it('round-trips the characters that HTML escaping touches', () => {
    const matrix = parseMarkdown('$$\\begin{pmatrix} a & b \\\\ c & d \\end{pmatrix}$$');
    expect(matrix).not.toContain('katex-error');
    expect(matrix).toContain('katex-display');

    const relations = parseMarkdown('$a < b > c$ and $x \\ne \\text{"y"}$');
    expect(relations).not.toContain('katex-error');
    expect(relations).toContain('class="katex"');

    // A formula whose SOURCE contains an entity-looking string must not be double-unescaped.
    const entity = parseMarkdown('$\\text{a\\&amp;b}$');
    expect(entity).toContain('&amp;amp;');
  });

  it('keeps sub/sup markup that chemistry and physics text relies on', () => {
    const html = parseMarkdown('H<sub>2</sub>O and E = mc<sup>2</sup>');
    expect(html).toContain('<sub>2</sub>');
    expect(html).toContain('<sup>2</sup>');
  });
});

describe('math sanitizer boundary', () => {
  it('drops event handlers, javascript: and data: from anything inside a math placeholder', () => {
    const html = parseMarkdown(
      '<span class="md-math md-math-inline" onclick="steal()">x</span>\n\n' +
        '<p class="md-math md-math-display" onmouseover="steal()" style="background:url(https://evil.test/x)">y</p>'
    );
    expect(html).not.toMatch(/\son[a-z]+=/i);
    expect(html).not.toContain('steal()');
    expect(html).not.toContain('javascript:');
    expect(html).not.toContain('evil.test');
  });

  it('neutralises the KaTeX commands that can emit URLs', () => {
    const html = parseMarkdown(
      '$\\href{javascript:alert(1)}{a}$ $\\url{https://evil.test}$ ' +
        '$\\includegraphics[height=1em]{https://evil.test/x.png}$ ' +
        '$\\htmlStyle{background:url(https://evil.test/x)}{z}$'
    );
    // The hostile strings DO survive as inert text: KaTeX echoes the source into
    // `<annotation encoding="application/x-tex">`, which is never rendered and is what makes
    // copy-as-LaTeX and screen readers work. What must not exist is a fetching or executing
    // context, so the assertions are on markup, not on the characters.
    const body = new DOMParser().parseFromString(`<body>${html}</body>`, 'text/html').body;
    expect(body.querySelector('a, img, iframe, object, embed, mglyph')).toBeNull();
    for (const element of body.querySelectorAll('*')) {
      for (const attribute of element.attributes) {
        expect(attribute.name, element.outerHTML).not.toMatch(/^(on|href|src|xlink)/i);
        expect(attribute.value, attribute.name).not.toMatch(/javascript\s*:|url\s*\(|evil\.test/i);
      }
    }
    expect(body.textContent).toContain('\\href');
  });

  it('keeps only length-like inline styles on math output', () => {
    const html = parseMarkdown('$$\\frac{1}{2}$$');
    const styles = [...html.matchAll(/style="([^"]*)"/g)].map((m) => m[1]);
    expect(styles.length).toBeGreaterThan(0);
    for (const style of styles) {
      expect(style).not.toMatch(/url\s*\(|expression\s*\(|javascript\s*:|\\/i);
    }
  });

  /*
   * Attacks the KaTeX-output policy directly. Routing these through `parseMarkdown` would be
   * vacuous: `trust: false` means KaTeX never emits any of this for ANY LaTeX, so a passing test
   * would be measuring KaTeX, not the allowlist that has to hold if KaTeX ever changes.
   */
  it('strips scripts, URL styles and foreign tags from KaTeX-shaped output', () => {
    const clean = sanitizeMathHtml(
      '<span class="katex" style="height:1em;background:url(https://evil.test/a)">' +
        '<span style="color:#cc0000">keep</span>' +
        '<span style="width:calc(1px)">calc</span>' +
        '<span style="background-image:\\75 rl(https://evil.test/b)">esc</span>' +
        '<a href="javascript:alert(1)">link</a><img src="x" onerror="alert(1)">' +
        '<mglyph src="https://evil.test/c.png" /><script>alert(1)<\/script>' +
        '<math><mi onclick="alert(1)" mathvariant="italic">x</mi></math></span>'
    );
    expect(clean).not.toContain('evil.test');
    expect(clean).not.toContain('javascript:');
    expect(clean).not.toMatch(/<(a|img|script|mglyph)\b/i);
    expect(clean).not.toMatch(/\son[a-z]+=/i);
    expect(clean).not.toMatch(/style="[^"]*\(/);
    // The safe declarations and the MathML that carries meaning are untouched.
    expect(clean).toContain('style="color:#cc0000"');
    expect(clean).toContain('mathvariant="italic"');
    expect(clean).toContain('keep');
  });

  it('does not let sub/sup become a new attribute channel', () => {
    const html = parseMarkdown('<sub onclick="x()" style="background:url(https://evil.test/a)" id="s">2</sub>');
    expect(html).toContain('<sub>2</sub>');
    expect(html).not.toContain('onclick');
    expect(html).not.toContain('evil.test');
  });
});

describe('streaming cost', () => {
  beforeEach(() => {
    mathCacheStats.hits = 0;
    mathCacheStats.misses = 0;
  });

  it('renders each distinct formula once across a whole stream', () => {
    const full =
      'Intro $a^2 + b^2 = c^2$ then $$\\int_0^1 x\\,dx$$ and $\\ce{H2O}$ ' +
      'followed by a long tail of prose. '.repeat(40);
    for (let end = 1; end <= full.length; end += 7) parseMarkdown(full.slice(0, end), { highlight: false });
    parseMarkdown(full);

    // Three complete formulas. Partial prefixes are not tokenized at all (no closing delimiter), so
    // a miss can only come from a formula that actually closed -- one per formula, forever.
    expect(mathCacheStats.misses).toBeLessThanOrEqual(3);
    expect(mathCacheStats.hits).toBeGreaterThan(100);
  });
});
