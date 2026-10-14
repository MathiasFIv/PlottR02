import { useAtom } from 'jotai';
import { useCallback } from 'react';
import { applyTheme, toggleTheme, writeStoredTheme } from '../lib/theme';
import type { Theme } from '../lib/theme';
import { themeAtom } from '../store/atoms';

/**
 * Reads and controls the active theme.
 *
 * The side effects (apply to the DOM + persist to localStorage) run in the
 * event handlers, NOT in an effect — so the switch is StrictMode-safe (no
 * double-invoked effect writing twice) and the change lands synchronously
 * before the next paint. The atom is the single source of truth for React;
 * `main.tsx` seeds it with `resolveInitialTheme()` to match the anti-FOUC
 * inline script.
 */
export function useTheme(): {
  theme: Theme;
  isDark: boolean;
  setTheme: (theme: Theme) => void;
  toggle: () => void;
} {
  const [theme, setThemeAtom] = useAtom(themeAtom);

  const setTheme = useCallback(
    (next: Theme) => {
      setThemeAtom(next);
      applyTheme(next);
      writeStoredTheme(next);
    },
    [setThemeAtom],
  );

  const toggle = useCallback(() => {
    setTheme(toggleTheme(theme));
  }, [theme, setTheme]);

  return { theme, isDark: theme === 'plottr-dark', setTheme, toggle };
}
