import { describe, expect, it } from 'vitest';
import {
  USER_ROLES,
  assignableCrew,
  assignableRoles,
  canCreateTrials,
  canCreateUsers,
  canManageUsers,
  roleLabel,
} from './types';
import type { User, UserRole } from './types';

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

describe('canManageUsers', () => {
  it('allows system admins and trial managers', () => {
    expect(canManageUsers('SystemAdmin')).toBe(true);
    expect(canManageUsers('TrialManager')).toBe(true);
  });

  it('denies researchers, field workers, and no-role users', () => {
    expect(canManageUsers('Researcher')).toBe(false);
    expect(canManageUsers('FieldWorker')).toBe(false);
    expect(canManageUsers(null)).toBe(false);
  });
});

describe('assignableRoles', () => {
  it('lets a system admin assign all four roles, in canonical order', () => {
    expect(assignableRoles('SystemAdmin')).toEqual([
      'SystemAdmin',
      'Researcher',
      'TrialManager',
      'FieldWorker',
    ]);
  });

  it('lets a researcher assign only trial managers and field workers', () => {
    expect(assignableRoles('Researcher')).toEqual(['TrialManager', 'FieldWorker']);
  });

  it('lets a trial manager assign only field workers', () => {
    expect(assignableRoles('TrialManager')).toEqual(['FieldWorker']);
  });

  it('lets a field worker assign nothing', () => {
    expect(assignableRoles('FieldWorker')).toEqual([]);
  });

  it('lets a no-role actor assign nothing', () => {
    expect(assignableRoles(null)).toEqual([]);
  });
});

describe('canCreateUsers', () => {
  it('allows system admins, researchers, and trial managers', () => {
    expect(canCreateUsers('SystemAdmin')).toBe(true);
    expect(canCreateUsers('Researcher')).toBe(true);
    expect(canCreateUsers('TrialManager')).toBe(true);
  });

  it('denies field workers and no-role actors', () => {
    expect(canCreateUsers('FieldWorker')).toBe(false);
    expect(canCreateUsers(null)).toBe(false);
  });
});

describe('canCreateTrials', () => {
  it('allows system admins, researchers, and trial managers', () => {
    expect(canCreateTrials('SystemAdmin')).toBe(true);
    expect(canCreateTrials('Researcher')).toBe(true);
    expect(canCreateTrials('TrialManager')).toBe(true);
  });

  it('denies field workers and no-role actors', () => {
    expect(canCreateTrials('FieldWorker')).toBe(false);
    expect(canCreateTrials(null)).toBe(false);
  });
});

describe('assignableCrew', () => {
  const users: User[] = [
    { id: '1', name: 'Admin', email: 'admin@example.com', role: 'SystemAdmin' },
    { id: '2', name: 'Res', email: 'res@example.com', role: 'Researcher' },
    { id: '3', name: 'Manager', email: 'manager@example.com', role: 'TrialManager' },
    { id: '4', name: 'Worker', email: 'worker@example.com', role: 'FieldWorker' },
    { id: '5', name: 'Legacy', email: 'legacy@example.com', role: null },
  ];

  it('includes researchers, trial managers, and field workers', () => {
    expect(assignableCrew(users).map((u) => u.id)).toEqual(['2', '3', '4']);
  });

  it('excludes system admins (above the matrix) and null-role users', () => {
    const ids = assignableCrew(users).map((u) => u.id);
    expect(ids).not.toContain('1');
    expect(ids).not.toContain('5');
  });

  it('returns an empty list when no user is assignable', () => {
    expect(
      assignableCrew([
        { id: '1', name: 'Admin', email: 'admin@example.com', role: 'SystemAdmin' },
        { id: '2', name: 'Legacy', email: 'legacy@example.com', role: null },
      ]),
    ).toEqual([]);
  });
});
