import { useAtom, useSetAtom } from 'jotai';
import { useCallback, useEffect } from 'react';
import { api } from '../lib/api';
import { errorAtom, loadingAtom, usersAtom } from '../store/atoms';
import type { CreateUserInput, User } from '../types';

/**
 * Data-access hook for the `/api/Users` resource. Wraps the fetch calls and
 * mirrors their state (list, loading, error) into Jotai atoms.
 *
 * Pass `{ fetchOnMount: false }` for actors who may create users but lack the
 * list permission (GET /api/Users is SystemAdmin + TrialManager only), so
 * mounting a create-only form never fires a 403-ing list request.
 */
export function useUsers(options?: { fetchOnMount?: boolean }) {
  const { fetchOnMount = true } = options ?? {};
  const [users, setUsers] = useAtom(usersAtom);
  const [loading, setLoading] = useAtom(loadingAtom);
  const [error, setError] = useAtom(errorAtom);
  const setUsersOnly = useSetAtom(usersAtom);

  const fetchUsers = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await api.getUsers();
      setUsers(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load users');
    } finally {
      setLoading(false);
    }
  }, [setLoading, setError, setUsers]);

  const createUser = useCallback(
    async (input: CreateUserInput): Promise<User | null> => {
      setLoading(true);
      setError(null);
      try {
        const created = await api.createUser(input);
        setUsersOnly((prev) => [...prev, created]);
        return created;
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to create user');
        return null;
      } finally {
        setLoading(false);
      }
    },
    [setLoading, setError, setUsersOnly],
  );

  // Load the user list once when the hook is first used, unless the caller is
  // a create-only actor (see `fetchOnMount`).
  useEffect(() => {
    if (!fetchOnMount) return;
    void fetchUsers();
  }, [fetchUsers, fetchOnMount]);

  return { users, loading, error, fetchUsers, createUser };
}