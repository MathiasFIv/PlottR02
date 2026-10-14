import { assignableRoles } from '../types';
import type { UserRole } from '../types';

/**
 * One `<option>` in the manage-users inline edit form's role `<select>`.
 *
 * The list is the actor's assignable roles, optionally plus the row's CURRENT
 * role even when the actor may not assign it (the backend accepts an unchanged
 * role — echoing it is not an assignment) and, for a legacy no-role row, a
 * "no role (unchanged)" choice so name/email-only edits still submit.
 */
export type EditableRoleOption = {
  /** `<option>` value: a role value, or `''` for the "no role (unchanged)" option. */
  value: UserRole | '';
  /** The role this option represents; `null` for the "no role" option. */
  role: UserRole | null;
  /** True when this option is the row's current role (selected when the form opens). */
  isCurrent: boolean;
  /**
   * True when the actor may NOT assign this role: it is offered only so an
   * unchanged role can be echoed back. Always paired with `isCurrent` and a
   * non-null `role` (a preserved option is never the "no role" choice).
   */
  isPreserved: boolean;
};

/**
 * Builds the role options for editing `currentRole` as `actorRole`.
 *
 * Without this, a manager who cannot assign the row's existing role (e.g. a
 * TrialManager editing a Researcher) would get a `<select>` whose DOM value
 * collapses to the empty placeholder — the `required` attribute then blocks
 * Save silently. Including the current role fixes that, and `isPreserved`
 * lets the UI label/tooltip it as echo-only. A `null` current role gets an
 * explicit "no role (unchanged)" option (value `''`) so the required/typed
 * update can still be submitted.
 */
export function editableRolesFor(
  actorRole: UserRole | null,
  currentRole: UserRole | null,
): EditableRoleOption[] {
  const assignable = assignableRoles(actorRole);

  const options: EditableRoleOption[] = assignable.map((role) => ({
    value: role,
    role,
    isCurrent: role === currentRole,
    isPreserved: false,
  }));

  // Echo the row's current role even when the actor can't assign it — the
  // backend treats an unchanged role as a no-op, so the form must be able to
  // submit it. Never duplicated: only added when it is NOT assignable.
  if (currentRole !== null && !assignable.includes(currentRole)) {
    options.push({
      value: currentRole,
      role: currentRole,
      isCurrent: true,
      isPreserved: true,
    });
  }

  // A legacy no-role row has no assignable match; offer an explicit
  // "no role (unchanged)" choice so name/email-only edits still submit.
  if (currentRole === null) {
    options.push({
      value: '',
      role: null,
      isCurrent: true,
      isPreserved: false,
    });
  }

  return options;
}
