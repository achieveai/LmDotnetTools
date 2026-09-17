import DOMPurify from 'dompurify';
import plantUmlVizUrl from '@plantuml/core/viz-global.js?url';

export type DiagramLanguage = 'mermaid' | 'plantuml';

const MAX_SOURCE_LENGTH = 50_000;
const MAX_SVG_LENGTH = 2_000_000;
const RENDER_TIMEOUT_MS = 12_000;
const EXTERNAL_REFERENCE = /(?:https?:|ftp:|file:|data:|blob:|\/\/)/i;
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
  if (EXTERNAL_REFERENCE.test(source)) {
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
  const document = new DOMParser().parseFromString(clean, 'image/svg+xml');
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
      // Diagram frontmatter/directives cannot weaken the HTML-free rendering boundary.
      secure: ['securityLevel', 'htmlLabels', 'flowchart'],
      theme: 'base',
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
