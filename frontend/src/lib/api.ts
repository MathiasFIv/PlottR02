import type {
  CreateUserInput,
  LoginRequest,
  LoginResponse,
  RatingCell,
  RecordSubsampleRatingsCommand,
  RefreshResponse,
  SubsampleRatingResult,
  TrialSummary,
  User,
} from '../types';
import type { AudioRecording } from './audioDb';
import { readDefaultTarget } from './defaultTarget';
import { tokens } from './tokens';

/**
 * API base URL. Defaults to empty string so requests go to the same origin and
 * the Vite dev-server proxy forwards `/api` to the ASP.NET Core backend (see
 * vite.config.ts). Override with VITE_API_URL (e.g. a deployed backend) in a
 * `.env` / `.env.local` file if needed.
 */
export const API_BASE_URL: string = import.meta.env.VITE_API_URL ?? '';

/** Thrown on non-2xx API responses so callers can branch on the status code. */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

// ---------------------------------------------------------------------------
// Token refresh — deduped so concurrent 401s share a single refresh call
// ---------------------------------------------------------------------------

let isRefreshing = false;
let refreshPromise: Promise<string> | null = null;

async function refreshAccessToken(): Promise<string> {
  const refreshToken = tokens.getRefreshToken();
  if (!refreshToken) {
    throw new ApiError(401, 'No refresh token available');
  }

  const response = await fetch(`${API_BASE_URL}/api/Auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken } as { refreshToken: string }),
  });

  if (!response.ok) {
    tokens.clear();
    throw new ApiError(401, 'Refresh token expired');
  }

  const data = (await response.json()) as RefreshResponse;
  tokens.set(data.accessToken, data.refreshToken);
  return data.accessToken;
}

// ---------------------------------------------------------------------------
// Request helpers
// ---------------------------------------------------------------------------

function buildHeaders(
  init?: RequestInit,
  withAuth = true,
): Record<string, string> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...((init?.headers as Record<string, string>) ?? {}),
  };
  if (withAuth) {
    const accessToken = tokens.getAccessToken();
    if (accessToken) {
      headers['Authorization'] = `Bearer ${accessToken}`;
    }
  }
  return headers;
}

/**
 * Core fetch wrapper. When `withAuth` is true and the server responds with 401,
 * a single token-refresh is attempted (deduped across concurrent calls). The
 * original request is then retried with the new access token. If refresh fails
 * an `ApiError(401)` is thrown.
 *
 * When `applyDefaultHeaders` is false (default true), `buildHeaders` is
 * skipped so that the browser can set its own Content-Type — required for
 * multipart/form-data where the boundary must be auto-generated.
 */
async function requestWithRetry(
  path: string,
  init: RequestInit | undefined,
  withAuth: boolean,
  applyDefaultHeaders = true,
): Promise<Response> {
  // When `applyDefaultHeaders` is false (multipart), we still attach the
  // Authorization header whenever a token exists — only Content-Type is
  // deferred to the browser so it can generate the multipart boundary. A
  // logged-in sync must never make a deliberately unauthenticated first
  // attempt: that produced a visible 401 on every recording and burnt a
  // single-use refresh-token rotation each time.
  let headers: Record<string, string> | undefined;
  if (applyDefaultHeaders) {
    headers = buildHeaders(init, withAuth);
  } else if (withAuth) {
    const accessToken = tokens.getAccessToken();
    if (accessToken) {
      headers = {
        ...((init?.headers as Record<string, string>) ?? {}),
        Authorization: `Bearer ${accessToken}`,
      };
    }
  }

  let response = await fetch(`${API_BASE_URL}${path}`, {
    ...init,
    headers,
  });

  if (response.status === 401 && withAuth && tokens.getAccessToken()) {
    // Start a refresh if one isn't already in flight
    if (!isRefreshing) {
      isRefreshing = true;
      refreshPromise = refreshAccessToken().finally(() => {
        isRefreshing = false;
        refreshPromise = null;
      });
    }

    try {
      const newToken = await refreshPromise;
      response = await fetch(`${API_BASE_URL}${path}`, {
        ...init,
        headers: applyDefaultHeaders
          ? {
              ...buildHeaders(init, withAuth),
              Authorization: `Bearer ${newToken}`,
            }
          : { Authorization: `Bearer ${newToken}` },
      });

      // If the retried request is still 401 the session has been revoked
      // mid-flight — clear tokens so isAuthenticatedAtom flips false and
      // the caller's throw lands in a logged-out state.
      if (response.status === 401) {
        tokens.clear();
      }
    } catch {
      throw new ApiError(401, 'Session expired');
    }
  }

  return response;
}

async function jsonRequest<T>(
  path: string,
  init?: RequestInit,
  withAuth = true,
): Promise<T> {
  const response = await requestWithRetry(path, init, withAuth);
  if (!response.ok) {
    throw new ApiError(
      response.status,
      `API request failed (${response.status}): ${path}`,
    );
  }
  return (await response.json()) as T;
}

async function noContentRequest(
  path: string,
  init?: RequestInit,
  withAuth = true,
): Promise<void> {
  const response = await requestWithRetry(path, init, withAuth);
  if (!response.ok) {
    throw new ApiError(
      response.status,
      `API request failed (${response.status}): ${path}`,
    );
  }
}

/**
 * Multipart form-data request. Skips the default `Content-Type: application/json`
 * header so that the browser auto-generates `multipart/form-data; boundary=...`.
 * Reuses the same auth header + 401-refresh-retry behaviour as `jsonRequest`.
 */
async function formRequest<T>(
  path: string,
  body: FormData,
  withAuth = true,
): Promise<T> {
  const response = await requestWithRetry(
    path,
    { method: 'POST', body },
    withAuth,
    false, // applyDefaultHeaders — let the browser set Content-Type for FormData
  );
  if (!response.ok) {
    let message = `API request failed (${response.status}): ${path}`;
    try {
      const text = await response.text();
      if (text) {
        // ASP.NET Core may serialize a plain-string error as a JSON string
        // literal (e.g. `"File is too large"` including the surrounding
        // quotes).  Try to parse so the user sees a clean message.
        try {
          const parsed = JSON.parse(text) as unknown;
          message = typeof parsed === 'string' ? parsed : text;
        } catch {
          // Not valid JSON (plain text) — use the raw body.
          message = text;
        }
      }
    } catch {
      // response body not readable — keep default message
    }
    throw new ApiError(response.status, message);
  }
  return (await response.json()) as T;
}

// ---------------------------------------------------------------------------
// Voice-to-Excel sync (POST /api/Recordings/sync)
// ---------------------------------------------------------------------------

/** Result of a "Send now" sync or a correction of one voice recording. */
export interface SyncRecordingResponse {
  clientRecordingId: string;
  trialId: string;
  status: 'Received' | 'Written' | 'SkippedPresent' | 'Flagged' | 'Replay';
  plot: number | null;
  subsample: number | null;
  value: number | null;
  parsedPlot: number | null;
  parsedSubsample: number | null;
  parsedValue: number | null;
  plotExists: boolean;
  traitOrdinal: number;
  needsReview: boolean;
  replayed: boolean;
  /** True when the write target's plot and/or subsample came from the
   *  `defaultPlot`/`defaultSubsample` fields (values-only utterance with a
   *  remembered target). The server sends this on every sync/correct response. */
  usedDefaultTarget: boolean;
  /** True when the write ran on the transcript alone because the client sent
   *  no confirmed fields (corrections never set it; only transcript-only syncs). */
  wasTranscriptOnly: boolean;
  submittedAt: string;
  transcript: string;
  confidence: number | null;
  writtenCells: {
    plot: number;
    subsample: number;
    traitOrdinal: number;
    value: number | null;
    cellAddress: string;
  }[];
  skippedPresentCells: {
    plot: number;
    subsample: number;
    traitOrdinal: number;
    value: number | null;
    cellAddress: string;
  }[];
  missingSubsampleIndices: number[];
  /** number of spoken values that could not be written at all (category full / beyond trait 9). > 0 implies status Flagged. */
  outOfTraitRangeValueCount: number;
}

/**
 * Body for POST /api/Recordings/{id}/correct — the human-confirmed correction
 * applied to a recording that came back `needs-attention` (or a `failed` one
 * with enough confident data to salvage). Because a plot without subsamples
 * legitimately has subsample === null, every field is nullable: the server
 * treats `null` as "keep the value already confirmed."
 */
export interface CorrectRecordingRequest {
  plot: number | null;
  subsample: number | null;
  value: number | null;
}

/** Maps a recorded container mime type to a file extension (default webm). */
function extensionForMimeType(mimeType: string): string {
  const normalized = mimeType.split(';')[0]?.trim().toLowerCase() ?? '';
  if (normalized === 'audio/mp4' || normalized === 'audio/m4a') return 'mp4';
  if (normalized === 'audio/ogg' || normalized === 'audio/opus') return 'ogg';
  return 'webm';
}

// ---------------------------------------------------------------------------
// Workbook download helpers
// ---------------------------------------------------------------------------

/**
 * Extracts the file name from a `Content-Disposition` header. Prefers the
 * RFC 5987 `filename*=UTF-8''...` (URL-encoded) form, falls back to the plain
 * `filename="..."` / `filename=...` form, and finally to `fallback`.
 */
function fileNameFromContentDisposition(
  contentDisposition: string | null,
  fallback: string,
): string {
  if (!contentDisposition) return fallback;

  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition);
  if (encoded?.[1]) {
    try {
      return decodeURIComponent(encoded[1].trim().replace(/^"+|"+$/g, ''));
    } catch {
      // Malformed URL-encoding — fall through to the plain form below.
    }
  }

  const quoted = /filename="([^"]+)"/i.exec(contentDisposition);
  if (quoted?.[1]) return quoted[1].trim();

  const plain = /filename=([^;\s]+)/i.exec(contentDisposition);
  if (plain?.[1]) return plain[1].trim();

  return fallback;
}

// ---------------------------------------------------------------------------
// Public API surface
// ---------------------------------------------------------------------------

export const api = {
  login: (input: LoginRequest) =>
    jsonRequest<LoginResponse>(
      '/api/Auth/login',
      { method: 'POST', body: JSON.stringify(input) },
      false,
    ),

  refresh: (input: { refreshToken: string }) =>
    jsonRequest<RefreshResponse>(
      '/api/Auth/refresh',
      { method: 'POST', body: JSON.stringify(input) },
      false,
    ),

  logout: () =>
    noContentRequest('/api/Auth/logout', { method: 'POST' }),

  getMe: () => jsonRequest<User>('/api/Users/me'),

  getUsers: () => jsonRequest<User[]>('/api/Users'),

  getUser: (id: string) => jsonRequest<User>(`/api/Users/${id}`),

  createUser: (input: CreateUserInput) =>
    jsonRequest<User>('/api/Users', {
      method: 'POST',
      body: JSON.stringify(input),
    }),

  updateUser: (id: string, input: CreateUserInput) =>
    jsonRequest<User>(`/api/Users/${id}`, {
      method: 'PUT',
      body: JSON.stringify(input),
    }),

  deleteUser: (id: string) =>
    noContentRequest(`/api/Users/${id}`, { method: 'DELETE' }),

  // Ratings ---------------------------------------------------------------

  recordRating: (input: RecordSubsampleRatingsCommand) =>
    jsonRequest<SubsampleRatingResult>('/api/Ratings', {
      method: 'POST',
      body: JSON.stringify(input),
    }),

  readPlotColumn: (plot: number, traitOrdinal: number) =>
    jsonRequest<RatingCell[]>(`/api/Ratings/${plot}/${traitOrdinal}`),

  // Trials ---------------------------------------------------------------

  listTrials: () => jsonRequest<TrialSummary[]>('/api/Trials'),

  createTrial: (file: File) => {
    const body = new FormData();
    body.append('file', file);
    return formRequest<TrialSummary>('/api/Trials', body);
  },

  /**
   * Downloads the trial's current workbook (newest accumulated export copy, or
   * the uploaded original) and saves it via the browser download. Returns the
   * file name used. Reuses the same auth header + 401 handling as the other calls.
   */
  downloadWorkbook: async (trialId: string): Promise<string> => {
    const path = `/api/Trials/${trialId}/workbook`;
    const response = await requestWithRetry(path, undefined, true);

    if (!response.ok) {
      // Mirror the existing helpers: surface a plain-text (or JSON-string
      // literal) body message when present, else a generic status message.
      let message = `API request failed (${response.status}): ${path}`;
      try {
        const text = await response.text();
        if (text) {
          try {
            const parsed = JSON.parse(text) as unknown;
            message = typeof parsed === 'string' ? parsed : text;
          } catch {
            // Not valid JSON (plain text) — use the raw body.
            message = text;
          }
        }
      } catch {
        // response body not readable — keep default message
      }
      throw new ApiError(response.status, message);
    }

    const blob = await response.blob();

    const fileName = fileNameFromContentDisposition(
      response.headers.get('Content-Disposition'),
      `trial-${trialId}.xlsx`,
    );

    const objectUrl = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = objectUrl;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    // Defer the revoke to a macrotask: calling it synchronously right after
    // click() has a documented history of aborting the download in Safari.
    setTimeout(() => URL.revokeObjectURL(objectUrl), 0);

    return fileName;
  },

  // Recordings (V2E — "Send now") ---------------------------------------

  /**
   * Sends one confirmed voice recording to the backend for transcription +
   * workbook writing. Multipart — the browser sets Content-Type (boundary)
   * automatically, exactly as createTrial does.
   */
  syncRecording: (rec: AudioRecording) => {
    const body = new FormData();
    body.append('clientRecordingId', rec.id);
    body.append('trialId', rec.trialId ?? '');
    body.append('plot', String(rec.plot ?? ''));
    body.append('subsample', String(rec.subsample ?? ''));
    body.append('value', String(rec.value ?? ''));
    // Values-only utterance ("values 4, 2, 6"): the operator is continuing a
    // run, so ship the default target as the server-side defaults. NEVER sent
    // when either field is present — explicit spoken values always win.
    // The row's `defaultTarget` snapshot (taken at first send in autoSync)
    // wins over live memory so a retry replays the target the clip was
    // INTENDED for; live memory only seeds the very first send.
    const target =
      rec.defaultTarget ??
      (rec.plot == null && rec.subsample == null
        ? readDefaultTarget(rec.trialId ?? '')
        : null);
    if (target !== null) {
      body.append('defaultPlot', String(target.plot));
      body.append('defaultSubsample', String(target.subsample));
    }
    const traitOrdinal = rec.traitOrdinal;
    if (
      typeof traitOrdinal === 'number' &&
      Number.isInteger(traitOrdinal) &&
      traitOrdinal > 0
    ) {
      body.append('traitOrdinal', String(traitOrdinal));
    }
    body.append('createdAt', rec.createdAt); // already ISO-8601 (stored via toISOString() at save)
    body.append('file', rec.blob, `rec.${extensionForMimeType(rec.mimeType)}`);
    return formRequest<SyncRecordingResponse>('/api/Recordings/sync', body);
  },

  /**
   * Applies a confirmed correction to a recording that came back
   * `needs-review` (or was transcript-only). Uses the JSON `correct` endpoint
   * so the client sends only the confirmed fields — never a transcript.
   *
   * Correction is inherently a "confirmed send": the client explicitly chose
   * plot/subsample/value, so the returned `SyncRecordingResponse` always has
   * `wasTranscriptOnly === false`. A 409 (already written by another client or
   * a prior correction) surfaces as `ApiError` with `status 409` — the queue
   * treats that as "already synced, nothing to correct."
   */
  correctRecording: (clientRecordingId: string, fields: CorrectRecordingRequest) =>
    jsonRequest<SyncRecordingResponse>(
      `/api/Recordings/${encodeURIComponent(clientRecordingId)}/correct`,
      {
        method: 'POST',
        body: JSON.stringify(fields),
      },
    ),
};
