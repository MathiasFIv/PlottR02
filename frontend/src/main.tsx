import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Provider, getDefaultStore } from 'jotai'
import './index.css'
import App from './App.tsx'
import { PwaUpdateToast } from './components/PwaUpdateToast.tsx'
import { applyTheme, resolveInitialTheme } from './lib/theme.ts'
import { themeAtom } from './store/atoms.ts'

// Seed the theme atom with the SAME value the anti-FOUC inline script in
// index.html already put on <html>, so React's first render agrees with the
// painted theme. DOM-free: resolveInitialTheme only reads storage + matchMedia.
const initialTheme = resolveInitialTheme()
getDefaultStore().set(themeAtom, initialTheme)

// Defensive re-assert of what the inline script painted (harmless if it
// matches) and the one thing the inline script cannot do: update the
// meta[name=theme-color] so the browser-chrome tint matches on first load.
applyTheme(initialTheme)

// Request persistent storage so the IndexedDB voice recordings are not
// evicted under storage pressure (mostly granted on Android/Chrome).
if (navigator.storage?.persist) {
  void navigator.storage.persist().catch(() => {})
}

// Service-worker registration happens inside PwaUpdateToast. vite.config.ts
// uses registerType 'autoUpdate' (devOptions.enabled = true), so a new build
// takes over on the next page load without prompting; the toast's Refresh
// branch only matters if we gate updates with 'prompt' for production.

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {/* `getDefaultStore()` MUST be passed in explicitly. Provider called with
        no `store` creates a private one (jotai/react.js: `storeRef.current =
        vanilla.createStore()`), and `lib/syncQueue.ts` writes atoms from
        non-React code via `getDefaultStore()` — without this line the two
        stores diverge and those writes (the sheet-refresh tick, the
        attention badge) never reach anything React renders. */}
    <Provider store={getDefaultStore()}>
      <PwaUpdateToast />
      <App />
    </Provider>
  </StrictMode>,
)