import { useSetAtom } from 'jotai';
import { useCallback } from 'react';
import { api } from '../lib/api';
import { tokens } from '../lib/tokens';
import { currentUserAtom } from '../store/atoms';
import type { CurrentUser } from '../types';

/**
 * Auth hook backed by the real `POST /api/Auth/login` endpoint. The API
 * verifies the email/password against the stored BCrypt hash and returns the
 * authenticated user profile together with JWT access and refresh tokens.
 */
export function useAuth() {
  const setCurrentUser = useSetAtom(currentUserAtom);

  const login = useCallback(
    (user: CurrentUser) => {
      setCurrentUser(user);
    },
    [setCurrentUser],
  );

  /** Calls the login endpoint, persists tokens, and returns the verified user. */
  const loginRequest = useCallback(
    async (email: string, password: string): Promise<CurrentUser> => {
      const response = await api.login({ email, password });
      tokens.set(response.accessToken, response.refreshToken);
      return response.user;
    },
    [],
  );

  /**
   * Revokes refresh tokens on the server (fire-and-forget), clears persisted
   * tokens from localStorage, and resets the in-memory user atom.
   */
  const logout = useCallback(() => {
    void api.logout().catch(() => {});
    tokens.clear();
    setCurrentUser(null);
  }, [setCurrentUser]);

  return { login, loginRequest, logout };
}
