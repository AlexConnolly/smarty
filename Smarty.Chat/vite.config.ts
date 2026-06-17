import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Proxy API calls to the Smarty.Api backend during development.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5179',
      '/health': 'http://localhost:5179',
    },
  },
})
