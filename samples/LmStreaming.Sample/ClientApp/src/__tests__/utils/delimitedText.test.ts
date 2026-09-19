import { describe, it, expect } from 'vitest';
import { delimiterForPath, parseDelimitedText } from '@/utils/delimitedText';

describe('delimiterForPath', () => {
  it.each([
    ['data.csv', ','],
    ['DATA.CSV', ','],
    ['out/table.tsv', '\t'],
    ['notes.txt', null],
    ['csv', null],
  ])('%s -> %j', (path, expected) => {
    expect(delimiterForPath(path)).toBe(expected);
  });
});

describe('parseDelimitedText', () => {
  it('splits simple rows and columns', () => {
    expect(parseDelimitedText('a,b,c\n1,2,3', ',').rows).toEqual([
      ['a', 'b', 'c'],
      ['1', '2', '3'],
    ]);
  });

  it('handles quoted fields with delimiters, doubled quotes and embedded newlines (RFC 4180)', () => {
    const text = 'name,note\n"Smith, J","said ""hi"""\n"multi\nline",x';
    expect(parseDelimitedText(text, ',').rows).toEqual([
      ['name', 'note'],
      ['Smith, J', 'said "hi"'],
      ['multi\nline', 'x'],
    ]);
  });

  it('accepts CRLF line endings and ignores one trailing newline', () => {
    expect(parseDelimitedText('a,b\r\n1,2\r\n', ',').rows).toEqual([
      ['a', 'b'],
      ['1', '2'],
    ]);
  });

  it('keeps empty fields', () => {
    expect(parseDelimitedText('a,,c\n,,', ',').rows).toEqual([
      ['a', '', 'c'],
      ['', '', ''],
    ]);
  });

  it('parses tabs for TSV', () => {
    expect(parseDelimitedText('a\tb\n1\t2', '\t').rows).toEqual([
      ['a', 'b'],
      ['1', '2'],
    ]);
  });

  it('caps the number of rows and reports truncation', () => {
    const text = Array.from({ length: 10 }, (_, i) => `r${i}`).join('\n');
    const result = parseDelimitedText(text, ',', 4);
    expect(result.rows).toEqual([['r0'], ['r1'], ['r2'], ['r3']]);
    expect(result.truncated).toBe(true);
    expect(parseDelimitedText(text, ',', 10).truncated).toBe(false);
  });

  it('caps CRLF rows the same way', () => {
    const text = Array.from({ length: 10 }, (_, i) => `r${i}`).join('\r\n');
    const result = parseDelimitedText(text, ',', 4);
    expect(result.rows).toEqual([['r0'], ['r1'], ['r2'], ['r3']]);
    expect(result.truncated).toBe(true);
  });

  it('returns no rows for empty text', () => {
    expect(parseDelimitedText('', ',').rows).toEqual([]);
  });
});
