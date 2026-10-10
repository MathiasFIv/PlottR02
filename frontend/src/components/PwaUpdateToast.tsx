import { useEffect, useState } from 'react';
import { registerSW } from 'virtual:pwa-register';

/**
 * Service-worker lifecycle toast.
 *
 * `registerSW` (via `virtual:pwa-register`) is wired exactly once per mount
 * inside a `useState` initializer. During iteration the app runs
 * `registerType: 'autoUpdate'`, so new builds take over on the next page
 * load and `onNeedRefresh` never fires. `onOfflineReady` fires the first
 * time the shell is fully cached for offline use.
 *
 * The Refresh branch stays for production: switch vite.config back to
 * `registerType: 'prompt'` and this toast becomes the update gate (D5) —
 * the waiting worker only activates when the user presses Refresh.
 *
 * If neither state is active the component renders nothing.
 */
export function PwaUpdateToast() {
  const [needRefresh, setNeedRefresh] = useState(false);
  const [offlineReady, setOfflineReady] = useState(false);

  const [updateSW] = useState(() =>
    registerSW({
      immediate: true,
      onNeedRefresh: () => setNeedRefresh(true),
      onOfflineReady: () => setOfflineReady(true),
    }),
  );

  // Auto-dismiss the "ready offline" toast after ~4s; clear the timer on
  // unmount / when the toast disappears for any other reason.
  useEffect(() => {
    if (!offlineReady) return;
    const timer = window.setTimeout(() => setOfflineReady(false), 4000);
    return () => window.clearTimeout(timer);
  }, [offlineReady]);

  if (needRefresh) {
    return (
      <div className="toast toast-center toast-bottom z-[1000]" role="status">
        {/* daisyUI 5 has no `alert-neutral`; the old toast was a dark chip
            (#111827 / #fff), so we style it explicitly with `bg-neutral
            text-neutral-content` rather than `.alert`. */}
        <div className="bg-neutral text-neutral-content rounded-lg shadow-lg flex items-center gap-4 px-4 py-3 text-sm max-w-[calc(100vw-2rem)]">
          <span className="flex-1">New version available.</span>
          <div className="flex gap-2 shrink-0">
            <button
              type="button"
              className="btn btn-primary btn-sm"
              onClick={() => void updateSW(true)}
            >
              Refresh
            </button>
          </div>
        </div>
      </div>
    );
  }

  if (offlineReady) {
    return (
      <div className="toast toast-center toast-bottom z-[1000]" role="status">
        <div className="bg-neutral text-neutral-content rounded-lg shadow-lg flex items-center gap-4 px-4 py-3 text-sm max-w-[calc(100vw-2rem)]">
          <span className="flex-1">PlottR is ready to work offline.</span>
          <div className="flex gap-2 shrink-0">
            <button
              type="button"
              className="btn btn-ghost btn-sm"
              onClick={() => setOfflineReady(false)}
            >
              Dismiss
            </button>
          </div>
        </div>
      </div>
    );
  }

  return null;
}