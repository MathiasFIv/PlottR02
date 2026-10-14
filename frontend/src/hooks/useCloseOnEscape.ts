import { useEffect } from 'react';

/**
 * Closes a dialog-style component when the user presses Escape.
 *
 * The app's modals are daisyUI `modal modal-open` `div`s, not `<dialog>`
 * elements, so the browser provides no built-in ESC handling — this restores
 * it. The listener re-binds cheaply if `onClose`'s identity changes; if
 * several modals were ever mounted at once, each would close, which is the
 * intended behaviour for overlapping overlays.
 */
export function useCloseOnEscape(onClose: () => void) {
  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);
}