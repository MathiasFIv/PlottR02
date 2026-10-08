import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Provider, getDefaultStore } from 'jotai'
import './index.css'
import App from './App.tsx'
import { PwaUpdateToast } from './components/PwaUpdateToast.tsx'

// Request persistent storage so the IndexedDB voice recordings are not
// evicted under storage pressure (mostly granted on Android/Chrome).
if (navigator.storage?.persist) {
  void navigator.storage.persist().catch(() => {})
}

// Service-worker registration happens inside PwaUpdateToast (registerType is
// 'prompt', so the SW activates only after the toast's Refresh button calls
// updateSW(true)). The SW is active in dev too (vite.config.ts sets
// devOptions.enabled = true) so the manifest + offline flow can be exercised
// from the dev server / Android emulator, not just in `vite preview` builds.

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