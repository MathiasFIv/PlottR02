import { useState } from 'react';
import type { FormEvent } from 'react';
import { Button } from './Button';
import { FormField } from './FormField';
import { UserList } from './UserList';
import { useUsers } from '../hooks/useUsers';
import { assignableRoles, canCreateUsers, canManageUsers, roleLabel } from '../types';
import type { UserRole } from '../types';

type UserManagementSectionProps = {
  /** The signed-in actor's role; drives which roles may be created and whether the list is shown. */
  actorRole: UserRole | null;
};

/**
 * User-administration section of the dashboard. The create form (POST
 * /api/Users) is shown to any creator (SystemAdmin, Researcher, TrialManager)
 * and offers only the roles that actor may assign. The live user list
 * (GET /api/Users) is managers-only (SystemAdmin, TrialManager) — for
 * create-only actors the hook's initial list GET is suppressed.
 */
export function UserManagementSection({ actorRole }: UserManagementSectionProps) {
  const { users, loading, error, fetchUsers, createUser } = useUsers({
    fetchOnMount: canManageUsers(actorRole),
  });

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<UserRole | ''>('');
  const [password, setPassword] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [created, setCreated] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // Defensive: the dashboard also gates this mount.
  if (!canCreateUsers(actorRole)) {
    return null;
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setValidationError(null);

    const trimmed = name.trim();
    const trimmedEmail = email.trim();
    const passwordError = validatePassword(password);
    if (passwordError) {
      setValidationError(passwordError);
      return;
    }
    if (!role) {
      setValidationError('Role is required.');
      return;
    }
    if (!trimmed || !trimmedEmail || submitting) {
      return;
    }

    setSubmitting(true);
    const result = await createUser({
      name: trimmed,
      email: trimmedEmail,
      role,
      password,
    });
    setSubmitting(false);

    if (result) {
      setCreated(result.name);
      setName('');
      setEmail('');
      setRole('');
      setPassword('');
    }
  }

  /** Empty and overlong passwords would be rejected by the API (400). */
  function validatePassword(value: string): string | null {
    if (!value) {
      return 'Password is required.';
    }
    const byteLength = new TextEncoder().encode(value).length;
    if (byteLength > 72) {
      return 'Password must be 72 bytes or fewer.';
    }
    return null;
  }

  return (
    <>
      <section className="card">
        <h2 className="heading">Create a user</h2>
        <form onSubmit={handleSubmit}>
          <FormField
            label="Name"
            htmlFor="create-name"
            inputProps={{
              value: name,
              onChange: (e) => setName(e.target.value),
              placeholder: 'Full name',
              required: true,
            }}
          />
          <FormField
            label="Email"
            htmlFor="create-email"
            inputProps={{
              value: email,
              onChange: (e) => setEmail(e.target.value),
              placeholder: 'you@example.com',
              type: 'email',
              autoComplete: 'email',
              required: true,
            }}
          />
          <FormField label="Role" htmlFor="create-role">
            <select
              id="create-role"
              name="create-role"
              className="input"
              value={role}
              onChange={(e) => setRole(e.target.value as UserRole | '')}
              required
            >
              <option value="">Select a role…</option>
              {assignableRoles(actorRole).map((r) => (
                <option key={r} value={r}>
                  {roleLabel(r)}
                </option>
              ))}
            </select>
          </FormField>
          <FormField
            label="Password"
            htmlFor="create-password"
            inputProps={{
              value: password,
              onChange: (e) => setPassword(e.target.value),
              placeholder: 'Enter a password',
              type: 'password',
              autoComplete: 'new-password',
            }}
          />
          <Button
            type="submit"
            disabled={!name.trim() || !email.trim() || !role || !password || submitting}
          >
            {submitting ? 'Creating…' : 'Create user'}
          </Button>
        </form>

        {validationError && <p className="error-text">{validationError}</p>}
        {created && (
          <p className="success-text" role="status">
            Created user “{created}” — the list below has been refreshed.
            Password stored hashed (BCrypt) — plaintext is never persisted.
          </p>
        )}
        {error && <p className="error-text">{error}</p>}
      </section>

      {canManageUsers(actorRole) && (
        <section className="card">
          <div className="card__header">
            <h2 className="heading">Users</h2>
            <button
              type="button"
              className="link-button"
              onClick={() => void fetchUsers()}
            >
              Refresh
            </button>
          </div>
          <UserList users={users} loading={loading} error={error} />
        </section>
      )}
    </>
  );
}
