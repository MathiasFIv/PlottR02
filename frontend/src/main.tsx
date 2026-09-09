import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Provider } from 'jotai'
import { registerSW } from 'virtual:pwa-register'
import './index.css'
import App from './App.tsx'

// App-shell PWA: register the service worker so the app shell is precached for
// offline loading. This is a no-op in dev (devOptions.enabled is false) and
// only takes effect in production builds.
// TODO(phase-B): capture the returned updateSW callback and wire it to an
// update-prompt UI (registerType is 'prompt', so without this the waiting
// service worker only activates when all tabs close).
registerSW({ immediate: true })

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Provider>
      <App />
    </Provider>
  </StrictMode>,
)