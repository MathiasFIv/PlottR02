import { useState } from 'react';
import { Button } from './Button';
import { CreateUserModal } from './CreateUserModal';
import { ManageUsersModal } from './ManageUsersModal';
import { UserList } from './UserList';
import { useUsers } from '../hooks/useUsers';
import { canCreateUsers, canManageUsers } from '../types';
import type { UserRole } from '../types';

type UsersPanelProps = {
  /** The signed-in actor's role; drives create/manage gating and the role list. */
  actorRole: UserRole | null;
  /** Extra classes — e.g. grid placement and equal panel height. */
  className?: string;
};

/**
 * Consolidated users panel of the dashboard. Shown to any account creator
 * (SystemAdmin, Researcher, TrialManager). The live list (GET /api/Users) is
 * managers-only (SystemAdmin, TrialManager) — create-only actors (Researcher)
 * see a hint instead and never trigger the list GET (`fetchOnMount`). Below
 * the list sit the two entry points: "Create user" (any creator) and "Manage
 * users" (managers only), each opening its own daisyUI dialog.
 */
export function UsersPanel({ actorRole, className = '' }: UsersPanelProps) {
  const manage = canManageUsers(actorRole);
  const { users, loading, error, fetchUsers } = useUsers({
    fetchOnMount: manage,
  });

  const [createOpen, setCreateOpen] = useState(false);
  const [manageOpen, setManageOpen] = useState(false);
  const [createdName, setCreatedName] = useState<string | null>(null);

  // Defensive: the dashboard also gates this mount.
  if (!canCreateUsers(actorRole)) {
    return null;
  }

  return (
    <section
      className={`card card-border bg-base-100 shadow-sm p-6 flex flex-col ${className}`}
    >
      <h2 className="text-lg font-semibold text-base-content mb-3">Users</h2>

      {manage ? (
        <div className="flex-1 min-h-0 overflow-y-auto lg:overscroll-contain lg:pr-1">
          <UserList users={users} loading={loading} error={error} />
        </div>
      ) : (
        <p className="text-sm text-base-content/70">
          Only system admins and trial managers can manage users.
        </p>
      )}

      {createdName && (
        <p className="text-sm mt-3 text-success" role="status">
          Created user “{createdName}”.
        </p>
      )}

      <div className="flex flex-wrap gap-3 mt-4">
        <Button
          type="button"
          onClick={() => {
            setCreatedName(null);
            setCreateOpen(true);
          }}
        >
          Create user
        </Button>
        {manage && (
          <Button
            variant="secondary"
            type="button"
            onClick={() => {
              setCreatedName(null);
              setManageOpen(true);
            }}
          >
            Manage users
          </Button>
        )}
      </div>

      {createOpen && (
        <CreateUserModal
          actorRole={actorRole}
          onClose={() => setCreateOpen(false)}
          onCreated={(name) => {
            setCreatedName(name);
            setCreateOpen(false);
            // The atom append in createUser keeps a manager's list correct; a
            // create-only actor has no list to refresh (matches fetchOnMount).
            if (manage) void fetchUsers();
          }}
        />
      )}

      {manageOpen && (
        <ManageUsersModal
          actorRole={actorRole}
          onClose={() => setManageOpen(false)}
        />
      )}
    </section>
  );
}
