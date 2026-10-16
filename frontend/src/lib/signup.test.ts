import { describe, expect, it, vi } from 'vitest';
import { ApiError } from './api';
import {
  SIGNUP_ROLE,
  createAccountAndLogin,
  signupErrorMessage,
  signupPasswordError,
} from './signup';
import type { CurrentUser } from '../types';

// ---------------------------------------------------------------------------
// Fixtures / fakes — house style: hand-rolled, no mock library.
// ---------------------------------------------------------------------------

const createdUser: CurrentUser = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'Ada Lovelace',
  email: 'ada@example.com',
  role: 'SystemAdmin',
};

/** Builds a deps bundle of spies, each resolving like the real wiring would. */
function makeDeps() {
  return {
    createUser: vi.fn().mockResolvedValue(createdUser),
    loginRequest: vi.fn().mockResolvedValue(createdUser),
    login: vi.fn(),
    navigate: vi.fn(),
  };
}

const validInput = {
  name: '  Ada Lovelace  ',
  email: '  ada@example.com  ',
  password: 'correct-horse-battery',
};

// ---------------------------------------------------------------------------
// signupPasswordError — mirrors UserService.ValidatePassword
// ---------------------------------------------------------------------------

describe('signupPasswordError', () => {
  it('rejects an empty password', () => {
    expect(signupPasswordError('')).toBe('Password is required.');
  });

  it('accepts a normal password', () => {
    expect(signupPasswordError('hunter2')).toBeNull();
  });

  it('accepts a password of exactly 72 bytes', () => {
    expect(signupPasswordError('a'.repeat(72))).toBeNull();
  });

  it('rejects a password longer than 72 bytes', () => {
    expect(signupPasswordError('a'.repeat(73))).toBe(
      'Password must be 72 bytes or fewer.',
    );
  });
});

// ---------------------------------------------------------------------------
// signupErrorMessage — status mapping
// ---------------------------------------------------------------------------

describe('signupErrorMessage', () => {
  it('maps 409 to the duplicate-email message', () => {
    expect(signupErrorMessage(new ApiError(409, 'conflict'))).toBe(
      'An account with this email already exists.',
    );
  });

  it('maps 401 and 403 to the disabled message', () => {
    expect(signupErrorMessage(new ApiError(401, 'unauthorized'))).toBe(
      'Account creation is currently disabled.',
    );
    expect(signupErrorMessage(new ApiError(403, 'forbidden'))).toBe(
      'Account creation is currently disabled.',
    );
  });

  it('surfaces the server message for a 400 validation error', () => {
    const err = new ApiError(
      400,
      'API request failed (400): /api/Users — Password must be at least 8 characters.',
    );
    expect(signupErrorMessage(err)).toBe(
      'Password must be at least 8 characters.',
    );
  });

  it('falls back to a generic validation message for a prefixless 400', () => {
    expect(signupErrorMessage(new ApiError(400, 'Bad request'))).toBe(
      'Please check the details you entered and try again.',
    );
  });

  it('falls back for unknown errors', () => {
    expect(signupErrorMessage(new Error('boom'))).toBe(
      'Something went wrong. Please try again.',
    );
    expect(signupErrorMessage('not an error')).toBe(
      'Something went wrong. Please try again.',
    );
  });
});

// ---------------------------------------------------------------------------
// createAccountAndLogin — orchestration
// ---------------------------------------------------------------------------

describe('createAccountAndLogin', () => {
  it('creates the account, logs in, and lands on /record', async () => {
    const deps = makeDeps();

    const result = await createAccountAndLogin(validInput, deps);

    expect(result).toEqual({ ok: true });
    expect(deps.createUser).toHaveBeenCalledTimes(1);
    expect(deps.createUser).toHaveBeenCalledWith({
      name: 'Ada Lovelace',
      email: 'ada@example.com',
      role: SIGNUP_ROLE,
      password: 'correct-horse-battery',
    });
    expect(deps.loginRequest).toHaveBeenCalledTimes(1);
    expect(deps.loginRequest).toHaveBeenCalledWith(
      'ada@example.com',
      'correct-horse-battery',
    );
    expect(deps.login).toHaveBeenCalledWith(createdUser);
    expect(deps.navigate).toHaveBeenCalledWith('/record', { replace: true });
  });

  it('defaults the role to SystemAdmin (not user-selectable)', async () => {
    const deps = makeDeps();

    await createAccountAndLogin(validInput, deps);

    expect(deps.createUser.mock.calls[0][0].role).toBe('SystemAdmin');
  });

  it('does not call the API when a field is missing', async () => {
    const deps = makeDeps();

    const result = await createAccountAndLogin(
      { name: '   ', email: 'ada@example.com', password: 'secret' },
      deps,
    );

    expect(result).toEqual({
      ok: false,
      error: 'Please enter your name, email and password.',
    });
    expect(deps.createUser).not.toHaveBeenCalled();
  });

  it('does not call the API when the password is too long', async () => {
    const deps = makeDeps();

    const result = await createAccountAndLogin(
      { ...validInput, password: 'a'.repeat(73) },
      deps,
    );

    expect(result).toEqual({
      ok: false,
      error: 'Password must be 72 bytes or fewer.',
    });
    expect(deps.createUser).not.toHaveBeenCalled();
  });

  it('surfaces a duplicate-email failure and never logs in', async () => {
    const deps = makeDeps();
    deps.createUser.mockRejectedValue(new ApiError(409, 'email taken'));

    const result = await createAccountAndLogin(validInput, deps);

    expect(result).toEqual({
      ok: false,
      error: 'An account with this email already exists.',
    });
    expect(deps.loginRequest).not.toHaveBeenCalled();
    expect(deps.navigate).not.toHaveBeenCalled();
  });

  it('surfaces a disabled-signup failure (401) and never logs in', async () => {
    const deps = makeDeps();
    deps.createUser.mockRejectedValue(new ApiError(401, 'unauthorized'));

    const result = await createAccountAndLogin(validInput, deps);

    expect(result).toEqual({
      ok: false,
      error: 'Account creation is currently disabled.',
    });
    expect(deps.loginRequest).not.toHaveBeenCalled();
  });

  it('surfaces a disabled-signup failure (403) and never logs in', async () => {
    const deps = makeDeps();
    deps.createUser.mockRejectedValue(new ApiError(403, 'forbidden'));

    const result = await createAccountAndLogin(validInput, deps);

    expect(result).toEqual({
      ok: false,
      error: 'Account creation is currently disabled.',
    });
    expect(deps.loginRequest).not.toHaveBeenCalled();
    expect(deps.navigate).not.toHaveBeenCalled();
  });

  it('reports a sign-in failure after the account was created (401)', async () => {
    const deps = makeDeps();
    deps.loginRequest.mockRejectedValue(new ApiError(401, 'unauthorized'));

    const result = await createAccountAndLogin(validInput, deps);

    expect(result).toEqual({
      ok: false,
      error: 'Your account was created, but signing you in failed. Please log in.',
    });
    expect(result).not.toEqual({
      ok: false,
      error: 'Account creation is currently disabled.',
    });
    expect(deps.createUser).toHaveBeenCalledTimes(1);
    expect(deps.navigate).not.toHaveBeenCalled();
  });

  it('reports a sign-in failure after the account was created (403)', async () => {
    const deps = makeDeps();
    deps.loginRequest.mockRejectedValue(new ApiError(403, 'forbidden'));

    const result = await createAccountAndLogin(validInput, deps);

    expect(result).toEqual({
      ok: false,
      error: 'Your account was created, but signing you in failed. Please log in.',
    });
    expect(result).not.toEqual({
      ok: false,
      error: 'Account creation is currently disabled.',
    });
    expect(deps.createUser).toHaveBeenCalledTimes(1);
    expect(deps.navigate).not.toHaveBeenCalled();
  });
});
