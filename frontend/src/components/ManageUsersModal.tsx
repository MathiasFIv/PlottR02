import { useState } from 'react';
import type { FormEvent } from 'react';
import { Button } from './Button';
import { FormField } from './FormField';
import { useUsers } from '../hooks/useUsers';
import { editableRolesFor } from '../lib/userRoles';
import { roleLabel } from '../types';
import type { User, UserRole } from '../types';

type ManageUsersModalProps = {
  /** The signed-in actor's role; decides which roles the edit form offers. */
  actorRole: UserRole | null;
  onClose: () => void;
};

/**
 * Manager-only "Manage users" dialog. Reads the already-fetched list from
 * `usersAtom` (via `useUsers` with `fetchOnMount: false` — the dashboard panel
 * owns the GET) and edits/deletes in place: each row can expand into an inline
 * edit form (PUT /api/Users/{id}, no password) or a two-step inline delete
 * confirm (DELETE /api/Users/{id}). Both mutate the atom on success, so the
 * list behind the dialog stays in sync without a refetch.
 *
 * The role `<select>` uses `editableRolesFor`: a row whose current role the
 * actor cannot assign still lists it (echoed, labelled "(current)") so an
 * unchanged role can be saved, and a legacy no-role row gets a "no role
 * (unchanged)" option. Mutation failures are shown from this hook instance's
 * LOCAL `operationError`, never the shared list `errorAtom`.
 */
export function ManageUsersModal({ actorRole, onClose }: ManageUsersModalProps) {
  const { users, error, operationError, updateUser, deleteUser } = useUsers({
    fetchOnMount: false,
  });

  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState('');
  const [editEmail, setEditEmail] = useState('');
  const [editRole, setEditRole] = useState<UserRole | ''>('');
  const [savingEdit, setSavingEdit] = useState(false);

  const [confirmingDeleteId, setConfirmingDeleteId] = useState<string | null>(
    null,
  );
  const [deletingId, setDeletingId] = useState<string | null>(null);

  const [attempted, setAttempted] = useState(false);
  const [status, setStatus] = useState<string | null>(null);

  function startEdit(user: User) {
    setStatus(null);
    setConfirmingDeleteId(null);
    setEditingId(user.id);
    setEditName(user.name);
    setEditEmail(user.email);
    setEditRole(user.role ?? '');
  }

  function cancelEdit() {
    setEditingId(null);
  }

  async function handleSaveEdit(event: FormEvent, user: User) {
    event.preventDefault();
    const trimmed = editName.trim();
    const trimmedEmail = editEmail.trim();
    if (!trimmed || !trimmedEmail || savingEdit) return;
    // A legacy no-role row may submit '' to keep its role-less state; every
    // other row must carry a role (possibly the preserved/echoed one).
    if (!editRole && user.role !== null) return;

    setSavingEdit(true);
    setAttempted(true);
    const updated = await updateUser(user.id, {
      name: trimmed,
      email: trimmedEmail,
      role: editRole === '' ? null : editRole,
    });
    setSavingEdit(false);

    if (updated) {
      setStatus(`Updated ${updated.name}.`);
      setEditingId(null);
    }
  }

  async function handleDelete(user: User) {
    if (deletingId) return;
    setDeletingId(user.id);
    setAttempted(true);
    const ok = await deleteUser(user.id);
    setDeletingId(null);
    if (ok) {
      setStatus(`Deleted ${user.name}.`);
      setConfirmingDeleteId(null);
    }
  }

  return (
    <div className="modal modal-open">
      <div
        className="modal-box max-w-[520px]"
        role="dialog"
        aria-modal="true"
        aria-labelledby="manage-users-title"
      >
        <h2
          id="manage-users-title"
          className="text-lg font-semibold text-base-content mb-3"
        >
          Manage users
        </h2>

        {users.length === 0 ? (
          error ? (
            <p className="text-sm text-error">{error}</p>
          ) : (
            <p className="text-sm text-base-content/70">No users found yet.</p>
          )
        ) : (
          <ul className="list-none m-0 p-0 border border-base-300 rounded-md bg-base-100 overflow-hidden divide-y divide-base-300">
            {users.map((user) => {
              const roleOptions = editableRolesFor(actorRole, user.role);
              const hasPreservedRole = roleOptions.some(
                (option) => option.isPreserved,
              );
              return (
                <li key={user.id} className="px-4 py-3">
                  {editingId === user.id ? (
                    <form
                      className="flex flex-col"
                      onSubmit={(e) => void handleSaveEdit(e, user)}
                    >
                      <FormField
                        label="Name"
                        htmlFor={`edit-name-${user.id}`}
                        inputProps={{
                          value: editName,
                          onChange: (e) => setEditName(e.target.value),
                          required: true,
                        }}
                      />
                      <FormField
                        label="Email"
                        htmlFor={`edit-email-${user.id}`}
                        inputProps={{
                          value: editEmail,
                          onChange: (e) => setEditEmail(e.target.value),
                          type: 'email',
                          autoComplete: 'email',
                          required: true,
                        }}
                      />
                      <FormField label="Role" htmlFor={`edit-role-${user.id}`}>
                        <select
                          id={`edit-role-${user.id}`}
                          name={`edit-role-${user.id}`}
                          className="select w-full"
                          value={editRole}
                          onChange={(e) =>
                            setEditRole(e.target.value as UserRole | '')
                          }
                          required={user.role !== null}
                        >
                          {roleOptions.map((option) => (
                            <option
                              key={option.role ?? 'no-role'}
                              value={option.value}
                              title={
                                option.isPreserved
                                  ? "You can't change a member with this role — only keep it as is."
                                  : undefined
                              }
                            >
                              {option.role === null
                                ? 'No role (unchanged)'
                                : option.isPreserved
                                  ? `${roleLabel(option.role)} (current)`
                                  : roleLabel(option.role)}
                            </option>
                          ))}
                        </select>
                      </FormField>
                      {hasPreservedRole && (
                        <p className="text-xs text-base-content/60 mt-1">
                          You can’t change members with this role — you can only
                          keep it as is.
                        </p>
                      )}
                      <div className="flex justify-end gap-2 mt-2">
                        <Button
                          variant="secondary"
                          type="button"
                          onClick={cancelEdit}
                          disabled={savingEdit}
                        >
                          Cancel
                        </Button>
                        <Button
                          type="submit"
                          disabled={
                            !editName.trim() ||
                            !editEmail.trim() ||
                            (!editRole && user.role !== null) ||
                            savingEdit
                          }
                        >
                          {savingEdit ? 'Saving…' : 'Save'}
                        </Button>
                      </div>
                    </form>
                  ) : confirmingDeleteId === user.id ? (
                    <div className="flex items-center justify-between gap-3">
                      <p className="text-sm text-base-content">
                        Delete <span className="font-medium">{user.name}</span>?
                      </p>
                      <div className="flex gap-2 shrink-0">
                        <button
                          type="button"
                          className="btn btn-sm"
                          onClick={() => setConfirmingDeleteId(null)}
                          disabled={deletingId === user.id}
                        >
                          Cancel
                        </button>
                        <button
                          type="button"
                          className="btn btn-error btn-sm"
                          onClick={() => void handleDelete(user)}
                          disabled={deletingId === user.id}
                        >
                          {deletingId === user.id ? 'Deleting…' : 'Delete'}
                        </button>
                      </div>
                    </div>
                  ) : (
                    <div className="flex items-start justify-between gap-3">
                      <div>
                        <p className="font-medium text-base-content">
                          {user.name}
                        </p>
                        {user.email && (
                          <p className="text-sm text-base-content/70">
                            {user.email}
                          </p>
                        )}
                        {user.role && (
                          <p className="text-sm text-base-content/70">
                            {roleLabel(user.role)}
                          </p>
                        )}
                      </div>
                      <div className="flex gap-2 shrink-0">
                        <button
                          type="button"
                          className="btn btn-sm"
                          onClick={() => startEdit(user)}
                        >
                          Edit
                        </button>
                        <button
                          type="button"
                          className="btn btn-error btn-sm"
                          onClick={() => {
                            setStatus(null);
                            setEditingId(null);
                            setConfirmingDeleteId(user.id);
                          }}
                        >
                          Delete
                        </button>
                      </div>
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
        )}

        {attempted && operationError && (
          <p className="text-sm mt-3 text-error">{operationError}</p>
        )}
        {status && (
          <p className="text-sm mt-3 text-success" role="status">
            {status}
          </p>
        )}

        <div className="flex justify-end gap-3 mt-5">
          <Button variant="secondary" type="button" onClick={onClose}>
            Close
          </Button>
        </div>
      </div>
    </div>
  );
}
