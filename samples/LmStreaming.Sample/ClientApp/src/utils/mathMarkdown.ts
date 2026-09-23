import DOMPurify from 'dompurify';
import katex from 'katex';
// mhchem extends KaTeX in place with \ce and \pu. It must be imported for its side effect, once,
// before the first render -- importing it here keeps that ordering a property of this module.
import 'katex/contrib/mhchem';
import type { MarkedExtension, Tokens } from 'marked';

/*
 * TeX math, chemistry (mhchem) and their sanitization, kept deliberately OUT of the Markdown
 * sanitizer's allowlist.
 *
 * WHY A SEPARATE POLICY. Letting KaTeX's HTML flow through `markdown.ts`'s DOMPurify pass would
 * mean widening that allowlist by ~25 MathML tags, `svg`/`path`/`line` and -- decisively -- the
 * `style` ATTRIBUTE. `style` there is granted to every element in the document, including raw HTML
 * that a model, a tool result or a pasted file wrote, for no markdown benefit at all. So this
 * module works the way `diagramRenderer.ts` already works for generated SVG:
 *
 *   1. the marked extension emits only a PLACEHOLDER -- `<span class="md-math md-math-inline">`
 *      or `<p class="md-math md-math-display">` -- whose content is the LaTeX source as text.
 *      `span`, `p` and `class` are already in the Markdown allowlist, so nothing there changes;
 *   2. `renderMathInHtml` runs AFTER that sanitizer, replaces each placeholder's content with
 *      KaTeX output, and sanitizes that output under the dedicated policy below.
 *
 * A model can of course write a literal `<span class="md-math">` in raw HTML and have it rendered
 * as math. That is harmless and intentional: `trust: false` means KaTeX emits no URL, no handler
 * and no `src` for ANY input (verified: \href, \url, \includegraphics, \htmlClass and \htmlStyle
 * all degrade to red literal text), and whatever it does emit is sanitized below regardless.
 */

const MATH_CLASS = 'md-math';
const INLINE_CLASS = 'md-math-inline';
const DISPLAY_CLASS = 'md-math-display';

/** A formula longer than this is a pathological input, not notation; it renders as plain text. */
const MAX_MATH_LENGTH = 4_000;

/**
 * A streaming message re-parses its WHOLE accumulated text on every delta (see `parseMarkdown`), so
 * an already-complete formula would otherwise be re-rendered once per chunk. Keyed by mode+source,
 * which is the entire input to `renderToString`, so a hit is always exact.
 */
const MAX_CACHE_ENTRIES = 512;
const renderCache = new Map<string, string>();

/** Cache instrumentation. Read by the streaming-cost test; cheap enough to leave on. */
export const mathCacheStats = { hits: 0, misses: 0 };

const KATEX_OPTIONS = {
  // A model writes broken TeX routinely. `throwOnError` off turns that into a red `katex-error`
  // span carrying the source; a throw here would take down the whole message render.
  throwOnError: false,
  // Disables \href, \url, \includegraphics, \htmlClass, \htmlId, \htmlData and \htmlStyle. This is
  // the primary reason the sanitizer below never has to reason about URLs.
  trust: false,
  strict: 'ignore' as const,
  // Bounds on the two ways a short input can produce unbounded work/output.
  maxSize: 50,
  maxExpand: 1_000,
  output: 'htmlAndMathml' as const,
};

/*
 * Exactly the node surface KaTeX 0.18 can produce, enumerated from its own source (every
 * `MathNode`/`SvgNode` construction) plus a probe of 20 representative formulas -- not guessed,
 * and not DOMPurify's MathML profile, which is far wider.
 *
 * `mglyph` is the one KaTeX MathML node deliberately LEFT OUT: it is the \includegraphics image
 * carrier and is the only one that takes `src`. `trust: false` already prevents it; excluding it
 * means a future KaTeX that emits it cannot introduce an image load here either.
 */
const MATH_ALLOWED_TAGS = [
  // KaTeX's HTML layer.
  'span',
  // Stretchy delimiters, \sqrt rules and \rule are drawn as SVG.
  'svg', 'path', 'line',
  // The MathML layer, which is what screen readers and copy-paste consume.
  'math', 'semantics', 'annotation', 'mrow', 'mi', 'mn', 'mo', 'ms', 'mtext', 'mspace',
  'mfrac', 'msqrt', 'mroot', 'msub', 'msup', 'msubsup', 'munder', 'mover', 'munderover',
  'mtable', 'mtr', 'mtd', 'mstyle', 'mpadded', 'mphantom', 'menclose', 'merror',
];

/*
 * Every attribute KaTeX sets, minus the three that carry a URL (`href`, `xlink:href`, `src`).
 * Those are unreachable with `trust: false`; leaving them out is the second lock on the same door.
 * `title` is here because the `katex-error` span puts the parse message in it.
 */
const MATH_ALLOWED_ATTR = [
  'class', 'style', 'title', 'aria-hidden', 'xmlns',
  // SVG geometry.
  'd', 'viewBox', 'preserveAspectRatio', 'width', 'height',
  'x', 'y', 'x1', 'x2', 'y1', 'y2', 'fill', 'stroke', 'stroke-width',
  // MathML presentation.
  'accent', 'accentunder', 'columnalign', 'columnlines', 'columnspacing', 'depth', 'display',
  'displaystyle', 'encoding', 'fence', 'largeop', 'linethickness', 'lspace', 'mathbackground',
  'mathcolor', 'mathsize', 'mathvariant', 'maxsize', 'minsize', 'notation', 'rowlines',
  'rowspacing', 'rspace', 'scriptlevel', 'separator', 'stretchy', 'valign', 'voffset',
];

/*
 * KaTeX's inline styles are positions, sizes and one colour -- observed set: color, height, width,
 * min-width, top, left, bottom, margin-left, margin-right, padding-left, border-bottom-width,
 * vertical-align, position, display. Rather than enumerate properties (a future KaTeX would then
 * lose layout silently), the VALUE grammar is constrained: identifiers, numbers, units, percentages
 * and hex colours only. No parenthesis can appear, so `url(...)` and `expression(...)` are
 * unrepresentable; no colon or slash, so `javascript:` is too; no backslash, so escape sequences
 * cannot reconstruct any of them.
 */
const SAFE_STYLE = /^(?:\s*[a-z-]+\s*:[-+0-9a-z.%#\s]*(?:;|$))*\s*$/i;

/**
 * The policy for KaTeX's own output, separate from the Markdown one.
 *
 * Exported because it is the only part of this module that a test can attack directly: the LaTeX
 * that reaches `renderFormula` is attacker-controlled, but everything it can produce through
 * `trust: false` KaTeX is benign, so a test routed through `parseMarkdown` could only ever confirm
 * KaTeX's behaviour, not this allowlist's.
 */
export function sanitizeMathHtml(html: string): string {
  const dropUnsafeStyle = (node: Element) => {
    const style = node.getAttribute('style');
    if (style !== null && !SAFE_STYLE.test(style)) node.removeAttribute('style');
  };
  DOMPurify.addHook('afterSanitizeAttributes', dropUnsafeStyle);
  try {
    return DOMPurify.sanitize(html, {
      ALLOWED_TAGS: MATH_ALLOWED_TAGS,
      ALLOWED_ATTR: MATH_ALLOWED_ATTR,
      ALLOW_DATA_ATTR: false,
      ALLOW_ARIA_ATTR: false,
    });
  } finally {
    DOMPurify.removeHook('afterSanitizeAttributes', dropUnsafeStyle);
  }
}

function renderFormula(latex: string, displayMode: boolean): string {
  const key = `${displayMode ? 'display' : 'inline'}|${latex}`;
  const cached = renderCache.get(key);
  if (cached !== undefined) {
    mathCacheStats.hits += 1;
    return cached;
  }
  mathCacheStats.misses += 1;
  let rendered: string;
  try {
    rendered = sanitizeMathHtml(katex.renderToString(latex, { ...KATEX_OPTIONS, displayMode }));
  } catch {
    // `throwOnError: false` covers parse errors; this covers everything else KaTeX can raise
    // (maxExpand, unsupported environments). The formula falls back to its own source text.
    return '';
  }
  // Oldest-first eviction: during a stream the oldest entries are the ones already rendered and
  // scrolled past, and an unbounded map would grow with every distinct formula in the session.
  if (renderCache.size >= MAX_CACHE_ENTRIES) {
    const oldest = renderCache.keys().next();
    if (!oldest.done) renderCache.delete(oldest.value);
  }
  renderCache.set(key, rendered);
  return rendered;
}

const ESCAPE: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
const escapeHtml = (text: string) => text.replace(/[&<>"']/g, (ch) => ESCAPE[ch]);

/*
 * The TAG follows the extension's level and the CLASS follows KaTeX's display mode -- they are not
 * the same question. An inline `$$…$$` mid-sentence is typeset in display mode but is still inline
 * content: emitting a `<p>` for it put a block element inside a paragraph, and the HTML parser then
 * split the surrounding paragraph in three, stranding the words before and after it.
 */
function placeholder(latex: string, displayMode: boolean, tag: 'p' | 'span'): string {
  const body = escapeHtml(latex.trim());
  const mode = displayMode ? DISPLAY_CLASS : INLINE_CLASS;
  return `<${tag} class="${MATH_CLASS} ${mode}">${body}</${tag}>`;
}

/*
 * `$$…$$` as its own block, and ``` ```math ``` / ```` ```latex ```` / ```` ```tex ```` fences.
 *
 * The fence is claimed by a BLOCK TOKENIZER rather than by a `code` renderer because
 * `marked-highlight` mutates `token.text` of every `code` token into highlighted HTML during
 * `walkTokens`; a renderer added afterwards would receive markup, not LaTeX. Tokenizing it as its
 * own type keeps the source intact and skips the pointless highlight pass entirely.
 */
const MATH_BLOCK = /^ {0,3}\$\$([\s\S]+?)\$\$[ \t]*(?:\n|$)/;
/*
 * Where a block-level `$$` can begin: at the start of a LINE, never mid-sentence.
 *
 * marked uses a block extension's `start` to decide where the current paragraph is cut, so a plain
 * `indexOf('$$')` here cut the paragraph at every inline `$$…$$` as well. The block tokenizer then
 * declined the fragment (it is not at a line start) and marked re-lexed the remainder -- 0.3 ms of
 * parsing became 42 ms on a 10 KB message, and only when BOTH extensions were installed, which is
 * why neither one looks expensive on its own.
 */
const MATH_BLOCK_START = /(?:^|\n) {0,3}(?:\$\$|(?:`{3,}|~{3,})[ \t]*(?:math|latex|tex)[ \t]*(?:\r?\n|$))/i;
const MATH_FENCE = /^ {0,3}(`{3,}|~{3,})[ \t]*(?:math|latex|tex)[ \t]*\r?\n([\s\S]*?)(?:\r?\n)? {0,3}\1[ \t]*(?:\n+|$)/i;

/*
 * Inline `$$…$$` first, then `$…$`.
 *
 * The single-dollar form requires a NON-SPACE right after the opening `$` and right before the
 * closing one. That is what keeps prose safe: `It costs $5 and $10 total` finds no closing `$`
 * that is not preceded by a space, so it is never treated as math. `\$` never reaches here at all
 * -- marked's escape tokenizer owns it, and extensions are only consulted at the `$` itself.
 *
 * Both use a GREEDY negated class rather than a lazy alternation. `[^$\n]` cannot cross a `$`, so
 * the match stops at the next delimiter on its own and backtracks at most one character -- linear.
 * The lazy `(?:[^$\n]|\\\$)*?` form this replaced backtracks through every expansion of a
 * two-branch alternation whenever a `$` does not close, which is the normal state of the LAST
 * formula in a message that is still streaming.
 */
const MATH_INLINE_DOUBLE = /^\$\$(?!\$)([^\n]*[^\n\s])\$\$/;
const MATH_INLINE_SINGLE = /^\$(?!\s)([^$\n]*[^\s$])\$(?!\$)/;

/*
 * marked resolves a renderer by the token's TYPE, not by the extension's `name`, so the two
 * extensions must mint distinct types. Sharing one made the block extension's renderer dead code:
 * its tokens were rendered by the inline renderer instead, which emitted a `<span>` where the
 * document needed a block element.
 */
const BLOCK_TOKEN = 'mdMathBlock';
const INLINE_TOKEN = 'mdMath';

interface MathToken extends Tokens.Generic {
  type: typeof BLOCK_TOKEN | typeof INLINE_TOKEN;
  raw: string;
  text: string;
  displayMode: boolean;
}

function mathToken(
  type: MathToken['type'],
  raw: string,
  text: string,
  displayMode: boolean
): MathToken | undefined {
  if (!text.trim() || text.length > MAX_MATH_LENGTH) return undefined;
  return { type, raw, text, displayMode };
}

/** The `$…$` / `$$…$$` / ```` ```math ```` marked extension. Rendering happens post-sanitize. */
export const markedMath: MarkedExtension = {
  extensions: [
    {
      name: BLOCK_TOKEN,
      level: 'block',
      start: (src: string) => {
        const match = MATH_BLOCK_START.exec(src);
        if (!match) return undefined;
        // Point at the `$$`/fence itself, not at the newline that preceded it.
        return match.index + (match[0].startsWith('\n') ? 1 : 0);
      },
      tokenizer(src: string) {
        const fence = MATH_FENCE.exec(src);
        if (fence) return mathToken(BLOCK_TOKEN, fence[0], fence[2], true);
        const block = MATH_BLOCK.exec(src);
        if (block) return mathToken(BLOCK_TOKEN, block[0], block[1], true);
        return undefined;
      },
      renderer: (token: Tokens.Generic) => placeholder(token.text as string, true, 'p'),
    },
    {
      name: INLINE_TOKEN,
      level: 'inline',
      start: (src: string) => src.indexOf('$'),
      tokenizer(src: string) {
        const double = MATH_INLINE_DOUBLE.exec(src);
        if (double) return mathToken(INLINE_TOKEN, double[0], double[1], true);
        const single = MATH_INLINE_SINGLE.exec(src);
        if (single) return mathToken(INLINE_TOKEN, single[0], single[1], false);
        return undefined;
      },
      renderer: (token: Tokens.Generic) =>
        placeholder(token.text as string, (token as MathToken).displayMode, 'span'),
    },
  ],
};

/*
 * The placeholder as DOMPurify serializes it. Matching it as TEXT rather than walking a parsed
 * document is a deliberate performance decision, not a shortcut: a streaming message re-parses its
 * whole accumulated text on every delta, and parsing + reserializing the entire document there cost
 * 34 ms per delta on a 10 KB message (18 s over a 500-delta stream) even with every formula served
 * from the cache -- the DOM round-trip, not KaTeX, was the whole bill. The string form costs
 * 0.1 ms per delta for the same message.
 *
 * This is safe to do on a string because both halves are already sanitized independently: the
 * surrounding document by `markdown.ts`, and the replacement by `sanitizeMathHtml`. The capture is
 * non-greedy and the placeholder body is HTML-escaped text, so `</span>` inside a formula is
 * `&lt;/span&gt;` and cannot end the match early.
 */
const PLACEHOLDER = /<(span|p) class="md-math md-math-(inline|display)">([\s\S]*?)<\/\1>/g;
const UNESCAPE: Record<string, string> = {
  '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'", '&amp;': '&',
};

/**
 * Replace every math placeholder in already-sanitized Markdown HTML with sanitized KaTeX output.
 *
 * Returns `html` untouched, without scanning, when the document holds no placeholder -- the
 * overwhelmingly common case, and what keeps this off the hot path for ordinary messages.
 */
export function renderMathInHtml(html: string): string {
  if (!html.includes(MATH_CLASS)) return html;
  return html.replace(PLACEHOLDER, (whole, tag: string, mode: string, body: string) => {
    // One pass, so `&amp;lt;` correctly yields `&lt;` rather than being unescaped twice. The `&`
    // matters: it is the column separator in every `\begin{pmatrix}`-style environment.
    const latex = body.replace(/&(?:lt|gt|quot|#39|amp);/g, (entity) => UNESCAPE[entity]);
    const rendered = renderFormula(latex, mode === 'display');
    // An empty result means KaTeX itself failed; the placeholder keeps showing the source.
    return rendered ? `<${tag} class="${MATH_CLASS} md-math-${mode}">${rendered}</${tag}>` : whole;
  });
}
