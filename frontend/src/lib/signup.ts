import { ApiError } from './api';
import type { CreateUserInput, CurrentUser, UserRole } from '../types';

/**
 * Self-service account creation from the login page.
 *
 * The public signup affordance is deliberately narrow: the login page creates
 * the FIRST account only, so the role is fixed (never user-selectable) and the
 * authenticated permission matrix of {@link CreateUserModal} is bypassed by the
 * backend's temporary anonymous-signup config flag. This module holds the pure
 * orchestration + error mapping so it can be unit-tested without a DOM, exactly
 * like the other `lib` helpers (the project's Vitest setup is node-only).
 */

/**
 * Role granted to the first self-created account. Hard-coded because the login
 * page must not let an anonymous caller choose their own role.
 */
export const SIGNUP_ROLE: UserRole = 'SystemAdmin';

/**
 * BCrypt only uses the first 72 bytes of input, so the API rejects longer
 * passwords (mirrors `UserService.BcryptMaxPasswordBytes`). There is no minimum
 * length beyond non-empty on the backend, so none is invented here.
 */
const MAX_PASSWORD_BYTES = 72;

/** Whether the account flow landed the user on the post-login screen. */
export type SignupResult = { ok: true } | { ok: false; error: string };

/** Everything `createAccountAndLogin` needs, injected so tests need no DOM. */
export type SignupDeps = {
  createUser: (input: CreateUserInput) => Promise<unknown>;
  loginRequest: (email: string, password: string) => Promise<CurrentUser>;
  login: (user: CurrentUser) => void;
  navigate: (to: string, options: { replace: boolean }) => void;
};

/**
 * Client-side mirror of the backend password rules: required and at most 72
 * UTF-8 bytes. Returns `null` when the value is acceptable.
 */
export function signupPasswordError(password: string): string | null {
  if (!password) {
    return 'Password is required.';
  }
  if (new TextEncoder().encode(password).length > MAX_PASSWORD_BYTES) {
    return 'Password must be 72 bytes or fewer.';
  }
  return null;
}

/**
 * The API's own message from an `ApiError`, stripped of the client's transport
 * prefix ("API request failed (400): /path — …"). Returns `null` when absent.
 */
function serverMessage(err: ApiError): string | null {
  const marker = ' — ';
  const index = err.message.indexOf(marker);
  if (index < 0) return null;
  const message = err.message.slice(index + marker.length).trim();
  return message || null;
}

/**
 * Maps a **create-account** (`POST /api/Users`) failure to the message shown on
 * the login page. Deliberately scoped to phase 1 of {@link createAccountAndLogin}
 * and never called for the follow-up login, so a 401/403 can only ever mean
 * "anonymous signup disabled" — it can never be misattributed to a sign-in
 * failure (which is reported by the caller with its own message).
 *
 * - 409 duplicate email
 * - 400 validation (surfaces the server's own rule, e.g. a password minimum)
 * - 401/403 anonymous signup disabled (endpoint still `[Authorize]`)
 */
export function signupErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 409) {
      return 'An account with this email already exists.';
    }
    if (err.status === 400) {
      return (
        serverMessage(err) ??
        'Please check the details you entered and try again.'
      );
    }
    if (err.status === 401 || err.status === 403) {
      return 'Account creation is currently disabled.';
    }
  }
  return 'Something went wrong. Please try again.';
}

/** Shown when the account exists but the follow-up sign-in did not succeed. */
const SIGN_IN_FAILED_MESSAGE =
  'Your account was created, but signing you in failed. Please log in.';

/**
 * Creates the account then signs in with the same credentials, navigating to
 * the identical post-login destination as the normal form. Validation and any
 * failure are returned as a message (never thrown) so the view stays thin.
 */
export async function createAccountAndLogin(
  input: { name: string; email: string; password: string },
  deps: SignupDeps,
): Promise<SignupResult> {
  const name = input.name.trim();
  const email = input.email.trim();
  const password = input.password;

  if (!name || !email || !password) {
    return { ok: false, error: 'Please enter your name, email and password.' };
  }
  const passwordError = signupPasswordError(password);
  if (passwordError) {
    return { ok: false, error: passwordError };
  }

  // Phase 1 — create the account. This is the ONLY place `signupErrorMessage`
  // runs, so the 401/403 "account creation is currently disabled" mapping is
  // structurally confined to a pre-creation failure and can never be produced
  // by the follow-up login below.
  try {
    await deps.createUser({ name, email, role: SIGNUP_ROLE, password });
  } catch (err) {
    return { ok: false, error: signupErrorMessage(err) };
  }

  // Phase 2 — sign in with the same credentials. A failure here means the
  // account already exists, so it gets its own message and never the disabled
  // mapping.
  let user: CurrentUser;
  try {
    user = await deps.loginRequest(email, password);
  } catch {
    return { ok: false, error: SIGN_IN_FAILED_MESSAGE };
  }

  deps.login(user);
  deps.navigate('/record', { replace: true });
  return { ok: true };
}
