import { useAtom, useSetAtom } from 'jotai';
import { useCallback, useEffect, useState } from 'react';
import { api } from '../lib/api';
import { errorAtom, loadingAtom, usersAtom } from '../store/atoms';
import type { CreateUserInput, UpdateUserInput, User } from '../types';

/**
 * Data-access hook for the `/api/Users` resource. Wraps the fetch calls and
 * mirrors their state (list, loading, error) into Jotai atoms.
 *
 * Pass `{ fetchOnMount: false }` for actors who may create users but lack the
 * list permission (GET /api/Users is SystemAdmin + TrialManager only), so
 * mounting a create-only form never fires a 403-ing list request.
 *
 * Error scoping: `errorAtom` (the shared `error` here) is reserved for the
 * LIST fetch — the panel and both dialogs read the same list state. Mutation
 * failures (create/update/delete) land in `operationError`, which is LOCAL
 * `useState` to this hook instance, so each dialog owns its own result and a
 * failed edit/delete can never leak "Error: …" into the list behind the
 * dialog (each call site mounts its own hook instance).
 */
export function useUsers(options?: { fetchOnMount?: boolean }) {
  const { fetchOnMount = true } = options ?? {};
  const [users, setUsers] = useAtom(usersAtom);
  const [loading, setLoading] = useAtom(loadingAtom);
  const [error, setError] = useAtom(errorAtom);
  const setUsersOnly = useSetAtom(usersAtom);
  const [operationError, setOperationError] = useState<string | null>(null);

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
      setOperationError(null);
      setLoading(true);
      try {
        const created = await api.createUser(input);
        setUsersOnly((prev) => [...prev, created]);
        return created;
      } catch (err) {
        setOperationError(err instanceof Error ? err.message : 'Failed to create user');
        return null;
      } finally {
        setLoading(false);
      }
    },
    [setLoading, setUsersOnly],
  );

  /**
   * Edits a user's profile (name/email/role) via PUT. On success the matching
   * entry in `usersAtom` is replaced in place so the list rerenders without a
   * round-trip; returns the updated user, or null on failure (message in the
   * local `operationError`).
   */
  const updateUser = useCallback(
    async (id: string, input: UpdateUserInput): Promise<User | null> => {
      setOperationError(null);
      setLoading(true);
      try {
        const updated = await api.updateUser(id, input);
        setUsersOnly((prev) =>
          prev.map((user) => (user.id === id ? updated : user)),
        );
        return updated;
      } catch (err) {
        setOperationError(err instanceof Error ? err.message : 'Failed to update user');
        return null;
      } finally {
        setLoading(false);
      }
    },
    [setLoading, setUsersOnly],
  );

  /**
   * Deletes a user via DELETE (204). On success the entry is filtered out of
   * `usersAtom`; returns true, or false on failure (message in the local
   * `operationError`).
   */
  const deleteUser = useCallback(
    async (id: string): Promise<boolean> => {
      setOperationError(null);
      setLoading(true);
      try {
        await api.deleteUser(id);
        setUsersOnly((prev) => prev.filter((user) => user.id !== id));
        return true;
      } catch (err) {
        setOperationError(err instanceof Error ? err.message : 'Failed to delete user');
        return false;
      } finally {
        setLoading(false);
      }
    },
    [setLoading, setUsersOnly],
  );

  // Load the user list once when the hook is first used, unless the caller is
  // a create-only actor (see `fetchOnMount`).
  useEffect(() => {
    if (!fetchOnMount) return;
    void fetchUsers();
  }, [fetchUsers, fetchOnMount]);

  return {
    users,
    loading,
    error,
    operationError,
    fetchUsers,
    createUser,
    updateUser,
    deleteUser,
  };
}