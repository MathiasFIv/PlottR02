import { useState } from 'react';
import type { FormEvent } from 'react';
import { Button } from './Button';
import { FormField } from './FormField';
import { useUsers } from '../hooks/useUsers';
import { useCloseOnEscape } from '../hooks/useCloseOnEscape';
import { assignableRoles, roleLabel } from '../types';
import type { UserRole } from '../types';

type CreateUserModalProps = {
  /** The signed-in actor's role; decides which roles may be assigned. */
  actorRole: UserRole | null;
  onClose: () => void;
  /** Called with the created user's name after a successful POST. */
  onCreated: (name: string) => void;
};

/**
 * Create-account dialog (POST /api/Users). Hosts the validated create form —
 * name/email/role (limited to the actor's `assignableRoles`) and a password
 * bounded to 72 bytes so an overlong value never reaches the API's 400. On
 * success it hands the new user's name back to the panel, which closes the
 * dialog and refreshes the list.
 */
export function CreateUserModal({
  actorRole,
  onClose,
  onCreated,
}: CreateUserModalProps) {
  const { operationError, createUser } = useUsers({ fetchOnMount: false });

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<UserRole | ''>('');
  const [password, setPassword] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [attempted, setAttempted] = useState(false);

  useCloseOnEscape(onClose);

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
    setAttempted(true);
    const result = await createUser({
      name: trimmed,
      email: trimmedEmail,
      role,
      password,
    });
    setSubmitting(false);

    if (result) {
      onCreated(result.name);
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
    <div className="modal modal-open">
      <div
        className="modal-box max-w-[440px]"
        role="dialog"
        aria-modal="true"
        aria-labelledby="create-user-title"
      >
        <h2
          id="create-user-title"
          className="text-lg font-semibold text-base-content mb-3"
        >
          Create a user
        </h2>
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
              className="select w-full"
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

          {validationError && (
            <p className="text-sm mt-3 text-error">{validationError}</p>
          )}
          {attempted && operationError && (
            <p className="text-sm mt-3 text-error">{operationError}</p>
          )}

          <div className="flex justify-end gap-3 mt-5">
            <Button
              variant="secondary"
              type="button"
              onClick={onClose}
              disabled={submitting}
            >
              Cancel
            </Button>
            <Button
              type="submit"
              disabled={
                !name.trim() || !email.trim() || !role || !password || submitting
              }
            >
              {submitting ? 'Creating…' : 'Create user'}
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}
