import { describe, it, expect } from 'vitest';
import { stripMarkdownPreview } from '@/utils/stripMarkdownPreview';

describe('stripMarkdownPreview (#246 safe single-select preview)', () => {
  it('returns empty string for null/undefined/empty input, never throws', () => {
    expect(stripMarkdownPreview(null)).toBe('');
    expect(stripMarkdownPreview(undefined)).toBe('');
    expect(stripMarkdownPreview('')).toBe('');
  });

  it('strips bold/italic/code/heading/link markers, keeping the plain text', () => {
    expect(stripMarkdownPreview('**bold**')).toBe('bold');
    expect(stripMarkdownPreview('__bold__')).toBe('bold');
    expect(stripMarkdownPreview('*italic*')).toBe('italic');
    expect(stripMarkdownPreview('_italic_')).toBe('italic');
    expect(stripMarkdownPreview('`code`')).toBe('code');
    expect(stripMarkdownPreview('```code```')).toBe('code');
    expect(stripMarkdownPreview('# Heading')).toBe('Heading');
    expect(stripMarkdownPreview('[label](https://example.com)')).toBe('label');
  });

  it('collapses internal whitespace/newlines and trims the result', () => {
    expect(stripMarkdownPreview('line one\n\nline   two')).toBe('line one line two');
    expect(stripMarkdownPreview('  padded  ')).toBe('padded');
  });

  it('never emits raw HTML-like markup as anything other than plain text (no v-html consumer needed)', () => {
    const evil = '<img src=x onerror=alert(1)>';
    // Deliberately NOT stripped — this helper only targets markdown punctuation. Safety comes from
    // the caller always rendering the output via {{ }} interpolation, never v-html.
    expect(stripMarkdownPreview(evil)).toContain('<img');
  });
});
