import { sanitizeDiagramSvg } from './diagramRenderer';

/*
 * ```smiles fences -> 2D structure drawings.
 *
 * SMILES is a line notation for a molecule (`CCO` is ethanol). `smiles-drawer` parses it and draws
 * into an SVG element entirely in this browser -- nothing is fetched and no structure leaves the
 * page. The resulting SVG goes through `sanitizeDiagramSvg`, the SAME policy Mermaid and PlantUML
 * output already passes through, so generated markup is never widened into the Markdown allowlist.
 */

const MAX_SMILES_LENGTH = 20_000;
/** One fence is a figure group, not a document. Past this the fence is almost certainly data. */
const MAX_STRUCTURES = 24;
const DRAW_WIDTH = 420;
const DRAW_HEIGHT = 320;

export class SmilesRenderError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SmilesRenderError';
  }
}

export interface SmilesEntry {
  smiles: string;
  /** Optional trailing caption on the same line, e.g. `CCO ethanol`. */
  label: string;
}

/**
 * Split a fence body into one structure per line.
 *
 * SMILES itself never contains whitespace, so the first token is the structure and whatever follows
 * is a caption. Blank lines and `#` comments are dropped.
 */
export function parseSmilesFence(source: string): SmilesEntry[] {
  const entries: SmilesEntry[] = [];
  for (const line of source.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;
    const boundary = trimmed.search(/\s/);
    entries.push(
      boundary < 0
        ? { smiles: trimmed, label: '' }
        : { smiles: trimmed.slice(0, boundary), label: trimmed.slice(boundary).trim() }
    );
    if (entries.length === MAX_STRUCTURES) break;
  }
  return entries;
}

type SmilesDrawerNamespace = {
  parse(smiles: string, onSuccess: (tree: unknown) => void, onError: (error: Error) => void): void;
  SvgDrawer: new (options: Record<string, unknown>) => {
    draw(tree: unknown, target: Element, themeName: string): void;
  };
};

let drawerReady: Promise<SmilesDrawerNamespace> | null = null;

function prepareDrawer(): Promise<SmilesDrawerNamespace> {
  // Dynamic import: the drawer is ~500 KB of parser and layout code that most sessions never need.
  drawerReady ??= import('smiles-drawer').then(
    (module) => ((module as { default?: unknown }).default ?? module) as SmilesDrawerNamespace
  );
  return drawerReady;
}

let idSequence = 0;

/**
 * Rename every generated fragment id to a prefix that starts with a letter, and rewrite the
 * `url(#…)` references that point at it.
 *
 * `smiles-drawer` seeds its ids from `Math.random()` over `[A-Za-z0-9]`, so roughly one drawing in
 * six gets an id whose first character is a digit. `sanitizeDiagramSvg` requires a fragment
 * reference to match `^#[A-Za-z_][\w:.-]*$` and drops the attribute otherwise -- which would take
 * the bond's gradient `stroke` or the label `mask` with it, leaving invisible bonds on an
 * unpredictable subset of molecules. Renaming makes that deterministic instead of a coin flip.
 */
function normalizeFragmentIds(svg: Element, prefix: string): void {
  const renamed = new Map<string, string>();
  let next = 0;
  for (const element of svg.querySelectorAll('[id]')) {
    const current = element.getAttribute('id');
    if (!current) continue;
    const replacement = `${prefix}-${next++}`;
    renamed.set(current, replacement);
    element.setAttribute('id', replacement);
  }
  if (renamed.size === 0) return;
  for (const element of [svg, ...svg.querySelectorAll('*')]) {
    for (const attribute of [...element.attributes]) {
      if (!/url\(\s*#/i.test(attribute.value)) continue;
      element.setAttribute(
        attribute.name,
        attribute.value.replace(/url\(\s*#([^)\s]+)\s*\)/gi, (whole, id: string) =>
          renamed.has(id) ? `url(#${renamed.get(id)})` : whole
        )
      );
    }
  }
}

function parseStructure(
  drawer: SmilesDrawerNamespace,
  smiles: string
): Promise<unknown> {
  return new Promise((resolve, reject) => {
    try {
      drawer.parse(smiles, resolve, (error) =>
        reject(new SmilesRenderError(error?.message?.trim() || 'The structure could not be parsed.'))
      );
    } catch (error) {
      reject(error);
    }
  });
}

/** Draw one SMILES string in this browser and return a complete, sanitized SVG string. */
export async function renderSmiles(smiles: string): Promise<string> {
  const source = smiles.trim();
  if (!source) throw new SmilesRenderError('The structure is empty.');
  if (source.length > MAX_SMILES_LENGTH) {
    throw new SmilesRenderError('The structure is too large to render safely.');
  }

  const drawer = await prepareDrawer();
  const tree = await parseStructure(drawer, source);

  // The drawer measures text through the live layout, so the host has to be in the document. The
  // offscreen positioning goes on a WRAPPER, never on the svg itself -- the svg is serialized, and
  // a `position:absolute; left:-10000px` inline style would travel with it into the figure. Both
  // are removed before this function returns; only the serialized markup escapes.
  const stage = document.createElement('div');
  stage.style.cssText = 'position:absolute;left:-10000px;top:0;width:0;height:0;overflow:hidden';
  const host = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  host.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  stage.appendChild(host);
  document.body.appendChild(stage);
  try {
    new drawer.SvgDrawer({ width: DRAW_WIDTH, height: DRAW_HEIGHT }).draw(tree, host, 'light');
    normalizeFragmentIds(host, `smiles-${(idSequence++).toString(36)}`);
    return sanitizeDiagramSvg(new XMLSerializer().serializeToString(host));
  } catch (error) {
    throw error instanceof SmilesRenderError
      ? error
      : new SmilesRenderError(
          (error instanceof Error ? error.message : String(error)).trim() ||
            'The structure could not be drawn.'
        );
  } finally {
    stage.remove();
  }
}
