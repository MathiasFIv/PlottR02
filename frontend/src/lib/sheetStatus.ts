/**
 * Pure status model for the read-only sheet (N2), shared by both hosts
 * (`views/SheetView.tsx` and `components/SheetPanel.tsx`).
 *
 * Keeping the derivation out of the components means the status pill and the
 * failure classification are unit-testable in plain Node — house style is
 * "components stay thin render wiring", no DOM test framework.
 *
 * Status set is the frozen one from PLAN_2026-10-06 §3/§7:
 * `loading | live | idle | error | not-found | offline`, plus the B1 `source`
 * facet (`network | cache`) so the pill can say `offline — showing saved copy`.
 * N2 ships initial fetch + manual Refresh only — `idle` is the pre-fetch
 * default and `live` the settled state; N4's poller reuses the same atoms.
 */

/** Lifecycle of the sheet for one host. */
export type TrialSheetStatus =
  | 'loading'
  | 'live'
  | 'idle'
  | 'error'
  | 'not-found'
  | 'offline';

/** Where the currently displayed payload came from. */
export type TrialSheetSource = 'network' | 'cache';

/** Visual weight of the status pill. */
export type SheetStatusTone = 'muted' | 'ok' | 'warn' | 'error';

export type SheetStatusDescriptor = {
  status: TrialSheetStatus;
  source: TrialSheetSource;
  /** Short pill text. */
  label: string;
  tone: SheetStatusTone;
  /** `saved …` suffix when a cached copy backs the pill, else null. */
  detail: string | null;
};

/**
 * Deterministic, locale-independent timestamp for the "saved at" badge:
 * `YYYY-MM-DD HH:mm` in local time (never throws on junk input).
 */
export function formatSavedAt(iso: string | null): string | null {
  if (iso === null) return null;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  const pad = (n: number): string => String(n).padStart(2, '0');
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}`
  );
}

/**
 * Maps (status, source) to the pill's text + tone.
 *
 * - hydrated snapshot still fetching → `stale — showing saved copy`
 * - fetch failed while a copy is on screen → `offline — showing saved copy`
 * - everything else is the plain status word.
 *
 * `offline` always claims a saved copy, so it derives `saved …` from `savedAt`
 * whenever that is usable — regardless of `source`, which stays `'network'`
 * after a network payload followed by a failed refresh. `loading`/`live` keep
 * the `source === 'cache'` gate.
 */
export function describeSheetStatus(
  status: TrialSheetStatus,
  source: TrialSheetSource,
  savedAt: string | null,
): SheetStatusDescriptor {
  const saved =
    status === 'offline' || source === 'cache' ? formatSavedAt(savedAt) : null;
  const detail = saved === null ? null : `saved ${saved}`;

  switch (status) {
    case 'loading':
      return source === 'cache'
        ? { status, source, label: 'stale — showing saved copy', tone: 'warn', detail }
        : { status, source, label: 'Loading sheet…', tone: 'muted', detail: null };
    case 'live':
      return source === 'cache'
        ? { status, source, label: 'live — saved copy', tone: 'ok', detail }
        : { status, source, label: 'live', tone: 'ok', detail: null };
    case 'offline':
      return {
        status,
        source,
        label: 'offline — showing saved copy',
        tone: 'warn',
        detail,
      };
    case 'not-found':
      return { status, source, label: 'not found', tone: 'error', detail: null };
    case 'error':
      return { status, source, label: 'error', tone: 'error', detail: null };
    case 'idle':
    default:
      return { status, source, label: 'Sheet not loaded', tone: 'muted', detail: null };
  }
}

/**
 * True for the server's plain-string 400 on a workbook that has no ratings
 * sheet (PLAN_2026-10-06 §3 error table + §9 risk 9): a NON-ARM upload. This
 * must render as a readable explanation, never as a generic error.
 */
export function isNonArmSheetError(message: string | null): boolean {
  if (message === null) return false;
  return /Sheet '[^']*' was not found/.test(message);
}

/**
 * Classifies a failed fetch into a status.
 *
 * Order matters:
 * 1. non-ARM 400 → `error` (the host renders the dedicated readable state);
 * 2. 404 → `not-found` (trial or workbook gone — persistent, not retryable);
 * 3. anything else with a payload already on screen → `offline` (keep showing
 *    the cached copy — PLAN §8 sweep step (e));
 * 4. otherwise → `error`.
 *
 * `errStatus` is `ApiError.status`, or null when the request never reached the
 * server (offline / DNS / connection reset).
 */
export function classifySheetFailure(
  message: string | null,
  errStatus: number | null,
  hasPayload: boolean,
): TrialSheetStatus {
  if (isNonArmSheetError(message)) return 'error';
  if (errStatus === 404) return 'not-found';
  if (hasPayload) return 'offline';
  return 'error';
}
