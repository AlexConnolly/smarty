import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Smarty.Control is served by Smarty.Api at /control, so it builds under that base. Its API calls go to
// /api/control/* at the origin root. In dev, proxy those to the running Smarty.Api on :5179.
export default defineConfig({
  base: '/control/',
  plugins: [react()],
  server: {
    port: 5174,
    proxy: {
      '/api': 'http://localhost:5179',
      '/health': 'http://localhost:5179',
    },
  },
})
