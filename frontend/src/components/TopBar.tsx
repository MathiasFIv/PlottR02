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
 */
export function TopBar({ links = [], onLogout }: TopBarProps) {
  const currentUser = useAtomValue(currentUserAtom);
  const role = currentUser?.role ?? null;

  const [createOpen, setCreateOpen] = useState(false);
  const [manageOpen, setManageOpen] = useState(false);

  return (
    <>
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
          {canCreateUsers(role) && (
            <Button
              className="btn-sm"
              type="button"
              onClick={() => setCreateOpen(true)}
            >
              Create user
            </Button>
          )}
          {canManageUsers(role) && (
            <Button
              variant="secondary"
              className="btn-sm"
              type="button"
              onClick={() => setManageOpen(true)}
            >
              Manage users
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
          <Button variant="secondary" onClick={onLogout}>
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