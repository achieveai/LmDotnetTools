// @vitest-environment node
import { execFile } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

interface RenderReceipt {
  stage: string
  svgLength?: number
  root?: string
  parseErrors?: number
  paths?: number
  hasNonFiniteCoordinates?: boolean
  message?: string
}

async function renderIsolated(source: string): Promise<RenderReceipt[]> {
  const fixture = fileURLToPath(new URL('./fixtures/mermaidRender.mjs', import.meta.url))
  // A separate process contains even fatal V8 OOMs. The parent owns the timeout,
  // and execFile's callback runs after child termination and output collection.
  return new Promise((resolve, reject) => {
    execFile(process.execPath, ['--max-old-space-size=256', fixture, source], {
      timeout: 15_000,
      killSignal: 'SIGKILL',
      maxBuffer: 32_768,
      windowsHide: true,
    }, (error, stdout, stderr) => {
      try {
        const receipts = stdout.trim().split('\n').filter(Boolean).map(line => JSON.parse(line) as RenderReceipt)
        if (error) receipts.push({
          stage: error.code === 'ERR_CHILD_PROCESS_STDIO_MAXBUFFER' ? 'output-limit' : error.killed ? 'parent-timeout' : 'process-error',
          message: stderr || error.message,
        })
        resolve(receipts)
      } catch (parseError) {
        reject(parseError)
      }
    })
  })
}

function expectFiniteSvg(receipts: RenderReceipt[]) {
  console.info(JSON.stringify({ test: 'mermaid-numeric-range', receipts }))
  // A render followed by a timeout/crash is still a failure; require clean exit.
  const result = receipts.at(-1)
  expect(result, JSON.stringify(receipts)).toMatchObject({
    stage: 'rendered',
    root: 'svg',
    parseErrors: 0,
    hasNonFiniteCoordinates: false,
  })
  expect(result?.svgLength).toBeGreaterThan(100)
  expect(result?.paths).toBeGreaterThan(0)
}

describe('real Mermaid numeric axes', () => {
  it('renders ordinary and equal-endpoint XY charts without hanging', async () => {
    // The control must work in the same isolated DOM before the advisory case
    // can establish a dependency regression (GHSA-2v8p-3f2j-5mp7).
    expectFiniteSvg(await renderIsolated('xychart\n  x-axis 1 --> 2\n  line [1, 2]'))
    expectFiniteSvg(await renderIsolated('xychart\n  x-axis 1 --> 1\n  line [1, 2]'))
  }, 35_000)
})
