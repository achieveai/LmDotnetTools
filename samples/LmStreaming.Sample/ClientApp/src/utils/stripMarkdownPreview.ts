/**
 * Smallest focused helper for a SAFE inline preview of an option's markdown `preview` text (#246,
 * QuestionRich). We deliberately do NOT reach for `marked.parse()` + `v-html` here (that would
 * require sanitizing untrusted HTML and there is no sanitizer library in this project — see
 * `utils/markdown.ts`, which is unsanitized and only used for already-trusted assistant text).
 * Instead, mirroring the existing "rich" tool components (DiffRich/CodeBlockRich/MatchesRich/
 * TerminalRich), we render plain text through `{{ }}` interpolation (Vue auto-escapes), stripping
 * just enough common markdown punctuation so the preview reads cleanly as a plain string.
 *
 * NOT a general-purpose markdown-to-text converter — only strips the handful of inline markers
 * likely to appear in a short option preview (bold/italic/code/links/headings). Never throws.
 */
export function stripMarkdownPreview(text: string | null | undefined): string {
  if (!text) return '';
  return text
    .replace(/`{1,3}([^`]*)`{1,3}/g, '$1') // `code` / ```code```
    .replace(/\*\*([^*]+)\*\*/g, '$1') // **bold**
    .replace(/__([^_]+)__/g, '$1') // __bold__
    .replace(/\*([^*]+)\*/g, '$1') // *italic*
    .replace(/_([^_]+)_/g, '$1') // _italic_
    .replace(/^#{1,6}\s+/gm, '') // # heading
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1') // [text](url)
    .replace(/\s+/g, ' ')
    .trim();
}
