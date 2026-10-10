import { useTheme } from '../hooks/useTheme';

/**
 * Dark/light switch. The accessible name states the ACTION ("Switch to dark
 * theme" / "Switch to light theme") and matches the `title` tooltip;
 * `aria-pressed` exposes the current dark state. The two glyphs swap purely in
 * CSS via the `dark:` variant (bound to our `data-theme`, see index.css): the
 * sun shows in dark mode, the moon in light mode.
 */
export function ThemeToggle() {
  const { isDark, toggle } = useTheme();
  const label = isDark ? 'Switch to light theme' : 'Switch to dark theme';

  return (
    <button
      type="button"
      className="btn btn-ghost btn-sm btn-circle"
      onClick={toggle}
      aria-label={label}
      title={label}
      aria-pressed={isDark}
    >
      {/* Sun — the "switch to light" affordance, shown only in dark mode. */}
      <svg
        className="hidden dark:block w-5 h-5 stroke-current"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth={2}
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <circle cx="12" cy="12" r="4" />
        <path d="M12 2v2" />
        <path d="M12 20v2" />
        <path d="m4.93 4.93 1.41 1.41" />
        <path d="m17.66 17.66 1.41 1.41" />
        <path d="M2 12h2" />
        <path d="M20 12h2" />
        <path d="m6.34 17.66-1.41 1.41" />
        <path d="m19.07 4.93-1.41 1.41" />
      </svg>

      {/* Moon — the "switch to dark" affordance, shown only in light mode. */}
      <svg
        className="block dark:hidden w-5 h-5 stroke-current"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth={2}
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79Z" />
      </svg>
    </button>
  );
}
