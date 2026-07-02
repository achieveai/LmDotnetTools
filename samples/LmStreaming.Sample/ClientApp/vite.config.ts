import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'path';

// Dev-server port and backend proxy target are configurable so an isolated instance can run
// alongside another without colliding on 5173/5000. VITE_DEV_PORT sets the dev-server port;
// VITE_BACKEND_ORIGIN (e.g. http://localhost:5098) points the /api and /ws proxies at the paired
// backend. Both default to the standard single-instance values.
const devPort = Number(process.env.VITE_DEV_PORT) || 5173;
const backendOrigin = process.env.VITE_BACKEND_ORIGIN || 'http://localhost:5000';
const wsOrigin = backendOrigin.replace(/^http/, 'ws');

export default defineConfig({
  plugins: [vue()],
  base: '/dist/',
  build: {
    outDir: '../wwwroot/dist',
    emptyOutDir: true,
    manifest: true,
    rollupOptions: {
      input: resolve(__dirname, 'index.html'),
    },
  },
  server: {
    port: devPort,
    strictPort: true,
    proxy: {
      '/api': {
        target: backendOrigin,
        changeOrigin: true,
      },
      '/ws': {
        target: wsOrigin,
        ws: true,
        changeOrigin: true,
      },
    },
  },
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
    },
  },
});
