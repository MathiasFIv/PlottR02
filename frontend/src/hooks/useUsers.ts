import { useAtom, useSetAtom } from 'jotai';
import { useCallback, useEffect } from 'react';
import { api } from '../lib/api';
import { errorAtom, loadingAtom, usersAtom } from '../store/atoms';
import type { CreateUserInput, User } from '../types';

/**
 * Data-access hook for the `/api/Users` resource. Wraps the fetch calls and
 * mirrors their state (list, loading, error) into Jotai atoms.
 */
export function useUsers() {
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

  // Load the user list once when the hook is first used.
  useEffect(() => {
    void fetchUsers();
  }, [fetchUsers]);

  return { users, loading, error, fetchUsers, createUser };
}