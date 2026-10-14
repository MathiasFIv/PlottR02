/**
 * Theme infrastructure for the PlottR dark/light switch.
 *
 * The active theme is one of two ids owned by the daisyUI theme blocks in
 * `src/index.css`: `plottr-light` (default) and `plottr-dark`. The selected id
 * is written to the `<html data-theme>` attribute, to the document's
 * `color-scheme` (so native UI — scrollbars, form controls, the page canvas —
 * matches) and to `<meta name="theme-color">` (mobile browser chrome).
 *
 * Persistence is a single localStorage key (`THEME_STORAGE_KEY`); the same key
 * is read by the classic anti-FOUC inline script in `index.html` <head> so the
 * correct theme paints before the module bundle boots. Keep the two in sync.
 *
 * Node-safety: this module is imported by `theme.test.ts` (plain Node, no DOM)
 * and by `main.tsx` at boot. Every DOM / localStorage access therefore happens
 * INSIDE a function behind a `typeof window` / `typeof document` guard, and
 * every storage access is wrapped so a throwing storage (private browsing,
 * disabled localStorage) degrades silently — nothing here throws, ever.
 */

/** The two daisyUI theme ids this app owns. */
export type Theme = 'plottr-light' | 'plottr-dark';

/**
 * localStorage key for the persisted theme choice. Mirrored verbatim by the
 * anti-FOUC inline script in `index.html` (which cannot import this module).
 */
export const THEME_STORAGE_KEY = 'plottR-theme';

/** `<meta name="theme-color">` value per theme — the mobile browser chrome tint. */
const THEME_COLORS: Record<Theme, string> = {
  'plottr-light': '#059669',
  'plottr-dark': '#1f2937',
};

/** Type guard: true only for the two known theme ids (rejects stored garbage). */
export function isTheme(value: unknown): value is Theme {
  return value === 'plottr-light' || value === 'plottr-dark';
}

/** The OS preference mapped to a theme id; light when matchMedia is unavailable. */
export function systemTheme(): Theme {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return 'plottr-light';
  }
  try {
    return window.matchMedia('(prefers-color-scheme: dark)').matches
      ? 'plottr-dark'
      : 'plottr-light';
  } catch {
    return 'plottr-light';
  }
}

/** The persisted theme, or null when absent / invalid / storage throws. */
export function readStoredTheme(): Theme | null {
  try {
    if (typeof window === 'undefined' || window.localStorage == null) return null;
    const raw = window.localStorage.getItem(THEME_STORAGE_KEY);
    return isTheme(raw) ? raw : null;
  } catch {
    return null;
  }
}

/** The theme to boot with: the stored choice, else the OS preference. */
export function resolveInitialTheme(): Theme {
  return readStoredTheme() ?? systemTheme();
}

/** Persists the theme choice; swallows storage failures (private mode). */
export function writeStoredTheme(theme: Theme): void {
  try {
    if (typeof window === 'undefined' || window.localStorage == null) return;
    window.localStorage.setItem(THEME_STORAGE_KEY, theme);
  } catch {
    // Storage full/unavailable — the choice simply won't survive a reload.
  }
}

/** Flips a theme to its counterpart. */
export function toggleTheme(theme: Theme): Theme {
  return theme === 'plottr-dark' ? 'plottr-light' : 'plottr-dark';
}

/**
 * Applies a theme to the document: `data-theme` (drives daisyUI + the `dark:`
 * variant), `color-scheme` and the `theme-color` meta. Guarded and wrapped, so
 * it is a no-op in Node and never throws against an exotic DOM stub.
 */
export function applyTheme(theme: Theme): void {
  if (typeof document === 'undefined') return;
  try {
    const root = document.documentElement;
    root.dataset.theme = theme;
    root.style.colorScheme = theme === 'plottr-dark' ? 'dark' : 'light';
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta !== null) {
      meta.setAttribute('content', THEME_COLORS[theme]);
    }
  } catch {
    // DOM access can throw on stub/exotic documents — never break boot.
  }
}
