import { useState } from 'react';
import type { FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import headerLight from '../Assets/plottR-header-light-small.png';
import headerDark from '../Assets/plottR-header-dark-small.png';
import { Button } from '../components/Button';
import { FormField } from '../components/FormField';
import { ApiError, api } from '../lib/api';
import { createAccountAndLogin } from '../lib/signup';
import { useAuth } from '../hooks/useAuth';

/**
 * Real login screen. Submits email + password to `POST /api/Auth/login`, which
 * verifies the credential pair against the stored BCrypt hash. On success the
 * returned user profile is stored as the current user; on 401 we show an
 * "invalid credentials" message.
 *
 * A "Create an account" affordance reveals a minimal self-signup panel so the
 * FIRST account can be created without an authenticated actor — the backend
 * temporarily allows anonymous `POST /api/Users` via a config flag. The login
 * flow itself is untouched; signup defaults the role to SystemAdmin (it is not
 * user-selectable here) and, on success, logs the new account straight in and
 * lands on `/record`, exactly like a normal login.
 */
export function LoginView() {
  const { login, loginRequest } = useAuth();
  const navigate = useNavigate();

  const [mode, setMode] = useState<'login' | 'signup'>('login');

  // Login form state.
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // Create-account form state.
  const [signupName, setSignupName] = useState('');
  const [signupEmail, setSignupEmail] = useState('');
  const [signupPassword, setSignupPassword] = useState('');
  const [signupError, setSignupError] = useState<string | null>(null);
  const [signupSubmitting, setSignupSubmitting] = useState(false);

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

  async function handleSignupSubmit(event: FormEvent) {
    event.preventDefault();
    if (signupSubmitting) return;

    setSignupError(null);
    setSignupSubmitting(true);
    const result = await createAccountAndLogin(
      { name: signupName, email: signupEmail, password: signupPassword },
      { createUser: api.createUser, loginRequest, login, navigate },
    );

    // On success we've navigated away, so leave the button disabled rather than
    // setting state on a view that is being torn down.
    if (!result.ok) {
      setSignupSubmitting(false);
      setSignupError(result.error);
    }
  }

  function showSignup() {
    setError(null);
    setMode('signup');
  }

  function showLogin() {
    setSignupError(null);
    setMode('login');
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

        {mode === 'login' ? (
          <>
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

            <div className="divider text-xs text-base-content/60 my-4">or</div>

            <button
              type="button"
              onClick={showSignup}
              className="btn btn-ghost btn-sm w-full"
            >
              Create an account
            </button>
          </>
        ) : (
          <form onSubmit={handleSignupSubmit} aria-labelledby="signup-title">
            <h2
              id="signup-title"
              className="text-lg font-semibold text-base-content mb-1"
            >
              Create account
            </h2>
            <p className="text-sm text-base-content/60 mb-4">
              Set up the first account to get started.
            </p>

            <FormField
              label="Name"
              htmlFor="signup-name"
              inputProps={{
                value: signupName,
                onChange: (e) => setSignupName(e.target.value),
                placeholder: 'Full name',
                autoComplete: 'name',
                required: true,
              }}
            />
            <FormField
              label="Email"
              htmlFor="signup-email"
              inputProps={{
                value: signupEmail,
                onChange: (e) => setSignupEmail(e.target.value),
                placeholder: 'you@example.com',
                type: 'email',
                autoComplete: 'email',
                required: true,
              }}
            />
            <FormField
              label="Password"
              htmlFor="signup-password"
              inputProps={{
                value: signupPassword,
                onChange: (e) => setSignupPassword(e.target.value),
                placeholder: 'Choose a password',
                type: 'password',
                autoComplete: 'new-password',
                required: true,
              }}
            />

            {signupError && (
              <p className="text-sm mt-3 text-error" role="alert">
                {signupError}
              </p>
            )}

            <Button
              type="submit"
              className="w-full"
              disabled={
                !signupName.trim() ||
                !signupEmail.trim() ||
                !signupPassword ||
                signupSubmitting
              }
            >
              {signupSubmitting ? 'Creating account…' : 'Create account'}
            </Button>

            <button
              type="button"
              onClick={showLogin}
              className="btn btn-ghost btn-sm w-full mt-2"
            >
              Back to log in
            </button>
          </form>
        )}
      </div>
    </div>
  );
}
