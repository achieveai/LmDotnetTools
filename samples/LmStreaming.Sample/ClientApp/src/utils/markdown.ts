import { marked } from 'marked';
import { markedHighlight } from 'marked-highlight';
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

marked.use({
  breaks: true, // Enable GFM line breaks
  gfm: true     // Enable GFM
});

/*
 * marked dropped its built-in `highlight` option; `marked-highlight` is the supported
 * replacement. The `hljs ` prefix on the class is what the highlight.js theme selects on --
 * without it the emitted token spans are styled but the block itself is not.
 */
marked.use(
  markedHighlight({
    langPrefix: 'hljs language-',
    emptyLangClass: 'hljs',
    highlight(code: string, lang: string): string {
      // An unknown or absent language must degrade to escaped plain text, never throw:
      // a model can label a fence with anything at all (```mermaid, ```text, ```).
      const language = lang && hljs.getLanguage(lang) ? lang : 'plaintext';
      return hljs.highlight(code, { language, ignoreIllegals: true }).value;
    },
  })
);

/**
 * Parse markdown text to HTML
 */
export function parseMarkdown(text: string): string {
  if (!text) return '';
  return marked.parse(text) as string;
}
