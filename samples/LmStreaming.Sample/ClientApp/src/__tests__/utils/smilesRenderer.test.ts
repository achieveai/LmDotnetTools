import { describe, expect, it } from 'vitest';
import { SmilesRenderError, parseSmilesFence, renderSmiles } from '@/utils/smilesRenderer';

describe('parseSmilesFence', () => {
  it('splits one structure per line and keeps a trailing label', () => {
    expect(parseSmilesFence('CCO\n\nc1ccccc1  benzene\n')).toEqual([
      { smiles: 'CCO', label: '' },
      { smiles: 'c1ccccc1', label: 'benzene' },
    ]);
  });

  it('ignores comment lines and caps the number of structures', () => {
    const many = ['# ignored', ...Array.from({ length: 40 }, () => 'CCO')].join('\n');
    const parsed = parseSmilesFence(many);
    expect(parsed.length).toBeLessThanOrEqual(24);
    expect(parsed.every((entry) => entry.smiles === 'CCO')).toBe(true);
  });
});

describe('renderSmiles', () => {
  it('returns a sanitized standalone SVG for a valid structure', async () => {
    const svg = await renderSmiles('CCO');
    expect(svg.startsWith('<svg')).toBe(true);
    expect(svg).not.toMatch(/\son[a-z]+=/i);
    expect(svg).not.toContain('javascript:');
    expect(svg).toContain('<line');
  });

  it('rewrites generated fragment ids so every url(#…) reference survives sanitization', async () => {
    // smiles-drawer seeds ids from Math.random() over [A-Za-z0-9], so ~16% of drawings get an id
    // starting with a digit -- which sanitizeDiagramSvg rejects, silently dropping the gradient
    // stroke or the text mask that references it. Ids are renamed before sanitizing.
    const svg = await renderSmiles('c1ccccc1O');
    const ids = [...svg.matchAll(/\sid="([^"]*)"/g)].map((m) => m[1]);
    const references = [...svg.matchAll(/url\(#([^)]*)\)/g)].map((m) => m[1]);
    expect(ids.length).toBeGreaterThan(0);
    expect(references.length).toBeGreaterThan(0);
    for (const id of ids) expect(id).toMatch(/^[A-Za-z_][\w-]*$/);
    for (const reference of references) expect(ids).toContain(reference);
  });

  it('rejects an invalid structure and carries the parser message', async () => {
    await expect(renderSmiles('C(((')).rejects.toBeInstanceOf(SmilesRenderError);
    await expect(renderSmiles('C(((')).rejects.toThrow(/parenthes/i);
    await expect(renderSmiles('not a molecule!!')).rejects.toThrow(/Expected/);
  });

  it('rejects empty and oversized input before the parser runs', async () => {
    await expect(renderSmiles('   ')).rejects.toThrow(/empty/i);
    await expect(renderSmiles('C'.repeat(20_001))).rejects.toThrow(/too (large|long)/i);
  });
});
