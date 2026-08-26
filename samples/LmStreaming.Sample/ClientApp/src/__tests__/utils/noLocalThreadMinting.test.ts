import { describe, it, expect } from 'vitest';
import fs from 'fs';
import path from 'path';

/**
 * #435 acceptance, enforced rather than grepped by hand: no thread id is minted in the browser.
 *
 * The `/ws` gate refuses a thread id with no metadata row and deliberately does not mint one for it
 * — minting would make unknown ids succeed while taken ids are refused, which is the existence
 * oracle the shared 404 exists to close. So the only id a socket can ever be opened on is one the
 * server minted through `POST /api/conversations`. A locally generated id reintroduces a socket
 * that connects under `Identity:Enforce=false` and is refused the moment the flag is flipped —
 * exactly the failure this issue closed, and invisible to every test that runs with it off.
 */
const SRC_ROOT = path.resolve(__dirname, '../..');

/** Template-literal or concatenated `thread-` id construction, e.g. `` `thread-${Date.now()}` ``. */
const LOCAL_MINT = /['"`]thread-['"`]?\s*[+$]/;

/**
 * Comments are stripped before scanning: the code that USED to mint is worth describing in prose
 * right where it was removed, and a guard that fires on its own explanation would be deleted rather
 * than obeyed.
 */
function code(source: string): string {
  // `//` only counts as a comment at a line start or after whitespace, so neither `https://` nor
  // the `${proto}//${host}` of a URL template swallows the rest of its own line.
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|\s)\/\/.*$/gm, '$1');
}

function productionFiles(dir: string, found: string[] = []): string[] {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      // Tests legitimately name fixture threads `thread-1`; only shipped code is under scrutiny.
      if (entry.name === '__tests__' || entry.name === 'node_modules') continue;
      productionFiles(full, found);
    } else if (/\.(ts|vue)$/.test(entry.name)) {
      found.push(full);
    }
  }
  return found;
}

describe('SPA mints no thread ids of its own (#435)', () => {
  const files = productionFiles(SRC_ROOT);

  it('finds source files to check', () => {
    expect(files.length).toBeGreaterThan(50);
  });

  it('has no locally generated thread id anywhere in shipped code', () => {
    const offenders = files
      .filter((file) => LOCAL_MINT.test(code(fs.readFileSync(file, 'utf-8'))))
      .map((file) => path.relative(SRC_ROOT, file));

    expect(offenders).toEqual([]);
  });
});
