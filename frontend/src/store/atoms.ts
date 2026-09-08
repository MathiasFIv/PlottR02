import { atom } from 'jotai';
import type { CurrentUser, User } from '../types';
import { tokens } from '../lib/tokens';

/** The authenticated user profile (populated on login or session restore). */
export const currentUserAtom = atom<CurrentUser | null>(null);

/**
 * Derived atom: true when a persisted access token exists in localStorage.
 * Guards use this instead of currentUserAtom so the login page doesn't flash
 * on refresh while the session is being restored.
 */
export const isAuthenticatedAtom = atom<boolean>(
  () => tokens.getAccessToken() !== null,
);

/** All users currently loaded from GET /api/Users. */
export const usersAtom = atom<User[]>([]);

/** Whether a users API request is in flight. */
export const loadingAtom = atom<boolean>(false);

/** Last error message from a users API request, if any. */
export const errorAtom = atom<string | null>(null);