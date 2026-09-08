import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // In dev, forward all /api calls to the ASP.NET Core backend so the
    // frontend talks same-origin (/api/...) and we avoid hardcoding a port
    // and CORS. Matches the backend port in Properties/launchSettings.json.
    proxy: {
      '/api': {
        target: 'http://localhost:5095',
        changeOrigin: true,
      },
    },
  },
})
