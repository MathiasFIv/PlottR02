/**
 * API contract types — mirror the ASP.NET Core WebAPI DTOs:
 *
 *   GET  /api/Users          -> User[]
 *   GET  /api/Users/{id}     -> User | 404
 *   POST /api/Users          -> 201 User     (body: CreateUserInput)
 *   PUT  /api/Users/{id}     -> 200 User     (body: CreateUserInput)
 *   DELETE /api/Users/{id}   -> 204
 *   POST /api/Auth/login     -> 200 LoginResponse | 401 (body: LoginRequest)
 */

/**
 * The role enum shared with the API. The backend validates `role` against
 * exactly these four PascalCase values (anything else is a 400), so this list
 * is the single source of truth for the create-user dropdowns. A role is now
 * required on create — `null` ("no role") is never submittable.
 */
export const USER_ROLES = [
  { value: 'SystemAdmin', label: 'System admin' },
  { value: 'Researcher', label: 'Researcher' },
  { value: 'TrialManager', label: 'Trial manager' },
  { value: 'FieldWorker', label: 'Field worker' },
] as const;

/** One of the four API role values, e.g. `'TrialManager'`. */
export type UserRole = (typeof USER_ROLES)[number]['value'];

/**
 * Human-readable label for a role. Returns `''` for "no role" (null/empty)
 * and the display label otherwise (`'SystemAdmin'` -> `'System admin'`).
 * Unknown strings pass through unchanged so nothing ever renders blank.
 */
export function roleLabel(role: string | null): string {
  if (!role) return '';
  return USER_ROLES.find((r) => r.value === role)?.label ?? role;
}

/**
 * Whether a role may list, read, update, or delete users. Only system admins
 * and trial managers qualify — researchers and field workers never see the
 * dashboard's user list. Account creation is broader; see `canCreateUsers`.
 * `null` ("no role") is never allowed.
 */
export function canManageUsers(role: UserRole | null): boolean {
  return role === 'SystemAdmin' || role === 'TrialManager';
}

/**
 * Roles a given actor is allowed to assign when creating a new account.
 * Mirrors the backend permission matrix. No role can ever be assigned.
 */
export const ASSIGNABLE_ROLES: Record<UserRole, readonly UserRole[]> = {
  SystemAdmin: ['SystemAdmin', 'Researcher', 'TrialManager', 'FieldWorker'],
  Researcher: ['TrialManager', 'FieldWorker'],
  TrialManager: ['FieldWorker'],
  FieldWorker: [],
};

/** Roles the given actor may assign to a new user. Empty for FieldWorker / no-role actors. */
export function assignableRoles(actor: UserRole | null): UserRole[] {
  return actor ? [...ASSIGNABLE_ROLES[actor]] : [];
}

/** Whether the actor may create any account at all (SystemAdmin, Researcher, TrialManager). */
export function canCreateUsers(actor: UserRole | null): boolean {
  return actor !== null && ASSIGNABLE_ROLES[actor].length > 0;
}

export type User = {
  id: string;
  name: string;
  email: string;
  role: UserRole | null;
};

export type CreateUserInput = {
  name: string;
  email: string;
  /** Exactly one of the four roles — required, never null/omitted. */
  role: UserRole;
  password: string;
};

export type LoginRequest = {
  email: string;
  password: string;
};

export type LoginResponse = {
  user: User;
  accessToken: string;
  refreshToken: string;
};

export type RefreshResponse = {
  accessToken: string;
  refreshToken: string;
};

/**
 * The authenticated user returned by the login endpoint. Login is now real:
 * credentials are verified server-side against the stored BCrypt hash, and the
 * returned profile is what the app treats as the current user.
 */
export type CurrentUser = User;

// ---------------------------------------------------------------------------
// Trial types — mirror the ASP.NET Core WebAPI DTOs:
//
//   GET  /api/Trials          -> TrialSummary[]
//   POST /api/Trials          -> 201 TrialSummary  (multipart/form-data, field: file)
// ---------------------------------------------------------------------------

/** Summary of a trial workbook uploaded to the backend. */
export type TrialSummary = {
  trialId: string;
  externalId: string;
  fileName: string;
  status: string;
  createdAt: string;
  createdBy: string | null;
};

// ---------------------------------------------------------------------------
// Trial sheet types — mirror the frozen contract for
// GET /api/Trials/{trialId}/sheet?since={version}
// (PLAN_2026-10-06 §3 + metadata block per PLAN_2026-10-07 §9 N1).
//
// The server NEVER omits nulls: an unchanged (fast-path) response carries
// `changed: false` with fileName/generatedAt/metadata/columns/rows all
// PRESENT but null. These fields are therefore required-but-nullable — never
// optional — so consumers must branch on null instead of on absence.
// ---------------------------------------------------------------------------

/** One trait column (cols C..K), server-derived from workbook rows 3 (pest) + 8 (part). */
export type SheetColumn = {
  /** 1..9 — the value index in `SheetRow.values` is `traitOrdinal - 1`. */
  traitOrdinal: number;
  /** Excel column letter, e.g. "C". */
  column: string;
  pestCode: string | null;
  partRated: string | null;
  /** Display label, e.g. "SEPTTR × LEAF1". */
  label: string;
};

/** One workbook data row (Excel rows 22..245) with its typed cells. */
export type SheetRow = {
  /** Excel row number — keeps the `C22` ↔ row 22 cross-reference. */
  row: number;
  plot: number;
  subsample: number;
  /** Index-aligned with `TrialSheet.columns`: `values[traitOrdinal - 1]`.
   *  `null` = empty/text cell; `0` is a real value (empty ≠ 0). */
  values: (number | null)[];
};

/** Workbook-level ARM metadata strip — display-only (values are not ISO, e.g. "16Jun2026"). */
export type SheetMetadata = {
  crop: string | null;
  ratingDate: string | null;
  ratingType: string | null;
  unit: string | null;
  sampleSize: string | null;
  subsampleCount: string | null;
};

/** 200 body of GET /api/Trials/{trialId}/sheet — full payload, or fast path. */
export type TrialSheet = {
  trialId: string;
  /** Opaque version token — echo back as `?since=`. */
  version: string;
  changed: boolean;
  /** Null when `changed` is false. */
  fileName: string | null;
  /** ISO-8601 UTC; null when `changed` is false. */
  generatedAt: string | null;
  metadata: SheetMetadata | null;
  columns: SheetColumn[] | null;
  rows: SheetRow[] | null;
};
