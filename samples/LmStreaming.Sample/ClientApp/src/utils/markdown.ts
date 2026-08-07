import { Marked } from 'marked';
import { markedHighlight } from 'marked-highlight';
import DOMPurify from 'dompurify';
import hljs from 'highlight.js/lib/core';

import bash from 'highlight.js/lib/languages/bash';
import csharp from 'highlight.js/lib/languages/csharp';
import css from 'highlight.js/lib/languages/css';
import diff from 'highlight.js/lib/languages/diff';
import go from 'highlight.js/lib/languages/go';
import ini from 'highlight.js/lib/languages/ini';
import java from 'highlight.js/lib/languages/java';
import javascript from 'highlight.js/lib/languages/javascript';
import json from 'highlight.js/lib/languages/json';
import markdown from 'highlight.js/lib/languages/markdown';
import plaintext from 'highlight.js/lib/languages/plaintext';
import powershell from 'highlight.js/lib/languages/powershell';
import python from 'highlight.js/lib/languages/python';
import rust from 'highlight.js/lib/languages/rust';
import sql from 'highlight.js/lib/languages/sql';
import typescript from 'highlight.js/lib/languages/typescript';
import xml from 'highlight.js/lib/languages/xml';
import yaml from 'highlight.js/lib/languages/yaml';

/*
 * highlight.js is registered from `lib/core` with an explicit language list rather than the
 * default bundle: the full bundle pulls in ~190 grammars (~1 MB) for the handful an assistant
 * actually emits here. Adding a language is one import + one registerLanguage line.
 *
 * `registerLanguage` also registers each grammar's own aliases, so ```cs, ```ts, ```sh, ```yml
 * and ```html all resolve without being listed.
 */
const LANGUAGES = {
  bash,
  csharp,
  css,
  diff,
  go,
  ini,
  java,
  javascript,
  json,
  markdown,
  plaintext,
  powershell,
  python,
  rust,
  sql,
  typescript,
  xml,
  yaml,
};

for (const [name, definition] of Object.entries(LANGUAGES)) {
  hljs.registerLanguage(name, definition);
}

const MARKED_OPTIONS = {
  breaks: true, // Enable GFM line breaks
  gfm: true     // Enable GFM
};

const ESCAPE_REPLACEMENTS: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
};

/*
 * marked-highlight treats whatever the highlight callback returns as HTML, so the
 * no-highlight path has to escape the fence body itself -- returning raw code here would
 * put attacker-controlled markup straight into the `v-html` binding.
 */
function escapeHtml(code: string): string {
  return code.replace(/[&<>"']/g, (ch) => ESCAPE_REPLACEMENTS[ch]);
}

/*
 * marked dropped its built-in `highlight` option; `marked-highlight` is the supported
 * replacement. The `hljs ` prefix on the class is what the highlight.js theme selects on --
 * without it the emitted token spans are styled but the block itself is not.
 *
 * TWO instances rather than one instance with a mutable flag: highlighting is skipped while a
 * message is still streaming (see below), and a module-level flag toggled around `parse()` is
 * hidden state that any concurrent parse would read. Both instances share MARKED_OPTIONS and
 * the same `hljs language-x` class list, so finalizing a message only paints token colours in
 * -- the block keeps its panel styling throughout and nothing shifts.
 */
function createMarked(highlightCode: (code: string, lang: string) => string): Marked {
  return new Marked(
    MARKED_OPTIONS,
    markedHighlight({
      langPrefix: 'hljs language-',
      emptyLangClass: 'hljs',
      highlight: highlightCode,
    })
  );
}

const markedHighlighted = createMarked((code, lang) => {
  // An unknown or absent language must degrade to escaped plain text, never throw:
  // a model can label a fence with anything at all (```mermaid, ```text, ```).
  const language = lang && hljs.getLanguage(lang) ? lang : 'plaintext';
  return hljs.highlight(code, { language, ignoreIllegals: true }).value;
});

const markedPlain = createMarked((code) => escapeHtml(code));

/*
 * Markdown-only sanitization allowlist (#268 follow-up). `parseMarkdown` output is bound with
 * `v-html`, so any HTML a model, a tool result or a pasted document carries through markdown
 * reaches the DOM verbatim -- marked deliberately passes raw HTML through, and neither the
 * highlight escape above nor Vue's interpolation covers that path.
 *
 * The list is exactly what marked emits with `{ gfm: true, breaks: true }` + marked-highlight, so
 * anything outside it is by definition not markdown. DOMPurify's DEFAULTS were measured (3.4.13)
 * rather than assumed, because two entries here are load-bearing for rendering:
 *
 *   - `class` carries `hljs language-x` and every `hljs-*` token span. Drop it and highlighting
 *     silently turns monochrome.
 *   - `align` is the LEGACY PRESENTATIONAL ATTRIBUTE marked emits for GFM column alignment
 *     (`| ---: |` -> `<th align="right">`); `markdown.css` selects on `[align='right']`. Drop it
 *     and every aligned column silently flattens -- a bug this file's PR already fixed once.
 *
 * Both are in DOMPurify's defaults, so the allowlist is a tightening, not a rescue: the defaults
 * also keep `<style>`, `<form>`, SVG, MathML and `data-*`, none of which markdown produces.
 */
const ALLOWED_TAGS = [
  'p', 'br', 'hr',
  'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
  'strong', 'em', 'del', 'ins',
  'blockquote',
  'ul', 'ol', 'li',
  'table', 'thead', 'tbody', 'tfoot', 'tr', 'th', 'td',
  'pre', 'code', 'span',
  'a', 'img',
  'input', // GFM task-list checkboxes only -- `type`/`checked`/`disabled`, never `name`/`value`.
];

const ALLOWED_ATTR = [
  'href', 'title', 'alt', 'src',
  'class',   // hljs language + token classes
  'align',   // GFM column alignment (see above)
  'start',   // <ol start="3">
  'type', 'checked', 'disabled', // task-list checkbox
];

/**
 * Sanitize marked's HTML for the `v-html` bindings.
 *
 * `ALLOW_DATA_ATTR`/`ALLOW_ARIA_ATTR` are off: markdown emits neither, and leaving them on keeps
 * an attribute channel open for no rendering benefit. Everything not listed above -- `<script>`,
 * `<iframe>`, `<style>`, `<form>`, SVG/MathML, every `on*` handler, `javascript:` URLs and
 * `target` -- is dropped.
 */
function sanitize(html: string): string {
  return DOMPurify.sanitize(html, {
    ALLOWED_TAGS,
    ALLOWED_ATTR,
    ALLOW_DATA_ATTR: false,
    ALLOW_ARIA_ATTR: false,
  });
}

/**
 * Parse markdown text to sanitized HTML, safe for a `v-html` binding.
 *
 * `highlight: false` skips syntax highlighting. Highlighting a fence is O(fence length), and a
 * streaming message re-parses its whole accumulated text on every delta, so highlighting a
 * growing fence per chunk is quadratic on the main thread (measured: 500 incremental parses of
 * a 10 KB JS fence took 2.02 s highlighted vs 8.5 ms plain). Callers rendering a message that
 * is still streaming should pass `false` and re-render highlighted once it completes.
 */
export function parseMarkdown(text: string, options?: { highlight?: boolean }): string {
  if (!text) return '';
  const instance = options?.highlight === false ? markedPlain : markedHighlighted;
  return sanitize(instance.parse(text) as string);
}
