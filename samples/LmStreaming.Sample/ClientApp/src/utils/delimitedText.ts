/**
 * CSV/TSV parsing for the file preview modal. Hand-rolled rather than a dependency: the preview endpoint
 * already caps a file at 256 KiB / 5000 lines, and RFC 4180 (quoted fields, doubled quotes, embedded
 * newlines, CRLF) is all a table preview needs.
 */

export type Delimiter = ',' | '\t';

export interface DelimitedTable {
  rows: string[][];
  /** True when the input had more rows than `maxRows`. */
  truncated: boolean;
}

/** `,` for `.csv`, a tab for `.tsv`, otherwise null (not a table). */
export function delimiterForPath(path: string): Delimiter | null {
  const lower = path.toLowerCase();
  if (lower.endsWith('.csv')) return ',';
  if (lower.endsWith('.tsv')) return '\t';
  return null;
}

export function parseDelimitedText(text: string, delimiter: Delimiter, maxRows = 1000): DelimitedTable {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = '';
  let inQuotes = false;
  let i = 0;

  const endRow = (): boolean => {
    row.push(field);
    rows.push(row);
    row = [];
    field = '';
    return rows.length > maxRows;
  };

  while (i < text.length) {
    const ch = text[i];
    if (inQuotes) {
      if (ch === '"') {
        if (text[i + 1] === '"') {
          field += '"';
          i += 2;
          continue;
        }
        inQuotes = false;
      } else {
        field += ch;
      }
      i++;
      continue;
    }

    if (ch === '"' && field.length === 0) {
      inQuotes = true;
    } else if (ch === delimiter) {
      row.push(field);
      field = '';
    } else if (ch === '\r' && text[i + 1] === '\n') {
      if (endRow()) break;
      i++;
    } else if (ch === '\n') {
      if (endRow()) break;
    } else {
      field += ch;
    }
    i++;
  }

  // The final row, unless the text ended with its newline (or the cap already stopped us).
  if (rows.length <= maxRows && (field.length > 0 || row.length > 0)) {
    endRow();
  }

  const truncated = rows.length > maxRows;
  return { rows: truncated ? rows.slice(0, maxRows) : rows, truncated };
}
