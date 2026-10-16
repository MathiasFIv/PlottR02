import { useAtomValue } from 'jotai';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import brandLight from '../Assets/plottR-header-light-small.png';
import brandDark from '../Assets/plottR-header-dark-small.png';
import { currentUserAtom } from '../store/atoms';
import { Button } from './Button';
import { CreateUserModal } from './CreateUserModal';
import { ManageUsersModal } from './ManageUsersModal';
import { ThemeToggle } from './ThemeToggle';
import { canCreateUsers, canManageUsers } from '../types';

interface TopBarLink {
  to: string;
  label: string;
}

interface TopBarProps {
  links?: TopBarLink[];
  onLogout: () => void;
}

/**
 * Shared topbar. The same brand + identity + nav + logout header is used
 * across DashboardView, RecordView and SheetView. The signed-in user is read
 * from the atom internally — callers only provide the nav links for their
 * screen and the logout handler. The brand logo links back to the dashboard
 * (`/`), so screens other than the dashboard don't need an explicit
 * "Dashboard" nav link.
 *
 * User management lives here now that the dashboard has no Users panel:
 * "Create user" (SystemAdmin, Researcher, TrialManager) opens the create
 * dialog; "Manage users" (SystemAdmin, TrialManager) opens the edit/delete
 * dialog. The dialogs render as siblings of the header, not children of it.
 *
 * RESPONSIVE COMPACTNESS — everything below `lg` (1024px) is treated as the
 * compact regime and every rule for it is gated behind `max-lg:` / `max-md:`
 * so the desktop layout (>=1024px) renders exactly as before. On tablets we
 * keep the management button LABELS but shrink them (`btn-xs`) and hide the
 * "Signed in as" badge; below `md` (768px) the two management buttons become
 * icon-only squares so the whole bar fits down to ~360px without wrapping or
 * overlapping the logo. Icon-only buttons keep their accessible name via
 * `aria-label`/`title`.
 */
export function TopBar({ links = [], onLogout }: TopBarProps) {
  const currentUser = useAtomValue(currentUserAtom);
  const role = currentUser?.role ?? null;

  const [createOpen, setCreateOpen] = useState(false);
  const [manageOpen, setManageOpen] = useState(false);

  return (
    <>
      <header className="navbar bg-base-100 border-b border-base-300 px-6 py-3 max-lg:px-4">
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
              className="block dark:hidden w-[min(352px,50vw)] max-lg:w-[min(200px,40vw)] h-auto"
            />
            <img
              src={brandDark}
              alt="PlottR 0.2"
              className="hidden dark:block w-[min(352px,50vw)] max-lg:w-[min(200px,40vw)] h-auto"
            />
          </Link>
        </div>
        <div className="navbar-end gap-4 max-lg:gap-1.5">
          {currentUser && (
            <span className="badge badge-ghost gap-1.5 text-sm font-normal max-lg:hidden">
              Signed in as <strong>{currentUser.name}</strong>
            </span>
          )}
          {canCreateUsers(role) && (
            <Button
              className="btn-sm max-lg:btn-xs max-md:btn-square"
              type="button"
              aria-label="Create user"
              title="Create user"
              onClick={() => setCreateOpen(true)}
            >
              <svg
                className="hidden max-md:block w-4 h-4 stroke-current"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth={2}
                strokeLinecap="round"
                strokeLinejoin="round"
                aria-hidden="true"
              >
                <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
                <circle cx="9" cy="7" r="4" />
                <line x1="19" y1="8" x2="19" y2="14" />
                <line x1="22" y1="11" x2="16" y2="11" />
              </svg>
              <span className="max-md:hidden">Create user</span>
            </Button>
          )}
          {canManageUsers(role) && (
            <Button
              variant="secondary"
              className="btn-sm max-lg:btn-xs max-md:btn-square"
              type="button"
              aria-label="Manage users"
              title="Manage users"
              onClick={() => setManageOpen(true)}
            >
              <svg
                className="hidden max-md:block w-4 h-4 stroke-current"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth={2}
                strokeLinecap="round"
                strokeLinejoin="round"
                aria-hidden="true"
              >
                <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
                <circle cx="9" cy="7" r="4" />
                <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
                <path d="M16 3.13a4 4 0 0 1 0 7.75" />
              </svg>
              <span className="max-md:hidden">Manage users</span>
            </Button>
          )}
          {links.map((l) => (
            <Link
              key={l.to}
              className="link link-primary text-sm font-medium"
              to={l.to}
            >
              {l.label}
            </Link>
          ))}
          <ThemeToggle />
          <Button
            variant="secondary"
            className="max-lg:btn-xs"
            onClick={onLogout}
          >
            Log out
          </Button>
        </div>
      </header>

      {createOpen && (
        <CreateUserModal
          actorRole={role}
          onClose={() => setCreateOpen(false)}
          onCreated={() => setCreateOpen(false)}
        />
      )}
      {manageOpen && (
        <ManageUsersModal
          actorRole={role}
          onClose={() => setManageOpen(false)}
        />
      )}
    </>
  );
}