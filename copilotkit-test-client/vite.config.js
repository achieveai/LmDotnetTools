import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: 'http://localhost:5264',
        changeOrigin: true,
      },
      '/ag-ui/ws': {
        target: 'ws://localhost:5264',
        ws: true,
      },
    },
  },
})
