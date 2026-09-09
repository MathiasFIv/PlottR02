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
  const headers = applyDefaultHeaders ? buildHeaders(init, withAuth) : undefined;

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
};
