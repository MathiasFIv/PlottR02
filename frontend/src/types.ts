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
