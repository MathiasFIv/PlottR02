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
    return <p className="muted">Loading users…</p>;
  }

  if (error) {
    return <p className="error-text">Error: {error}</p>;
  }

  if (users.length === 0) {
    return <p className="muted">No users found yet.</p>;
  }

  return (
    <ul className="user-list">
      {users.map((user) => (
        <li key={user.id} className="user-list__item">
          <div>
            <p className="user-list__name">{user.name}</p>
            {user.email && <p className="muted user-list__email">{user.email}</p>}
            {user.role && (
              <p className="muted user-list__role">{roleLabel(user.role)}</p>
            )}
          </div>
          <code className="muted user-list__id">{user.id}</code>
        </li>
      ))}
    </ul>
  );
}