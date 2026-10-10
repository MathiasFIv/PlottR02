import type { User } from '../types';
import { roleLabel } from '../types';

type UserListProps = {
  users: User[];
  loading: boolean;
  error: string | null;
};

/** Presentational list of users fetched from GET /api/Users. */
export function UserList({ users, loading, error }: UserListProps) {
  if (loading && users.length === 0) {
    return <p className="text-sm text-base-content/70">Loading users…</p>;
  }

  if (error) {
    return <p className="text-sm mt-3 text-error">Error: {error}</p>;
  }

  if (users.length === 0) {
    return <p className="text-sm text-base-content/70">No users found yet.</p>;
  }

  return (
    <ul className="list">
      {users.map((user) => (
        <li key={user.id} className="list-row">
          <div>
            <p className="font-medium text-base-content">{user.name}</p>
            {user.email && (
              <p className="text-sm text-base-content/70">{user.email}</p>
            )}
            {user.role && (
              <p className="text-sm text-base-content/70">
                {roleLabel(user.role)}
              </p>
            )}
          </div>
        </li>
      ))}
    </ul>
  );
}