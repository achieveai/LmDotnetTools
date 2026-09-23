import DOMPurify from 'dompurify';
import plantUmlVizUrl from '@plantuml/core/viz-global.js?url';

export type DiagramLanguage = 'mermaid' | 'plantuml';

const MAX_SOURCE_LENGTH = 50_000;
const MAX_SVG_LENGTH = 2_000_000;
const RENDER_TIMEOUT_MS = 12_000;
// Schemes that only ever appear in order to embed or fetch a resource. Rejected everywhere.
const RESOURCE_SCHEME = /(?:ftp:|file:|data:|blob:)/i;
// Remote locations. Rejected outside quoted strings, and inside the configuration surface even when
// quoted — see validateSource.
const REMOTE_LOCATION = /(?:https?:|\/\/)/i;
const MERMAID_FRONTMATTER = /^\uFEFF?[ \t]*---[ \t]*\r?\n[\s\S]*?\r?\n[ \t]*---[ \t]*(?:\r?\n|$)/;
const MERMAID_DIRECTIVE = /%%\{[\s\S]*?\}%%/g;
const DOUBLE_QUOTED = /"(?:\\.|[^"\\])*"/g;
const XLINK_NAMESPACE = 'http://www.w3.org/1999/xlink';
const ABSOLUTE_LENGTH = /^\d+(?:\.\d+)?(?:px)?$/i;
const PLANTUML_INCLUDE = /^\s*!(?:include|include_once|include_many|import)\b/im;
const PLANTUML_REMOTE_THEME = /^\s*!theme\s+\S+\s+from\b/im;
const SAFE_FRAGMENT_REFERENCE = /^#[A-Za-z_][\w:.-]*$/;

let mermaidInitialized = false;
let mermaidReady: Promise<(typeof import('mermaid'))['default']> | null = null;
let mermaidQueue: Promise<void> = Promise.resolve();
let plantUmlReady: Promise<typeof import('@plantuml/core')> | null = null;
let plantUmlQueue: Promise<void> = Promise.resolve();
let renderSequence = 0;

const FLOWCHART_HEADER = /^\s*(?:---[\s\S]*?---\s*)?(?:%%[^\r\n]*(?:\r?\n|$)\s*)*(?:flowchart|graph)\b/i;
const QUOTED_FLOWCHART_NODE = /((?:^|[\s;])[\w.:-]+\s*(?:\[\[?|\(\(?|\{\{?)\s*[\\/]?")((?:\\.|[^"\\])*)(")/gm;

export class DiagramRenderError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'DiagramRenderError';
  }
}

function validateSource(source: string, language: DiagramLanguage): void {
  if (!source.trim()) throw new DiagramRenderError('The diagram is empty.');
  if (source.length > MAX_SOURCE_LENGTH) {
    throw new DiagramRenderError('The diagram is too large to render safely.');
  }
  // Both engines support constructs that can reference remote images or resources. Rendering is
  // intentionally offline, so reject those before an engine gets a chance to initiate a request.
  //
  // A blanket scan for any URL-looking text also rejected diagrams that merely *mention* one, which
  // is common and harmless: `A["See https://example.com"]`, `click A href "https://…"`, or a path
  // like `"src//legacy"`. What actually causes a fetch is a URL in a position the engine resolves —
  // above all Mermaid's configuration surface, where `themeCSS`/`fontFamily` reach the stylesheet.
  // So the rule is positional:
  //   * ftp:/file:/data:/blob: are rejected anywhere — they exist only to embed or fetch.
  //   * http:/https:/protocol-relative // are rejected anywhere in YAML frontmatter or a %%{…}%%
  //     directive, quoted or not, because quoting is not a safety property there.
  //   * elsewhere they are allowed only inside a double-quoted Mermaid string. Such a URL renders as
  //     label text; sanitizeDiagramSvg still strips every non-fragment href/xlink:href/src from the
  //     output, so it can neither load a resource nor become a live link.
  if (RESOURCE_SCHEME.test(source)) {
    throw new DiagramRenderError('External URLs are not allowed in diagrams.');
  }
  if (language === 'mermaid') {
    const configuration =
      (MERMAID_FRONTMATTER.exec(source)?.[0] ?? '') + (source.match(MERMAID_DIRECTIVE)?.join('\n') ?? '');
    if (REMOTE_LOCATION.test(configuration)) {
      throw new DiagramRenderError('External URLs are not allowed in diagram configuration.');
    }
    if (REMOTE_LOCATION.test(source.replace(MERMAID_DIRECTIVE, '').replace(DOUBLE_QUOTED, '""'))) {
      throw new DiagramRenderError('External URLs are only allowed inside quoted diagram text.');
    }
  } else if (REMOTE_LOCATION.test(source)) {
    throw new DiagramRenderError('External URLs are not allowed in diagrams.');
  }
  if (language === 'plantuml' && (PLANTUML_INCLUDE.test(source) || PLANTUML_REMOTE_THEME.test(source))) {
    throw new DiagramRenderError('PlantUML includes and remote themes are not allowed.');
  }
}

/**
 * Mermaid's traditional quoted flowchart labels document `<br>` as the line-break spelling. Models
 * commonly emit a literal `\n` instead; with SVG text labels that is displayed verbatim. Normalize
 * only those node-label strings for rendering. Stored source, Markdown-string labels, other diagram
 * families, and deliberately double-escaped `\\n` text retain their exact spelling.
 */
function normalizeMermaidFlowchartLabels(source: string): string {
  if (!FLOWCHART_HEADER.test(source)) return source;
  return source.replace(QUOTED_FLOWCHART_NODE, (whole, opening: string, label: string, closing: string) => {
    const trimmed = label.trim();
    if (trimmed.startsWith('`') && trimmed.endsWith('`')) return whole;
    const normalized = label.replace(/(?<!\\)\\n/g, '<br/>');
    return `${opening}${normalized}${closing}`;
  });
}

function withTimeout<T>(work: Promise<T>): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(
      () => reject(new DiagramRenderError('The diagram took too long to render.')),
      RENDER_TIMEOUT_MS
    );
    work.then(
      (value) => {
        window.clearTimeout(timer);
        resolve(value);
      },
      (reason) => {
        window.clearTimeout(timer);
        reject(reason);
      }
    );
  });
}

/** Sanitize engine output as SVG, independently from the Markdown HTML policy. */
export function sanitizeDiagramSvg(svg: string): string {
  if (!svg || svg.length > MAX_SVG_LENGTH) {
    throw new DiagramRenderError('The rendered diagram is too large to display safely.');
  }
  const clean = DOMPurify.sanitize(svg, {
    USE_PROFILES: { svg: true, svgFilters: true },
    FORBID_TAGS: ['script', 'foreignObject', 'iframe', 'object', 'embed', 'audio', 'video'],
    ALLOW_DATA_ATTR: false,
    ALLOW_ARIA_ATTR: true,
  });
  // DOMPurify serializes as HTML, which keeps an `xlink:href` attribute but drops the root's
  // `xmlns:xlink` declaration. Mermaid's C4 renderer emits one on its <image> sprites, so the strict
  // XML re-parse below rejected every C4Context/C4Container diagram. Re-declare the prefix; the
  // attribute scrub further down still removes the reference itself.
  const markup =
    /\sxlink:[a-z]/i.test(clean) && !/xmlns:xlink\s*=/i.test(clean)
      ? clean.replace(/^(\s*<svg\b)/i, `$1 xmlns:xlink="${XLINK_NAMESPACE}"`)
      : clean;
  const document = new DOMParser().parseFromString(markup, 'image/svg+xml');
  if (document.querySelector('parsererror')) {
    throw new DiagramRenderError('The diagram renderer returned invalid SVG.');
  }
  const root = document.documentElement;
  if (root.localName !== 'svg') throw new DiagramRenderError('The diagram renderer did not return SVG.');

  // SVG is displayed as an image by the viewer, so its CSS is document-isolated. Keep the engine's
  // essential node/edge theme, but discard a whole style block if it attempts to import or refer to
  // anything outside this SVG.
  for (const style of root.querySelectorAll('style')) {
    const css = style.textContent ?? '';
    const inspectableCss = css.replace(/\/\*[\s\S]*?\*\//g, '');
    const unsafeKeyword = /@import|expression\s*\(|javascript\s*:/i.test(inspectableCss);
    const references = [...inspectableCss.matchAll(/url\s*\(\s*(['"]?)(.*?)\1\s*\)/gi)];
    if (
      css.includes('\\') ||
      unsafeKeyword ||
      references.some((match) => !SAFE_FRAGMENT_REFERENCE.test(match[2] ?? ''))
    ) {
      style.remove();
    }
  }

  for (const element of [root, ...root.querySelectorAll('*')]) {
    for (const attribute of [...element.attributes]) {
      const name = attribute.name.toLowerCase();
      const value = attribute.value.trim();
      if (name.startsWith('on')) {
        element.removeAttribute(attribute.name);
      } else if (name === 'href' || name === 'xlink:href') {
        if (!SAFE_FRAGMENT_REFERENCE.test(value)) element.removeAttribute(attribute.name);
      } else if (name === 'src') {
        element.removeAttribute(attribute.name);
      } else if (name === 'style') {
        const inspectableValue = value.replace(/\/\*[\s\S]*?\*\//g, '');
        const references = [...inspectableValue.matchAll(/url\s*\(\s*(['"]?)(.*?)\1\s*\)/gi)];
        if (
          value.includes('\\') ||
          /@import|expression\s*\(|javascript\s*:/i.test(inspectableValue) ||
          references.some((match) => !SAFE_FRAGMENT_REFERENCE.test(match[2] ?? ''))
        ) {
          element.removeAttribute(attribute.name);
        }
      } else if (/url\s*\(/i.test(value)) {
        const references = [...value.matchAll(/url\s*\(\s*(['"]?)(.*?)\1\s*\)/gi)];
        if (references.some((match) => !SAFE_FRAGMENT_REFERENCE.test(match[2] ?? ''))) {
          element.removeAttribute(attribute.name);
        }
      }
    }
  }

  // Mermaid emits width="100%" and no height. The viewer shows the SVG through an <img>, where a
  // percentage width means "no intrinsic size", so the browser falls back to the 300x150 default
  // object size and then scales that to the viewer's box — a 326x68 diagram was being blown up to
  // 1834x380. Publish the viewBox as the intrinsic size so small diagrams render at their own scale.
  if (!ABSOLUTE_LENGTH.test(root.getAttribute('width') ?? '') ||
      !ABSOLUTE_LENGTH.test(root.getAttribute('height') ?? '')) {
    const box = (root.getAttribute('viewBox') ?? '').trim().split(/[\s,]+/).map(Number);
    if (box.length === 4 && box.every(Number.isFinite) && box[2] > 0 && box[3] > 0) {
      root.setAttribute('width', String(box[2]));
      root.setAttribute('height', String(box[3]));
    }
  }
  return new XMLSerializer().serializeToString(root);
}

function prepareMermaid(): Promise<(typeof import('mermaid'))['default']> {
  mermaidReady ??= import('mermaid').then(({ default: mermaid }) => {
    if (!mermaidInitialized) mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      htmlLabels: false,
      flowchart: { htmlLabels: false },
      suppressErrorRendering: true,
      // Diagram frontmatter/directives cannot weaken the HTML-free rendering boundary, nor replace
      // the stylesheet repair below with CSS of their own. Mermaid merges this with its own secure
      // list rather than replacing it.
      secure: ['securityLevel', 'htmlLabels', 'flowchart', 'themeCSS'],
      theme: 'base',
      // With htmlLabels off, an edge/relationship label is
      // `<g class="label"><rect class="background"/><text/></g>`, and several diagram stylesheets
      // set a single `fill` on `.label` — which the background rect inherits too, painting the text
      // in exactly its own colour. ER relationship labels and requirement relationship labels were
      // rendering as solid unreadable bars. Give the box and the glyphs separate colours.
      themeCSS: [
        '.edgeLabel .label rect.background { fill: #ffffff; }',
        '.edgeLabel .label text, .edgeLabel .label tspan { fill: #34404d; }',
        '.reqLabelBox { fill: #ffffff; }',
        '.relationshipLabel, .reqLabel { fill: #34404d; }',
      ].join('\n'),
      themeVariables: {
        fontFamily: 'Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
        background: '#ffffff',
        primaryColor: '#eef4fb',
        primaryBorderColor: '#8da9c4',
        primaryTextColor: '#34404d',
        lineColor: '#6f8194',
        textColor: '#34404d',
        clusterBkg: '#f7f9fc',
        clusterBorder: '#c7d2df',
      },
    });
    mermaidInitialized = true;
    return mermaid;
  });
  return mermaidReady;
}

async function renderMermaid(
  mermaid: (typeof import('mermaid'))['default'],
  source: string
): Promise<string> {
  const id = `diagram-${Date.now().toString(36)}-${renderSequence++}`;
  const { svg } = await mermaid.render(id, normalizeMermaidFlowchartLabels(source));
  return svg;
}

function loadClassicScript(url: string): Promise<void> {
  if ((globalThis as { Viz?: unknown }).Viz) return Promise.resolve();
  return new Promise<void>((resolve, reject) => {
    const existing = document.querySelector<HTMLScriptElement>('script[data-plantuml-viz]');
    if (existing) {
      existing.addEventListener('load', () => resolve(), { once: true });
      existing.addEventListener('error', () => reject(new Error('Graphviz failed to load.')), { once: true });
      return;
    }
    const script = document.createElement('script');
    script.src = url;
    script.dataset.plantumlViz = 'true';
    script.addEventListener('load', () => resolve(), { once: true });
    script.addEventListener('error', () => reject(new Error('Graphviz failed to load.')), { once: true });
    document.head.appendChild(script);
  });
}

function preparePlantUml(): Promise<typeof import('@plantuml/core')> {
  plantUmlReady ??= Promise.all([
    loadClassicScript(plantUmlVizUrl),
    // Register only the package's local theme table. Standard-library includes stay disabled.
    import('@plantuml/core/themes.js').then(() => undefined),
    import('@plantuml/core'),
  ]).then(([, , engine]) => engine);
  return plantUmlReady;
}

async function renderPlantUml(
  engine: typeof import('@plantuml/core'),
  source: string
): Promise<string> {
  return new Promise<string>((resolve, reject) => {
    try {
      engine.renderToString(source.split(/\r\n|\r|\n/), resolve, (message) => reject(new Error(message)));
    } catch (error) {
      reject(error);
    }
  });
}

function readableError(error: unknown): DiagramRenderError {
  if (error instanceof DiagramRenderError) return error;
  const message = error instanceof Error ? error.message : String(error);
  return new DiagramRenderError(message.trim() || 'The diagram could not be rendered.');
}

/** Render a diagram entirely in this browser and return a complete, sanitized SVG string. */
export async function renderDiagram(source: string, language: DiagramLanguage): Promise<string> {
  try {
    validateSource(source, language);
    if (language === 'mermaid') {
      const mermaid = await prepareMermaid();
      const previous = mermaidQueue;
      let release!: () => void;
      mermaidQueue = new Promise<void>((resolve) => (release = resolve));
      await previous;
      const renderWork = renderMermaid(mermaid, source);
      void renderWork.then(release, release);
      return sanitizeDiagramSvg(await withTimeout(renderWork));
    }

    // The TeaVM engine has shared mutable render state. Serialize calls in one browsing context.
    const engine = await preparePlantUml();
    const previous = plantUmlQueue;
    let release!: () => void;
    plantUmlQueue = new Promise<void>((resolve) => (release = resolve));
    await previous;
    const renderWork = renderPlantUml(engine, source);
    // A caller may time out, but the shared engine remains occupied until its real callback settles.
    void renderWork.then(release, release);
    return sanitizeDiagramSvg(await withTimeout(renderWork));
  } catch (error) {
    throw readableError(error);
  }
}
