import { useState } from 'react';
import type { FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button } from '../components/Button';
import { CreateUserModal } from '../components/CreateUserModal';
import { FormField } from '../components/FormField';
import { ApiError } from '../lib/api';
import { useAuth } from '../hooks/useAuth';

/**
 * Real login screen. Submits email + password to `POST /api/Auth/login`, which
 * verifies the credential pair against the stored BCrypt hash. On success the
 * returned user profile is stored as the current user; on 401 we show an
 * "invalid credentials" message. A "Create account" button opens a modal that
 * registers a new user via `POST /api/Users`.
 */
export function LoginView() {
  const { login, loginRequest } = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (submitting) return;

    // Basic client-side sanity before hitting the API.
    if (!email.trim() || !password) {
      setError('Please enter both email and password.');
      return;
    }

    setError(null);
    setSuccess(null);
    setSubmitting(true);
    try {
      const user = await loginRequest(email.trim(), password);
      login(user);
      navigate('/', { replace: true });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        setError('Invalid email or password.');
      } else {
        setError('Something went wrong. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="auth-shell">
      <div className="card auth-card">
        <h1 className="title">PlottR 0.2</h1>
        <p className="muted auth-subtitle">
          Agricultural trial management
        </p>

        <form onSubmit={handleSubmit}>
          <FormField
            label="Email"
            htmlFor="login-email"
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
            label="Password"
            htmlFor="login-password"
            inputProps={{
              value: password,
              onChange: (e) => setPassword(e.target.value),
              placeholder: 'Enter your password',
              type: 'password',
              autoComplete: 'current-password',
              required: true,
            }}
          />

          {error && (
            <p className="error-text" role="alert">
              {error}
            </p>
          )}

          {success && (
            <p className="success-text" role="status">
              {success}
            </p>
          )}

          <Button
            type="submit"
            className="w-full"
            disabled={!email.trim() || !password || submitting}
          >
            {submitting ? 'Signing in…' : 'Log in'}
          </Button>
        </form>

        <p className="auth-divider">No account yet?</p>
        <Button
          type="button"
          variant="secondary"
          className="w-full"
          onClick={() => setCreateOpen(true)}
        >
          Create account
        </Button>
      </div>

      <CreateUserModal
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        onCreated={(user) => {
          // Pre-fill the email so the new user only has to type their password.
          setEmail(user.email);
          setSuccess('Account created. Sign in with your new password.');
        }}
      />
    </div>
  );
}
