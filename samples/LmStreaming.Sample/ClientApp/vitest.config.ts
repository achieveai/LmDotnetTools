import { defineConfig } from 'vitest/config';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'path';

export default defineConfig({
  plugins: [vue()],
  test: {
    /*
     * jsdom, NOT happy-dom: DOMPurify (utils/markdown.ts) silently fails to sanitize under
     * happy-dom 20. It reports `isSupported: true` and populates `DOMPurify.removed`, but the
     * removals do not take -- `<p>hi</p><script>alert(1)</script>` sanitizes to
     * `hi<script>alert(1)</script>`, i.e. the first top-level element is UNWRAPPED and the script
     * SURVIVES. (DOMPurify prepends a `<remove></remove>` sentinel and deletes `body.firstChild`
     * afterwards; happy-dom parses the sentinel but its node removal misfires, so the deletion
     * lands on real content instead.) Under that environment a sanitization test is worse than no
     * test: it would report unfiltered `<script>` output as clean.
     */
    environment: 'jsdom',
    globals: true,
    include: ['src/**/*.test.ts'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json', 'html'],
      include: ['src/**/*.ts', 'src/**/*.vue'],
      exclude: ['src/**/*.test.ts', 'src/__tests__/**'],
    },
  },
  resolve: {
    alias: {
      '@': resolve(__dirname, './src'),
    },
  },
});
