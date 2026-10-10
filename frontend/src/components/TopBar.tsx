import { useAtomValue } from 'jotai';
import { Link } from 'react-router-dom';
import brandLight from '../Assets/plottR-header-light-small.png';
import brandDark from '../Assets/plottR-header-dark-small.png';
import { currentUserAtom } from '../store/atoms';
import { Button } from './Button';
import { ThemeToggle } from './ThemeToggle';

interface TopBarLink {
  to: string;
  label: string;
}

interface TopBarProps {
  links?: TopBarLink[];
  onLogout: () => void;
}

/**
 * Shared topbar. The same brand + identity + nav + logout header was
 * copy-pasted across DashboardView, RecordView and SheetView; this is the one
 * definition. The signed-in user is read from the atom internally — callers
 * only provide the nav links for their screen and the logout handler.
 * The brand logo links back to the dashboard (`/`), so screens other than
 * the dashboard don't need an explicit "Dashboard" nav link.
 */
export function TopBar({ links = [], onLogout }: TopBarProps) {
  const currentUser = useAtomValue(currentUserAtom);

  return (
    <header className="navbar bg-base-100 border-b border-base-300 px-6 py-3">
      <div className="navbar-start" />
      <div className="navbar-center">
        <Link
          to="/"
          aria-label="PlottR 0.2 — back to dashboard"
          className="block"
        >
          <img
            src={brandLight}
            alt="PlottR 0.2"
            className="block dark:hidden w-[min(352px,50vw)] h-auto"
          />
          <img
            src={brandDark}
            alt="PlottR 0.2"
            className="hidden dark:block w-[min(352px,50vw)] h-auto"
          />
        </Link>
      </div>
      <div className="navbar-end gap-4">
        {currentUser && (
          <span className="badge badge-ghost gap-1.5 text-sm font-normal max-[720px]:hidden">
            Signed in as <strong>{currentUser.name}</strong>
          </span>
        )}
        {links.map((l) => (
          <Link key={l.to} className="link link-primary text-sm font-medium" to={l.to}>
            {l.label}
          </Link>
        ))}
        <ThemeToggle />
        <Button variant="secondary" onClick={onLogout}>
          Log out
        </Button>
      </div>
    </header>
  );
}
