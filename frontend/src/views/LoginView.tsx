import { useState } from 'react';
import type { FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import headerLight from '../Assets/plottR-header-light-small.png';
import headerDark from '../Assets/plottR-header-dark-small.png';
import { Button } from '../components/Button';
import { FormField } from '../components/FormField';
import { ApiError } from '../lib/api';
import { useAuth } from '../hooks/useAuth';

/**
 * Real login screen. Submits email + password to `POST /api/Auth/login`, which
 * verifies the credential pair against the stored BCrypt hash. On success the
 * returned user profile is stored as the current user; on 401 we show an
 * "invalid credentials" message. Account creation is no longer anonymous and
 * happens only on the dashboard, for roles allowed by the permission matrix.
 */
export function LoginView() {
  const { login, loginRequest } = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (submitting) return;

    // Basic client-side sanity before hitting the API.
    if (!email.trim() || !password) {
      setError('Please enter both email and password.');
      return;
    }

    setError(null);
    setSubmitting(true);
    try {
      const user = await loginRequest(email.trim(), password);
      login(user);
      navigate('/record', { replace: true });
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
    <div className="min-h-screen flex items-center justify-center p-6">
      <div className="card card-border bg-base-100 shadow-sm p-6 w-full max-w-[400px]">
        {/* Visually hidden so screen readers still get a level-1 heading (the
            header is an image with alt="PlottR 0.2"). */}
        <h1 className="sr-only">PlottR 0.2</h1>
        <img
          src={headerLight}
          alt="PlottR 0.2"
          className="block dark:hidden w-full h-auto mb-6"
        />
        <img
          src={headerDark}
          alt="PlottR 0.2"
          className="hidden dark:block w-full h-auto mb-6"
        />

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
            <p className="text-sm mt-3 text-error" role="alert">
              {error}
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
      </div>
    </div>
  );
}
