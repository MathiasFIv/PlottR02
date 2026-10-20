import type {
  CreateUserInput,
  LoginRequest,
  LoginResponse,
  RefreshResponse,
  TrialSheet,
  TrialSummary,
  UpdateUserInput,
  User,
} from '../types';
import type { AudioRecording } from './audioDb';
import { resolveNextTarget } from './nextTarget';
import { tokens } from './tokens';

export const API_BASE_URL: string = import.meta.env.VITE_API_URL ?? '';

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

let isRefreshing = false;
let refreshPromise: Promise<string> | null = null;

/** One refresh-and-retry leg on 401; concurrent 401s share a single refresh. */
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
 * Core fetch wrapper: one refresh-and-retry leg on 401 (single-flight across
 * concurrent calls); a second 401 clears tokens. `applyDefaultHeaders` false
 * lets the browser set the multipart Content-Type boundary but still attaches
 * Authorization — a logged-in sync must never start unauthenticated.
 */
async function requestWithRetry(
  path: string,
  init: RequestInit | undefined,
  withAuth: boolean,
  applyDefaultHeaders = true,
): Promise<Response> {
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

      if (response.status === 401) {
        tokens.clear();
      }
    } catch {
      throw new ApiError(401, 'Session expired');
    }
  }

  return response;
}

/**
 * Builds the thrown message for a failed response: `API request failed
 * (status): path` plus the server's plain-string body (JSON-string literal
 * unwrapped) whenever one is readable.
 */
export async function readApiErrorMessage(
  response: Response,
  path: string,
): Promise<string> {
  const fallback = `API request failed (${response.status}): ${path}`;
  let text: string;
  try {
    text = await response.text();
  } catch {
    return fallback;
  }
  if (!text.trim()) return fallback;
  try {
    const parsed = JSON.parse(text) as unknown;
    if (typeof parsed === 'string') {
      return parsed.trim() ? `${fallback} — ${parsed}` : fallback;
    }
  } catch {
    // Not JSON — use the raw body.
  }
  return `${fallback} — ${text}`;
}

async function jsonRequest<T>(
  path: string,
  init?: RequestInit,
  withAuth = true,
): Promise<T> {
  const response = await requestWithRetry(path, init, withAuth);
  if (!response.ok) {
    throw new ApiError(response.status, await readApiErrorMessage(response, path));
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
 * Multipart POST; skips the JSON Content-Type so the browser generates the
 * form boundary. Shares auth, 401-refresh retry and error-body handling with
 * `jsonRequest`.
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
    false,
  );
  if (!response.ok) {
    throw new ApiError(response.status, await readApiErrorMessage(response, path));
  }
  return (await response.json()) as T;
}

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
  /** Write target came from `defaultPlot`/`defaultSubsample`, not spoken fields. */
  usedDefaultTarget: boolean;
  /** Wrote from the transcript alone — the client sent no confirmed fields. */
  wasTranscriptOnly: boolean;
  submittedAt: string;
  transcript: string;
  transcriptionError?: string;
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
  /** Spoken values that could not be written at all; > 0 implies status Flagged. */
  outOfTraitRangeValueCount: number;
}

/** Correction body; `null` means "keep the already-confirmed value". */
export interface CorrectRecordingRequest {
  plot: number | null;
  subsample: number | null;
  value: number | null;
}

function extensionForMimeType(mimeType: string): string {
  const normalized = mimeType.split(';')[0]?.trim().toLowerCase() ?? '';
  if (normalized === 'audio/mp4' || normalized === 'audio/m4a') return 'mp4';
  if (normalized === 'audio/ogg' || normalized === 'audio/opus') return 'ogg';
  return 'webm';
}

/** Prefers the RFC 5987 `filename*=UTF-8''…` form of Content-Disposition, then plain, then `fallback`. */
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
      // Malformed URL-encoding — try the plain form.
    }
  }

  const quoted = /filename="([^"]+)"/i.exec(contentDisposition);
  if (quoted?.[1]) return quoted[1].trim();

  const plain = /filename=([^;\s]+)/i.exec(contentDisposition);
  if (plain?.[1]) return plain[1].trim();

  return fallback;
}

/**
 * Body for POST /api/Trials/{trialId}/cells. `traitOrdinal` is the 1-based
 * column ordinal (`valuesIndex + 1`; ordinal 1 → column C, 9 → column K).
 */
export interface EditCellInput {
  plot: number;
  subsample: number;
  traitOrdinal: number;
  value: number;
  overwrite: boolean;
}

/** `version` is the workbook version AFTER the write — re-poll with it. */
export interface CellEditResult {
  trialId: string;
  plot: number;
  subsample: number;
  traitOrdinal: number;
  value: number | null;
  written: boolean;
  skipped: boolean;
  skippedReason: 'cell_already_filled' | 'row_not_found' | null;
  priorValue: number | null;
  cellAddress: string;
  version: string;
  fileName: string | null;
  generatedAt: string;
}

export interface VoiceCellEditResult {
  trialId: string;
  plot: number;
  subsample: number;
  traitOrdinal: number;
  value: number | null;
  status:
    | 'written'
    | 'not_understood'
    | 'row_not_found'
    | 'cell_already_filled'
    | 'unexpected';
  written: boolean;
  skipped: boolean;
  skippedReason: 'cell_already_filled' | 'row_not_found' | null;
  priorValue: number | null;
  cellAddress: string;
  version: string;
  fileName: string | null;
  transcript: string;
  generatedAt: string;
}

export const api = {
  login: (input: LoginRequest) =>
    jsonRequest<LoginResponse>(
      '/api/Auth/login',
      { method: 'POST', body: JSON.stringify(input) },
      false,
    ),

  logout: () =>
    noContentRequest('/api/Auth/logout', { method: 'POST' }),

  getMe: () => jsonRequest<User>('/api/Users/me'),

  getUsers: () => jsonRequest<User[]>('/api/Users'),

  createUser: (input: CreateUserInput) =>
    jsonRequest<User>('/api/Users', {
      method: 'POST',
      body: JSON.stringify(input),
    }),

  updateUser: (id: string, input: UpdateUserInput) =>
    jsonRequest<User>(`/api/Users/${id}`, {
      method: 'PUT',
      body: JSON.stringify(input),
    }),

  deleteUser: (id: string) =>
    noContentRequest(`/api/Users/${id}`, { method: 'DELETE' }),

  listTrials: () => jsonRequest<TrialSummary[]>('/api/Trials'),

  listTrialUsers: (trialId: string) =>
    jsonRequest<User[]>(`/api/Trials/${trialId}/users`),

  assignTrialUsers: (trialId: string, userIds: string[]) =>
    noContentRequest(`/api/Trials/${trialId}/users`, {
      method: 'PUT',
      body: JSON.stringify({ userIds }),
    }),

  createTrial: (file: File) => {
    const body = new FormData();
    body.append('file', file);
    return formRequest<TrialSummary>('/api/Trials', body);
  },

  /** Revoking the object URL is deferred a macrotask — a synchronous revoke aborts the download in Safari. */
  downloadWorkbook: async (trialId: string): Promise<string> => {
    const path = `/api/Trials/${trialId}/workbook`;
    const response = await requestWithRetry(path, undefined, true);

    if (!response.ok) {
      throw new ApiError(response.status, await readApiErrorMessage(response, path));
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
    setTimeout(() => URL.revokeObjectURL(objectUrl), 0);

    return fileName;
  },

  /**
   * Pass the last payload's `version` as `since` for the fast path: an
   * unchanged file answers `changed: false` with every payload field null.
   */
  getTrialSheet: (trialId: string, since: string | undefined, signal?: AbortSignal) =>
    jsonRequest<TrialSheet>(
      `/api/Trials/${trialId}/sheet${since ? `?since=${encodeURIComponent(since)}` : ''}`,
      { signal },
    ),

  /**
   * 409 is not an ApiError — its body is the CellEditResult carrying
   * `priorValue` for the overwrite offer; every other non-2xx throws.
   */
  editCell: async (trialId: string, input: EditCellInput): Promise<CellEditResult> => {
    const path = `/api/Trials/${trialId}/cells`;
    const response = await requestWithRetry(path, {
      method: 'POST',
      body: JSON.stringify(input),
    }, true);

    if (response.ok || response.status === 409) {
      return (await response.json()) as CellEditResult;
    }
    throw new ApiError(response.status, await readApiErrorMessage(response, path));
  },

  /** Multipart voice edit; every outcome is a 200 VoiceCellEditResult (no 409 contract). */
  editCellFromVoice: (
    trialId: string,
    input: {
      plot: number;
      subsample: number;
      traitOrdinal: number;
      overwrite: boolean;
      audio: Blob;
      mimeType: string;
    },
  ): Promise<VoiceCellEditResult> => {
    const body = new FormData();
    body.append('plot', String(input.plot));
    body.append('subsample', String(input.subsample));
    body.append('traitOrdinal', String(input.traitOrdinal));
    body.append('overwrite', String(input.overwrite));
    body.append(
      'file',
      input.audio,
      `cell.${extensionForMimeType(input.mimeType)}`,
    );
    return formRequest<VoiceCellEditResult>(
      `/api/Trials/${trialId}/cells/voice`,
      body,
    );
  },

  /**
   * Sends one recording (multipart) for transcription + workbook write.
   * defaultPlot/defaultSubsample are sent only when both explicit fields are
   * absent; a snapshotted defaultTarget (key present) beats live resolution.
   */
  syncRecording: (rec: AudioRecording) => {
    const body = new FormData();
    body.append('clientRecordingId', rec.id);
    body.append('trialId', rec.trialId ?? '');
    body.append('plot', String(rec.plot ?? ''));
    body.append('subsample', String(rec.subsample ?? ''));
    body.append('value', String(rec.value ?? ''));
    const target =
      rec.plot == null && rec.subsample == null
        ? 'defaultTarget' in rec
          ? rec.defaultTarget ?? null
          : resolveNextTarget(rec.trialId ?? '')
        : null;
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
    body.append('createdAt', rec.createdAt);
    body.append('file', rec.blob, `rec.${extensionForMimeType(rec.mimeType)}`);
    return formRequest<SyncRecordingResponse>('/api/Recordings/sync', body);
  },

  /** Sends only the confirmed fields; a 409 (already written) surfaces as ApiError. */
  correctRecording: (clientRecordingId: string, fields: CorrectRecordingRequest) =>
    jsonRequest<SyncRecordingResponse>(
      `/api/Recordings/${encodeURIComponent(clientRecordingId)}/correct`,
      {
        method: 'POST',
        body: JSON.stringify(fields),
      },
    ),
};
