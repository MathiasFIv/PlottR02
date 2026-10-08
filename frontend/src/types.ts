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

export type User = {
  id: string;
  name: string;
  email: string;
  role: string | null;
};

export type CreateUserInput = {
  name: string;
  email: string;
  role?: string | null;
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
// Rating types — mirror the ASP.NET Core WebAPI DTOs:
//
//   POST   /api/Ratings              -> SubsampleRatingResult
//   GET    /api/Ratings/{plot}/{trait} -> RatingCell[]
// ---------------------------------------------------------------------------

/** Command to record a rating for one plot/trait combination. */
export type RecordSubsampleRatingsCommand = {
  plot: number;
  traitOrdinal: number;
  value: number;
  subsampleIndices: number[];
};

/** A single written cell returned by the rating endpoint. */
export type WrittenCellDto = {
  subsample: number;
  cellAddress: string;
  value: number;
};

/** Result after successfully recording a rating. */
export type SubsampleRatingResult = {
  plot: number;
  traitOrdinal: number;
  value: number;
  writtenCells: WrittenCellDto[];
  untouchedSubsamples: number[];
  emptyPreserved: boolean;
};

/** Current value of a single cell when reading a plot column. */
export type RatingCell = {
  plot: number;
  subsample: number;
  traitOrdinal: number;
  value: number | null;
  cellAddress: string;
};

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
