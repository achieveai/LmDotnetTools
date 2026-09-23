import { describe, it, expect } from 'vitest';
import { delimitedTablePreview, delimiterForPath, parseDelimitedText } from '@/utils/delimitedText';

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

describe('delimitedTablePreview', () => {
  it('returns null for a path that is not delimited', () => {
    expect(delimitedTablePreview('notes.md', 'a,b')).toBeNull();
  });

  it('wraps a CSV as a single-sheet table named after the file', () => {
    const preview = delimitedTablePreview('data/items.csv', 'name,qty\npear,2');

    expect(preview).toEqual({
      sheets: [
        {
          name: 'items.csv',
          rows: [
            ['name', 'qty'],
            ['pear', '2'],
          ],
          truncated: false,
        },
      ],
      truncated: false,
    });
  });

  it('carries the row cap through as the sheet truncation flag', () => {
    const text = Array.from({ length: 10 }, (_, i) => `r${i}`).join('\n');

    const preview = delimitedTablePreview('a.csv', text, 4);

    expect(preview!.sheets[0].rows).toHaveLength(4);
    expect(preview!.sheets[0].truncated).toBe(true);
    // A delimited file is always ONE sheet, so nothing can be dropped at the workbook level.
    expect(preview!.truncated).toBe(false);
  });

  it('uses a tab for TSV', () => {
    expect(delimitedTablePreview('out.tsv', 'a\tb')!.sheets[0].rows).toEqual([['a', 'b']]);
  });
});
