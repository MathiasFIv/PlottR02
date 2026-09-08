import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Button } from './Button';
import { FormField } from './FormField';
import { ApiError, api } from '../lib/api';
import type { CreateUserInput, User } from '../types';

type CreateUserModalProps = {
  open: boolean;
  onClose: () => void;
  /** Called with the created user after a successful POST /api/Users. */
  onCreated?: (user: User) => void;
};

/**
 * Modal dialog for creating a new user, opened from the login screen.
 * Posts to `POST /api/Users` (name + email + optional role + password, BCrypt
 * hashed server-side). Validation mirrors what the API enforces.
 */
export function CreateUserModal({
  open,
  onClose,
  onCreated,
}: CreateUserModalProps) {
  const dialogRef = useRef<HTMLDialogElement>(null);

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [role, setRole] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (open && !dialog.open) {
      setError(null);
      dialog.showModal();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  /** Empty and overlong passwords would be rejected by the API (BCrypt 72-byte cap). */
  function validatePassword(value: string): string | null {
    if (!value) return 'Password is required.';
    const byteLength = new TextEncoder().encode(value).length;
    if (byteLength > 72) return 'Password must be 72 bytes or fewer.';
    return null;
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (submitting) return;

    const trimmedName = name.trim();
    const trimmedEmail = email.trim();
    const passwordError = validatePassword(password);
    if (!trimmedName || !trimmedEmail || !password) {
      setError('Please fill in name, email and password.');
      return;
    }
    if (passwordError) {
      setError(passwordError);
      return;
    }

    setError(null);
    setSubmitting(true);
    try {
      const input: CreateUserInput = {
        name: trimmedName,
        email: trimmedEmail,
        role: role.trim() || null,
        password,
      };
      const created = await api.createUser(input);
      onCreated?.(created);
      reset();
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && err.message.includes('already exists')) {
        setError('A user with this email already exists.');
      } else {
        setError('Could not create the user. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  }

  function reset() {
    setName('');
    setEmail('');
    setRole('');
    setPassword('');
    setError(null);
  }

  function handleClose() {
    if (submitting) return;
    reset();
    onClose();
  }

  return (
    <dialog
      ref={dialogRef}
      onClose={handleClose}
      className="modal"
    >
      <div className="modal__panel">
        <h2 className="heading modal__title">Create account</h2>
        <p className="muted modal__subtitle">
          The password is stored hashed (BCrypt) server-side — plaintext is
          never persisted.
        </p>

        <form onSubmit={handleSubmit}>
          <FormField
            label="Name"
            htmlFor="create-name"
            inputProps={{
              value: name,
              onChange: (e) => setName(e.target.value),
              placeholder: 'Full name',
              autoComplete: 'name',
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
          <FormField
            label="Role (optional)"
            htmlFor="create-role"
            inputProps={{
              value: role,
              onChange: (e) => setRole(e.target.value),
              placeholder: 'e.g. Researcher, Trial Manager',
            }}
          />
          <FormField
            label="Password"
            htmlFor="create-password"
            inputProps={{
              value: password,
              onChange: (e) => setPassword(e.target.value),
              placeholder: 'Enter a password',
              type: 'password',
              autoComplete: 'new-password',
              required: true,
            }}
          />

          {error && (
            <p className="error-text" role="alert">
              {error}
            </p>
          )}

          <div className="modal__actions">
            <Button type="button" variant="secondary" onClick={handleClose}>
              Cancel
            </Button>
            <Button
              type="submit"
              disabled={!name.trim() || !email.trim() || !password || submitting}
            >
              {submitting ? 'Creating…' : 'Create account'}
            </Button>
          </div>
        </form>
      </div>
    </dialog>
  );
}
