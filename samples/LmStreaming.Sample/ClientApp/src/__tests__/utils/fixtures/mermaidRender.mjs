import { JSDOM } from 'jsdom'

const report = receipt => process.stdout.write(`${JSON.stringify(receipt)}\n`)

const dom = new JSDOM('<!doctype html><html><body></body></html>', {
  pretendToBeVisual: true,
})
for (const name of ['window', 'document', 'navigator', 'Element', 'HTMLElement', 'SVGElement', 'DOMParser', 'XMLSerializer', 'CSSStyleSheet']) {
  Object.defineProperty(globalThis, name, { value: name === 'window' ? dom.window : dom.window[name], configurable: true })
}
globalThis.getComputedStyle = dom.window.getComputedStyle.bind(dom.window)

// jsdom has no SVG layout engine. Supply only text measurement; Mermaid's real
// parser, axis arithmetic, and SVG renderer still run unchanged in this process.
dom.window.SVGElement.prototype.getBBox = function () {
  return { x: 0, y: 0, width: (this.textContent?.length ?? 0) * 8, height: 16 }
}
dom.window.SVGElement.prototype.getComputedTextLength = function () {
  return (this.textContent?.length ?? 0) * 8
}

report({ stage: 'dom-ready' })
const { default: mermaid } = await import('mermaid')
mermaid.initialize({ startOnLoad: false, securityLevel: 'strict' })
report({ stage: 'render-start' })
try {
  const { svg } = await mermaid.render('bounded-xychart', process.argv[2])
  const document = new dom.window.DOMParser().parseFromString(svg, 'image/svg+xml')
  report({
    stage: 'rendered',
    svgLength: svg.length,
    root: document.documentElement.localName,
    parseErrors: document.querySelectorAll('parsererror').length,
    paths: document.querySelectorAll('path').length,
    hasNonFiniteCoordinates: /(?:NaN|Infinity)/.test(svg),
  })
} catch (error) {
  report({ stage: 'render-error', message: String(error) })
} finally {
  dom.window.close()
}
