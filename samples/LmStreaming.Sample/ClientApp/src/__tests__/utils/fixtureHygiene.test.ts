import { describe, it, expect } from 'vitest';
import fs from 'fs';
import path from 'path';

/**
 * CI guard for #199 test fixtures. These persisted samples are extracted from real conversations
 * and committed to durable history, so they must carry NO credentials and NO personal identity.
 * The extraction step scrubs tokens; this test enforces the same policy every run so a future
 * fixture can't reintroduce a leak.
 */
const FIXTURE_DIRS = [
  path.resolve(__dirname, '../fixtures/persisted'),
  path.resolve(__dirname, '../fixtures/synthetic'),
];

// Credential shapes (the extraction token-scrub gate) + the repo owner's personal name.
const FORBIDDEN: Array<{ label: string; re: RegExp }> = [
  { label: 'GitHub token', re: /ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}/ },
  { label: 'OpenAI key', re: /sk-[A-Za-z0-9]{20,}/ },
  { label: 'Bearer token', re: /Bearer\s+[A-Za-z0-9._-]{20,}/ },
  { label: 'AWS access key', re: /AKIA[0-9A-Z]{16}/ },
  { label: 'private key', re: /-----BEGIN [A-Z ]*PRIVATE KEY-----/ },
  { label: 'personal name', re: /Gautam\s+Bhakar/i },
];

function fixtureFiles(): string[] {
  const files: string[] = [];
  for (const dir of FIXTURE_DIRS) {
    if (!fs.existsSync(dir)) continue;
    for (const name of fs.readdirSync(dir)) {
      if (name.endsWith('.json')) files.push(path.join(dir, name));
    }
  }
  return files;
}

describe('fixture hygiene', () => {
  const files = fixtureFiles();

  it('finds fixtures to check', () => {
    expect(files.length).toBeGreaterThan(0);
  });

  for (const file of files) {
    it(`no secrets or personal data in ${path.basename(file)}`, () => {
      const content = fs.readFileSync(file, 'utf-8');
      for (const { label, re } of FORBIDDEN) {
        expect(re.test(content), `${label} found in ${path.basename(file)}`).toBe(false);
      }
    });
  }
});
