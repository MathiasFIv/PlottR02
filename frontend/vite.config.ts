import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { VitePWA } from 'vite-plugin-pwa'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      // 'prompt' so we can surface an update prompt later via virtual:pwa-register
      // (no autoUpdate — we want to control when the new version applies).
      registerType: 'prompt',
      manifest: {
        name: 'PlottR',
        short_name: 'PlottR',
        description: 'Field plot ratings with offline support',
        theme_color: '#059669',
        background_color: '#f3f4f6',
        display: 'standalone',
        orientation: 'portrait',
        lang: 'en',
        start_url: '/',
        icons: [
          { src: '/pwa-192x192.png', sizes: '192x192', type: 'image/png' },
          { src: '/pwa-512x512.png', sizes: '512x512', type: 'image/png' },
          {
            src: '/maskable-512x512.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'maskable',
          },
        ],
      },
      workbox: {
        // Precache the app-shell (HTML/JS/CSS/assets) for offline loading.
        // The manifest icons (pwa-*.png / maskable-*.png) are precached
        // automatically by the plugin, so exclude them here to avoid
        // duplicate entries in the generated sw.js precache list.
        globPatterns: ['**/*.{js,css,html,svg,png,ico,webp,woff2}'],
        globIgnores: ['**/pwa-*.png', '**/maskable-*.png'],
        // Offline navigation falls back to the cached index.html shell,
        // served via Workbox's NavigationRoute (always from precache).
        // No nav runtimeCaching needed: NavigationRoute already handles all
        // non-/api navigation requests.
        navigateFallback: 'index.html',
        // Never let /api requests be served from the navigation fallback
        // (auth + data must hit the network).
        navigateFallbackDenylist: [/^\/api/],
      },
      // injectManifest omitted -> default generateSW mode (no custom
      // service-worker source file; the tooling generates sw.js).
      // Keep the dev workflow unchanged — no PWA in `npm run dev`.
      devOptions: { enabled: false },
    }),
  ],
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
