import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AudioRecording } from './audioDb';
import type { SheetColumn, SheetRow, TrialSheet } from '../types';
import { ApiError, api, readApiErrorMessage } from './api';
import { writeDefaultTarget } from './defaultTarget';
import type { DefaultTargetMemory } from './defaultTarget';
import { resolveNextTarget } from './nextTarget';
import { writeSheetSnapshot } from './sheetSnapshot';

/**
 * House style: hand-rolled fakes, no mock library. Swaps global `fetch` and
 * `localStorage` (the token layer reads it on every authed request) and
 * returns the fetch spy for URL / signal assertions.
 */
const localStorageStub: Storage = {
  getItem: (key: string) =>
    key === 'plottR_accessToken'
      ? 'access-token'
      : key === 'plottR_refreshToken'
        ? 'refresh-token'
        : null,
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
  key: () => null,
  length: 0,
};

function stubTransport(): ReturnType<typeof vi.fn> {
  const fetchStub = vi.fn();
  vi.stubGlobal('fetch', fetchStub);
  vi.stubGlobal('localStorage', localStorageStub);
  return fetchStub;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

// ---------------------------------------------------------------------------
// Fixtures — verbatim shapes from the frozen contract (PLAN_2026-10-06 §3)
// ---------------------------------------------------------------------------

/** Unchanged fast path: every payload field PRESENT but null (never omitted). */
const unchangedSheet = {
  trialId: '0f1c0000-0000-0000-0000-000000000001',
  version: '3f2a91c4-63991234567890123-245760',
  changed: false,
  fileName: null,
  generatedAt: null,
  metadata: null,
  columns: null,
  rows: null,
};

const metadata = {
  crop: 'Winter wheat',
  ratingDate: '16Jun2026',
  ratingType: 'PESSEV',
  unit: '%AREA',
  sampleSize: '4',
  subsampleCount: '4',
};

/** 9 trait columns, part-major (C..K), ordinals 1..9. */
const columns: SheetColumn[] = [
  { traitOrdinal: 1, column: 'C', pestCode: 'SEPTTR', partRated: 'LEAF1', label: 'SEPTTR × LEAF1' },
  { traitOrdinal: 2, column: 'D', pestCode: 'ERYSGR', partRated: 'LEAF1', label: 'ERYSGR × LEAF1' },
  { traitOrdinal: 3, column: 'E', pestCode: 'PUCCST', partRated: 'LEAF1', label: 'PUCCST × LEAF1' },
  { traitOrdinal: 4, column: 'F', pestCode: 'SEPTTR', partRated: 'LEAF2', label: 'SEPTTR × LEAF2' },
  { traitOrdinal: 5, column: 'G', pestCode: 'ERYSGR', partRated: 'LEAF2', label: 'ERYSGR × LEAF2' },
  { traitOrdinal: 6, column: 'H', pestCode: 'PUCCST', partRated: 'LEAF2', label: 'PUCCST × LEAF2' },
  { traitOrdinal: 7, column: 'I', pestCode: 'SEPTTR', partRated: 'LEAF3', label: 'SEPTTR × LEAF3' },
  { traitOrdinal: 8, column: 'J', pestCode: 'ERYSGR', partRated: 'LEAF3', label: 'ERYSGR × LEAF3' },
  { traitOrdinal: 9, column: 'K', pestCode: 'PUCCST', partRated: 'LEAF3', label: 'PUCCST × LEAF3' },
];

const rows: SheetRow[] = [
  { row: 22, plot: 101, subsample: 1, values: [1, null, 0, null, null, null, null, null, null] },
  { row: 23, plot: 101, subsample: 2, values: [null, null, null, null, null, null, null, null, null] },
];

/** Full (changed) payload including the metadata block. */
const fullSheet = {
  ...unchangedSheet,
  changed: true,
  fileName: '4 subsamples_out_20261006_094512.xlsx',
  generatedAt: '2026-10-06T09:45:13.402Z',
  metadata,
  columns,
  rows,
};

/** Type-level + runtime guard: values[i] ↔ columns[i] (index = traitOrdinal − 1). */
function valueFor(column: SheetColumn, row: SheetRow): number | null {
  return row.values[column.traitOrdinal - 1];
}

// ---------------------------------------------------------------------------

describe('api.getTrialSheet', () => {
  it('builds the sheet URL — no ?since when absent, percent-encoded when present', async () => {
    const fetchStub = stubTransport();
    // Fresh Response per call — a Response body can only be read once.
    fetchStub.mockImplementation(async () => jsonResponse(unchangedSheet));

    await api.getTrialSheet('trial-1', undefined);
    expect(fetchStub.mock.calls[0]?.[0]).toBe('/api/Trials/trial-1/sheet');

    await api.getTrialSheet('trial-1', unchangedSheet.version);
    expect(fetchStub.mock.calls[1]?.[0]).toBe(
      '/api/Trials/trial-1/sheet?since=3f2a91c4-63991234567890123-245760',
    );

    // Reserved characters are encoded so a malformed version can't break the URL.
    await api.getTrialSheet('trial-1', 'v1 x&y/z');
    expect(fetchStub.mock.calls[2]?.[0]).toBe(
      '/api/Trials/trial-1/sheet?since=v1%20x%26y%2Fz',
    );
  });

  it('passes the AbortSignal through the initial request and the 401-refresh retry leg', async () => {
    const fetchStub = stubTransport();
    const controller = new AbortController();
    fetchStub
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(
        jsonResponse({ accessToken: 'new-access', refreshToken: 'new-refresh' }),
      )
      .mockResolvedValueOnce(jsonResponse(fullSheet));

    const sheet = await api.getTrialSheet('trial-1', undefined, controller.signal);

    expect(sheet.changed).toBe(true);
    expect(fetchStub).toHaveBeenCalledTimes(3);
    expect(fetchStub.mock.calls[1]?.[0]).toBe('/api/Auth/refresh');
    // Both sheet legs spread the caller's init, so the signal survives the
    // deduped 401 refresh — the retried request stays cancellable.
    const firstInit = fetchStub.mock.calls[0][1] as RequestInit;
    const retryInit = fetchStub.mock.calls[2][1] as RequestInit;
    expect(firstInit.signal).toBe(controller.signal);
    expect(retryInit.signal).toBe(controller.signal);
    // The retry really is authenticated with the refreshed token.
    expect(retryInit.headers).toMatchObject({
      Authorization: 'Bearer new-access',
    });
  });

  it('round-trips the unchanged fast path (nulls present) and pins values[i] ↔ columns[i]', async () => {
    const fetchStub = stubTransport();

    // Unchanged: all payload fields present as null — the server never omits
    // nulls, so `TrialSheet` requires them (type-level guard: the literal
    // below is assigned to TrialSheet, nulls are values, not optionals).
    fetchStub.mockResolvedValueOnce(jsonResponse(unchangedSheet));
    const unchanged: TrialSheet = await api.getTrialSheet('trial-1', unchangedSheet.version);
    expect(unchanged).toEqual(unchangedSheet);
    expect(unchanged.metadata).toBeNull();
    expect(unchanged.columns).toBeNull();
    expect(unchanged.rows).toBeNull();

    // Full payload with the metadata block.
    fetchStub.mockResolvedValueOnce(jsonResponse(fullSheet));
    const full: TrialSheet = await api.getTrialSheet('trial-1', undefined);
    expect(full.changed).toBe(true);
    expect(full.fileName).toBe('4 subsamples_out_20261006_094512.xlsx');
    expect(full.metadata).toEqual(metadata);

    // Contract invariant (risk 12): row.values[i] lines up with
    // columns[i].traitOrdinal — index = ordinal − 1.
    if (!full.columns || !full.rows) throw new Error('expected a full payload');
    expect(full.columns.map((c) => c.traitOrdinal)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9]);
    for (const row of full.rows) {
      expect(row.values).toHaveLength(full.columns.length);
    }
    expect(valueFor(full.columns[0], full.rows[0])).toBe(1); // ordinal 1 → index 0
    expect(valueFor(full.columns[2], full.rows[0])).toBe(0); // 0 is a real value
    expect(valueFor(full.columns[1], full.rows[0])).toBeNull(); // empty ≠ 0
    expect(valueFor(full.columns[8], full.rows[1])).toBeNull();
  });

  it('surfaces the server plain-string error body, falling back when the body is empty', async () => {
    const fetchStub = stubTransport();
    fetchStub.mockResolvedValueOnce(
      new Response("Workbook for trial 'trial-1' was not found on disk.", { status: 400 }),
    );

    const error = await api.getTrialSheet('trial-1', undefined).then(
      () => null,
      (e: unknown) => e,
    );
    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.status).toBe(400);
    // Recognisable prefix + the server's own message (decision A13).
    expect(apiError.message).toBe(
      "API request failed (400): /api/Trials/trial-1/sheet — Workbook for trial 'trial-1' was not found on disk.",
    );

    // Empty body → today's message, byte-for-byte.
    fetchStub.mockResolvedValueOnce(new Response(null, { status: 500 }));
    const emptyError = (await api.getTrialSheet('trial-1', undefined).catch(
      (e: unknown) => e,
    )) as ApiError;
    expect(emptyError.message).toBe('API request failed (500): /api/Trials/trial-1/sheet');

    // ASP.NET may serialize a plain string as a JSON string literal — unwrapped.
    expect(
      await readApiErrorMessage(
        new Response('"File is too large"', { status: 400 }),
        '/api/Trials',
      ),
    ).toBe('API request failed (400): /api/Trials — File is too large');
  });
});


// ---------------------------------------------------------------------------
// syncRecording — the default-target fields on the wire (file-derived next
// target: frozen key wins, absent key resolves from last-written + payload)
// ---------------------------------------------------------------------------

/** Plot 101, subsamples 1..4 — the workbook order `next` derives from. */
const syncRows: SheetRow[] = [1, 2, 3, 4].map((subsample, i) => ({
  row: 22 + i,
  plot: 101,
  subsample,
  values: [],
}));

const syncSheet: TrialSheet = {
  trialId: 'trial-1',
  version: 'v1',
  changed: true,
  fileName: '4 subsamples.xlsx',
  generatedAt: '2026-10-06T09:45:13.402Z',
  metadata: null,
  columns: [],
  rows: syncRows,
};

/**
 * Node-safe storage BOTH layers can see: the token layer (bare `localStorage`)
 * and the file-target helpers (`safeStorage()` reads `window.localStorage`).
 * Returns the fetch spy for FormData assertions.
 */
function stubSyncTransport(): {
  fetch: ReturnType<typeof vi.fn>;
  storage: Storage;
} {
  const map = new Map<string, string>();
  const storage: Storage = {
    getItem: (key: string) => map.get(key) ?? null,
    setItem: (key: string, value: string) => {
      map.set(key, value);
    },
    removeItem: (key: string) => {
      map.delete(key);
    },
    clear: () => {
      map.clear();
    },
    key: (index: number) => [...map.keys()][index] ?? null,
    get length() {
      return map.size;
    },
  };
  const fetchStub = vi.fn(() =>
    jsonResponse({
      clientRecordingId: 'rec-sync-1',
      trialId: 'trial-1',
      status: 'Written',
      plot: 101,
      subsample: 4,
    }),
  );
  vi.stubGlobal('fetch', fetchStub);
  vi.stubGlobal('localStorage', storage); // token layer
  vi.stubGlobal('window', { localStorage: storage }); // safeStorage()
  return { fetch: fetchStub, storage };
}

/** Last written 101/3 (full block) + payload rows 101/1..101/4 → next is 101/4. */
function seedFile(
  storage: Storage,
  written: DefaultTargetMemory = { plot: 101, subsample: 3, blockFull: true },
): void {
  writeDefaultTarget('trial-1', written, storage);
  writeSheetSnapshot('trial-1', syncSheet, 'network', storage);
}

/** Values-only clip: both spoken fields null (defaults branch eligible). */
function valuesOnly(overrides: Partial<AudioRecording> = {}): AudioRecording {
  return {
    id: 'rec-sync-1',
    blob: new Blob(['fake-audio'], { type: 'audio/webm' }),
    mimeType: 'audio/webm',
    createdAt: '2026-10-06T09:45:13.402Z',
    trialId: 'trial-1',
    plot: null,
    subsample: null,
    value: null,
    status: 'pending',
    syncAttempts: 0,
    ...overrides,
  };
}

describe('syncRecording default-target fields', () => {
  /** The multipart body of the single request this test issued. */
  function wireOf(fetchStub: ReturnType<typeof vi.fn>): FormData {
    expect(fetchStub.mock.calls).toHaveLength(1);
    return fetchStub.mock.calls[0]?.[1]?.body as FormData;
  }

  it('an unsnapshotted row resolves the file-derived next target at send time', async () => {
    const { fetch, storage } = stubSyncTransport();
    seedFile(storage);

    await api.syncRecording(valuesOnly()); // `defaultTarget` key ABSENT

    const body = wireOf(fetch);
    expect(body.get('defaultPlot')).toBe('101');
    expect(body.get('defaultSubsample')).toBe('4');
    expect(body.get('plot')).toBe('');
    expect(body.get('subsample')).toBe('');
  });

  it('an end-of-plot derived null ships NO default target fields', async () => {
    const { fetch, storage } = stubSyncTransport();
    // Last written 101/4 (full block) — the last row of the only plot in the
    // payload, so the file-derived next target is null (no cross-plot wrap).
    seedFile(storage, { plot: 101, subsample: 4, blockFull: true });
    expect(resolveNextTarget('trial-1')).toBeNull();

    await api.syncRecording(valuesOnly()); // key ABSENT → resolves at send time

    const body = wireOf(fetch);
    // The null resolution adds nothing: no defaults on the wire, and the
    // clip's own (absent) spoken fields stay empty.
    expect(body.get('defaultPlot')).toBeNull();
    expect(body.get('defaultSubsample')).toBeNull();
    expect(body.get('plot')).toBe('');
    expect(body.get('subsample')).toBe('');
  });

  it('a frozen null stays null — the key being present wins over live memory', async () => {
    const { fetch, storage } = stubSyncTransport();
    seedFile(storage);
    // Live resolution WOULD have a target to ship …
    expect(resolveNextTarget('trial-1')).toEqual({ plot: 101, subsample: 4 });

    await api.syncRecording(valuesOnly({ defaultTarget: null }));

    const body = wireOf(fetch);
    expect(body.get('defaultPlot')).toBeNull();
    expect(body.get('defaultSubsample')).toBeNull();
  });

  it('a frozen pair ships as-is — a retry never re-resolves the target', async () => {
    const { fetch, storage } = stubSyncTransport();
    seedFile(storage); // live next would be 101/4

    await api.syncRecording(
      valuesOnly({ defaultTarget: { plot: 101, subsample: 2 } }),
    );

    const body = wireOf(fetch);
    expect(body.get('defaultPlot')).toBe('101');
    expect(body.get('defaultSubsample')).toBe('2');
  });

  it('an explicit spoken plot/subsample always suppresses the defaults', async () => {
    const { fetch, storage } = stubSyncTransport();
    seedFile(storage);

    await api.syncRecording(
      valuesOnly({
        plot: 12,
        subsample: 1,
        defaultTarget: { plot: 101, subsample: 4 },
      }),
    );

    const body = wireOf(fetch);
    expect(body.get('plot')).toBe('12');
    expect(body.get('subsample')).toBe('1');
    expect(body.get('defaultPlot')).toBeNull();
    expect(body.get('defaultSubsample')).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// editCell — single-cell write (double-tap-to-edit)
// ---------------------------------------------------------------------------

/** A landed write: `written:true`, 200, NEW version to re-poll with. */
const writtenResult = {
  trialId: 'trial-1',
  plot: 101,
  subsample: 2,
  traitOrdinal: 3,
  value: 4,
  written: true,
  skipped: false,
  skippedReason: null,
  priorValue: null,
  cellAddress: 'E23',
  version: '9f2a91c4-63991234567890123-245760',
  fileName: '4 subsamples_out.xlsx',
  generatedAt: '2026-10-17T09:45:13.402Z',
};

/** 409 conflict body: SAME shape, skipped, prior value offered. */
const conflictResult = {
  ...writtenResult,
  value: 4,
  written: false,
  skipped: true,
  skippedReason: 'cell_already_filled',
  priorValue: 7,
};

describe('api.editCell', () => {
  const input = {
    plot: 101,
    subsample: 2,
    traitOrdinal: 3,
    value: 4,
    overwrite: false,
  };

  it('POSTs the body to /api/Trials/{trialId}/cells and returns the result', async () => {
    const fetchStub = stubTransport();
    fetchStub.mockResolvedValueOnce(jsonResponse(writtenResult));

    const result = await api.editCell('trial-1', input);

    expect(result.written).toBe(true);
    expect(result.version).toBe('9f2a91c4-63991234567890123-245760');
    const [url, init] = fetchStub.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/Trials/trial-1/cells');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(input);
  });

  it('returns the result for a 409 conflict instead of throwing', async () => {
    const fetchStub = stubTransport();
    fetchStub.mockResolvedValueOnce(jsonResponse(conflictResult, 409));

    const result = await api.editCell('trial-1', input);

    expect(result.written).toBe(false);
    expect(result.skipped).toBe(true);
    expect(result.skippedReason).toBe('cell_already_filled');
    // The prior value survives (an ApiError could not carry it).
    expect(result.priorValue).toBe(7);
  });

  it('returns a skipped row_not_found result on 200 (not an error)', async () => {
    const fetchStub = stubTransport();
    fetchStub.mockResolvedValueOnce(
      jsonResponse(
        {
          ...writtenResult,
          written: false,
          skipped: true,
          skippedReason: 'row_not_found',
        },
        200,
      ),
    );

    const result = await api.editCell('trial-1', input);
    expect(result.written).toBe(false);
    expect(result.skippedReason).toBe('row_not_found');
  });

  it('throws ApiError with the server message on 400', async () => {
    const fetchStub = stubTransport();
    fetchStub.mockResolvedValueOnce(
      new Response('Value must be between 0 and 10.', { status: 400 }),
    );

    const error = (await api.editCell('trial-1', input).catch(
      (e: unknown) => e,
    )) as ApiError;
    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(400);
    expect(error.message).toBe(
      'API request failed (400): /api/Trials/trial-1/cells — Value must be between 0 and 10.',
    );
  });

  it('throws ApiError(404) when the trial is missing', async () => {
    const fetchStub = stubTransport();
    fetchStub.mockResolvedValueOnce(new Response(null, { status: 404 }));

    const error = (await api.editCell('trial-1', input).catch(
      (e: unknown) => e,
    )) as ApiError;
    expect(error.status).toBe(404);
  });
});
