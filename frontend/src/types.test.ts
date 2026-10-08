import { describe, expect, it } from 'vitest';
import { USER_ROLES, roleLabel } from './types';
import type { UserRole } from './types';

describe('USER_ROLES', () => {
  it('holds exactly the four contract values, in order', () => {
    expect(USER_ROLES.map((r) => r.value)).toEqual([
      'SystemAdmin',
      'Researcher',
      'TrialManager',
      'FieldWorker',
    ]);
  });

  it('gives every role a non-empty human label', () => {
    for (const role of USER_ROLES) {
      expect(role.label.length).toBeGreaterThan(0);
    }
  });

  it('has unique values so a dropdown can key on them', () => {
    const values = USER_ROLES.map((r) => r.value);
    expect(new Set(values).size).toBe(values.length);
  });
});

describe('roleLabel', () => {
  it('returns an empty string for "no role" (null or empty)', () => {
    expect(roleLabel(null)).toBe('');
    expect(roleLabel('')).toBe('');
  });

  it('maps each of the four enum values to its human label', () => {
    const expected: Record<UserRole, string> = {
      SystemAdmin: 'System admin',
      Researcher: 'Researcher',
      TrialManager: 'Trial manager',
      FieldWorker: 'Field worker',
    };
    for (const role of Object.keys(expected) as UserRole[]) {
      expect(roleLabel(role)).toBe(expected[role]);
    }
  });

  it('passes an unknown string through unchanged rather than blanking it', () => {
    expect(roleLabel('LegacyRole')).toBe('LegacyRole');
  });
});
