import { beforeEach, describe, expect, it, vi } from 'vitest';

const mermaid = vi.hoisted(() => ({ initialize: vi.fn(), render: vi.fn() }));
const plantUml = vi.hoisted(() => ({ renderToString: vi.fn() }));

vi.mock('mermaid', () => ({ default: mermaid }));
vi.mock('@plantuml/core', () => plantUml);
vi.mock('@plantuml/core/themes.js', () => ({}));

import { DiagramRenderError, renderDiagram, sanitizeDiagramSvg } from '@/utils/diagramRenderer';

describe('diagramRenderer', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (globalThis as { Viz?: unknown }).Viz = {};
    mermaid.render.mockResolvedValue({
      svg: '<svg xmlns="http://www.w3.org/2000/svg"><text>Safe</text></svg>',
    });
  });

  it('lazy-renders Mermaid with strict settings and returns sanitized SVG', async () => {
    const svg = await renderDiagram('flowchart LR\nA-->B', 'mermaid');
    expect(mermaid.initialize).toHaveBeenCalledWith(expect.objectContaining({
      securityLevel: 'strict', htmlLabels: false, startOnLoad: false, theme: 'base',
      secure: ['securityLevel', 'htmlLabels', 'flowchart'],
    }));
    expect(svg).toContain('<text>Safe</text>');
  });

  it('serializes concurrent Mermaid renders', async () => {
    let finishFirst!: () => void;
    mermaid.render
      .mockImplementationOnce(
        () => new Promise((resolve) => {
          finishFirst = () => resolve({ svg: '<svg xmlns="http://www.w3.org/2000/svg"/>' });
        })
      )
      .mockResolvedValueOnce({ svg: '<svg xmlns="http://www.w3.org/2000/svg"/>' });
    const first = renderDiagram('flowchart LR\nA-->B', 'mermaid');
    const second = renderDiagram('flowchart LR\nC-->D', 'mermaid');
    await vi.waitFor(() => expect(mermaid.render).toHaveBeenCalledTimes(1));
    finishFirst();
    await Promise.all([first, second]);
    expect(mermaid.render).toHaveBeenCalledTimes(2);
  });

  it('normalizes single escaped newlines only in traditional quoted flowchart node labels', async () => {
    const source = [
      'flowchart TB',
      'N1["VM 1\\nLeader\\nAPI"]',
      'subgraph AUTH["Authority\\nBoundary"]',
      'N2["Keep \\\\n literal"]',
      'N3["`Markdown \\n label`"]',
    ].join('\n');
    await renderDiagram(source, 'mermaid');
    expect(mermaid.render).toHaveBeenCalledWith(
      expect.any(String),
      [
        'flowchart TB',
        'N1["VM 1<br/>Leader<br/>API"]',
        'subgraph AUTH["Authority<br/>Boundary"]',
        'N2["Keep \\\\n literal"]',
        'N3["`Markdown \\n label`"]',
      ].join('\n')
    );
  });

  it('does not normalize escaped newlines in non-flowchart Mermaid diagrams', async () => {
    const source = 'sequenceDiagram\nAlice->>Bob: first\\nsecond';
    await renderDiagram(source, 'mermaid');
    expect(mermaid.render).toHaveBeenCalledWith(expect.any(String), source);
  });

  it('keeps isolated engine styles but drops CSS that can load an external resource', () => {
    const svg = sanitizeDiagramSvg(
      '<svg xmlns="http://www.w3.org/2000/svg">' +
        '<style>.node{fill:#eef4fb;stroke:#8da9c4}</style>' +
        '<style>.bad{fill:url(https://example.com/fill.svg)}</style><rect class="node"/></svg>'
    );
    expect(svg).toContain('.node{fill:#eef4fb;stroke:#8da9c4}');
    expect(svg).not.toContain('example.com');
  });

  it('drops escaped and comment-obfuscated CSS references from blocks and inline styles', () => {
    const svg = sanitizeDiagramSvg(
      '<svg xmlns="http://www.w3.org/2000/svg">' +
        '<style>.escaped{fill:u\\72l(https://example.com/a)}</style>' +
        '<style>@im/**/port url(https://example.com/b);</style>' +
        '<rect style="fill:u\\72l(https://example.com/c)"/></svg>'
    );
    expect(svg).not.toMatch(/example\.com|@im|\\72/i);
    expect(svg).not.toContain('style=');
  });

  it('removes active content and external references from renderer output', () => {
    const svg = sanitizeDiagramSvg(
      '<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script>' +
        '<a href="https://example.com"><text onclick="bad()">bad</text></a>' +
        '<use href="#safe"/><image href="data:image/png,abc"/></svg>'
    );
    expect(svg).not.toMatch(/script|onclick|https:|data:image/i);
    // DOMPurify may conservatively remove <use>; any retained reference must be fragment-only.
    expect(svg).not.toMatch(/href="(?!#)/i);
  });

  it.each([
    ['PlantUML include', '!include https://example.com/a.puml', 'plantuml' as const],
    ['PlantUML local include', '!include <C4/C4_Context>', 'plantuml' as const],
    ['remote Mermaid image', 'flowchart LR\nA[https://example.com/a.png]', 'mermaid' as const],
  ])('rejects %s before loading an engine', async (_label, source, language) => {
    await expect(renderDiagram(source, language)).rejects.toBeInstanceOf(DiagramRenderError);
    expect(mermaid.render).not.toHaveBeenCalled();
    expect(plantUml.renderToString).not.toHaveBeenCalled();
  });

  it('renders PlantUML through the callback API and reports engine errors readably', async () => {
    plantUml.renderToString.mockImplementationOnce((_lines, success) =>
      success('<svg xmlns="http://www.w3.org/2000/svg"><text>UML</text></svg>')
    );
    await expect(renderDiagram('@startuml\nAlice -> Bob\n@enduml', 'plantuml')).resolves.toContain('UML');

    plantUml.renderToString.mockImplementationOnce((_lines, _success, error) => error('Bad syntax'));
    await expect(renderDiagram('@startuml\nbad\n@enduml', 'plantuml')).rejects.toThrow('Bad syntax');
  });
});
