import { marked } from 'marked';

// Configure marked options
marked.use({
  breaks: true, // Enable GFM line breaks
  gfm: true     // Enable GFM
});

/**
 * Parse markdown text to HTML
 */
export function parseMarkdown(text: string): string {
  if (!text) return '';
  return marked.parse(text) as string;
}
