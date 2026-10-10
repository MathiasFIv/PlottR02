import { describe, expect, it } from 'vitest';
import { editableRolesFor } from './userRoles';
import type { EditableRoleOption } from './userRoles';

/**
 * Guards the MAJOR manage-users bug: a role the actor cannot assign must still
 * appear in the edit `<select>` when it is the row's current role, or the DOM
 * value collapses to the empty placeholder and `required` silently blocks Save.
 * Pure module — plain Node, no DOM, no mocks (house style).
 */

/** True when `options` contains a role option for `role`. */
function hasRole(
  options: EditableRoleOption[],
  role: EditableRoleOption['role'],
): boolean {
  return options.some((option) => option.role === role);
}

describe('editableRolesFor — actor CAN assign the current role', () => {
  it('returns just the assignable roles, with the current one flagged', () => {
    const options = editableRolesFor('TrialManager', 'FieldWorker');

    expect(options).toEqual([
      { value: 'FieldWorker', role: 'FieldWorker', isCurrent: true, isPreserved: false },
    ]);
  });

  it('a system admin sees all four roles — none preserved', () => {
    const options = editableRolesFor('SystemAdmin', 'Researcher');

    expect(options.map((o) => o.value)).toEqual([
      'SystemAdmin',
      'Researcher',
      'TrialManager',
      'FieldWorker',
    ]);
    const researcher = options.find((o) => o.role === 'Researcher');
    expect(researcher).toMatchObject({ isCurrent: true, isPreserved: false });
    expect(options.some((o) => o.isPreserved)).toBe(false);
  });
});

describe('editableRolesFor — actor CANNOT assign the current role', () => {
  it('preserves and flags the current role alongside the assignable ones', () => {
    const options = editableRolesFor('TrialManager', 'Researcher');

    // The one assignable role the actor may grant.
    expect(options).toContainEqual({
      value: 'FieldWorker',
      role: 'FieldWorker',
      isCurrent: false,
      isPreserved: false,
    });
    // The current role is echoed and marked preserved (echo-only).
    expect(options).toContainEqual({
      value: 'Researcher',
      role: 'Researcher',
      isCurrent: true,
      isPreserved: true,
    });
    // Exactly one preserved option, and it is the current role.
    expect(options.filter((o) => o.isPreserved).map((o) => o.role)).toEqual(['Researcher']);
  });

  it('is the ONLY option for an actor with no assignable roles', () => {
    const options = editableRolesFor('FieldWorker', 'SystemAdmin');

    expect(options).toEqual([
      { value: 'SystemAdmin', role: 'SystemAdmin', isCurrent: true, isPreserved: true },
    ]);
  });

  it('never lists an assignable role twice when it is also the current role', () => {
    const options = editableRolesFor('TrialManager', 'FieldWorker');

    expect(options.map((o) => o.value)).toEqual(['FieldWorker']);
    expect(new Set(options.map((o) => o.value)).size).toBe(options.length);
  });
});

describe('editableRolesFor — null current role (legacy / no role)', () => {
  it('adds a "no role (unchanged)" option so name/email-only edits submit', () => {
    const options = editableRolesFor('TrialManager', null);

    expect(options).toContainEqual({
      value: '',
      role: null,
      isCurrent: true,
      isPreserved: false,
    });
    // The assignable role is still offered, just not selected.
    expect(hasRole(options, 'FieldWorker')).toBe(true);
    expect(hasRole(options, null)).toBe(true);
  });

  it('offers only the "no role" option to an actor who can assign nothing', () => {
    const options = editableRolesFor('FieldWorker', null);

    expect(options).toEqual([
      { value: '', role: null, isCurrent: true, isPreserved: false },
    ]);
  });
});
