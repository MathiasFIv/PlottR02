import { atom } from 'jotai';
import type { Theme } from '../lib/theme';
import type { TrialSheetSource, TrialSheetStatus } from '../lib/sheetStatus';
import type {
  CurrentUser,
  TrialSheet,
  TrialSummary,
  User,
} from '../types';

/**
 * The active UI theme (`plottr-light` / `plottr-dark`). A PURE literal
 * initializer on purpose: this module is imported by node-only test files
 * (nextTarget, sheetAnchor, sheetTarget, syncQueue), so there must be no
 * `window` / `localStorage` / `matchMedia` access at module evaluation.
 * `main.tsx` bootstraps the real initial value with `resolveInitialTheme()`
 * (matching the anti-FOUC inline script in index.html) before React mounts.
 */
export const themeAtom = atom<Theme>('plottr-light');

/** The authenticated user profile (populated on login or session restore). */
export const currentUserAtom = atom<CurrentUser | null>(null);

/**
 * Derived atom: true once the authenticated user profile is loaded. Dependency-less
 * atoms that read localStorage/other external state are evaluated only once and never
 * re-run (a stale-value footgun), so this derives from the reactive currentUserAtom
 * instead. The "login page flashing on refresh" concern is already handled in App.tsx,
 * which blocks the router behind a "Restoring session…" gate until restore completes.
 */
export const isAuthenticatedAtom = atom<boolean>(
  (get) => get(currentUserAtom) !== null,
);

/** All users currently loaded from GET /api/Users. */
export const usersAtom = atom<User[]>([]);

/** Whether a users API request is in flight. */
export const loadingAtom = atom<boolean>(false);

/** Last error message from a users API request, if any. */
export const errorAtom = atom<string | null>(null);

// ---------------------------------------------------------------------------
// Trial atoms
// ---------------------------------------------------------------------------

/** All trials currently loaded from GET /api/Trials. */
export const trialsAtom = atom<TrialSummary[]>([]);

/** Whether a trials list API request is in flight. */
export const trialsLoadingAtom = atom<boolean>(false);

/** Last error message from a trials list API request, if any. */
export const trialsErrorAtom = atom<string | null>(null);

/** Whether a trial upload (POST /api/Trials) is in flight. */
export const trialsUploadingAtom = atom<boolean>(false);

/** Last error message from a trial upload request, if any. */
export const trialsUploadErrorAtom = atom<string | null>(null);

// ---------------------------------------------------------------------------
// Voice recording atoms
// ---------------------------------------------------------------------------

/**
 * How many CONFIRMED recordings need attention before/while they sync: status
 * 'needs-attention' (correction surface open) or 'failed' (sync failed and
 * must be retried). Legacy 'ready' recordings are folded into this count only
 * until the startup sweep has synced them. The count seen while the confirm
 * card is open reflects recordings that were already fixed before this
 * session's clip.
 */
export const recordingsNeedingAttentionCountAtom = atom<number>(0);

/** The trial currently selected on the voice-record screen. */
export const activeTrialIdAtom = atom<string | null>(null);

// ---------------------------------------------------------------------------
// Sheet refresh signal (recording-driven live refresh — pre-N4 increment)
//
// SIGNAL-ONLY, declared OUTSIDE and ABOVE the trial-sheet payload block below:
// it is never read by the status pills, never touched by `clearSheetState()`
// in `useTrialSheet` (which owns only the payload block), and it is NOT part
// of the payload — nothing about the grid can be blanked or fabricated by it.
// It says only "the sync engine contacted the server for a send attempt on
// this trial — re-check the sheet" (`lib/sheetRefreshTick.ts` holds the pure
// trigger rules; `useTrialSheet` is the single observer). Bumped from
// non-React code through `getDefaultStore()` — the exact precedent already
// used for `recordingsNeedingAttentionCountAtom` in `lib/syncQueue.ts`.
// ---------------------------------------------------------------------------

/** One sheet re-check signal: monotonically growing `seq` + the trial it targets. */
export type SheetRefreshTick = { seq: number; trialId: string };

export const sheetRefreshTickAtom = atom<SheetRefreshTick | null>(null);

// ---------------------------------------------------------------------------
// Last-written target (N3 — "the cell we just wrote") — also declared OUTSIDE
// and ABOVE the trial-sheet payload block below, for the same reasons as the
// refresh tick: it SURVIVES route changes (the RecordView panel and
// `/trials/:trialId/sheet` swap hosts), it is NOT cleared by
// `clearSheetState()` in `useTrialSheet` (which owns only the payload block),
// and it is NEVER read by the status pills. Written from non-React code by
// `lib/sheetTarget.ts` through `getDefaultStore()` (the fixed
// `<Provider store={getDefaultStore()}>` in `main.tsx` makes those writes
// reach React); the hosts derive the NEXT target from it + the payload on
// every render (`lib/nextTarget.ts`), never from it alone.
// ---------------------------------------------------------------------------

/**
 * The plot+subsample of the LAST successful write (what the server resolved),
 * plus whether that write FILLED the category block it landed in
 * (`blockFull`, computed from the response's `writtenCells` against the
 * payload's `columns`). It is NOT a "next" pair: the next target is derived
 * at read time from the payload's `rows[]` (`deriveNextTarget`) — STAY on
 * this row while the block is unfilled, advance to the next subsample of the
 * SAME plot once it is full. Consumers MUST guard on `trialId` before using
 * it for a given trial's grid.
 */
export type SheetLastWritten = {
  trialId: string;
  plot: number;
  subsample: number;
  blockFull: boolean;
};

export const sheetLastWrittenAtom = atom<SheetLastWritten | null>(null);

// ---------------------------------------------------------------------------
// Write anchor (tap-to-anchor) — also declared OUTSIDE and ABOVE the
// trial-sheet payload block, for exactly the same reasons as the atoms
// above: it SURVIVES route changes (the RecordView panel and
// `/trials/:trialId/sheet` swap hosts), it is NOT cleared by
// `clearSheetState()` in `useTrialSheet` (which owns only the payload block),
// and it is NEVER read by the status pills.
//
// Deliberately SEPARATE from `sheetLastWrittenAtom` — never reused, never
// mutated into it: that atom is a last-written FACT feeding
// `deriveNextTarget` (the auto-continue cursor), while a tap is an INTENT.
// Advancing `sheetLastWrittenAtom` from a tap would move `Next:` and the N3
// highlight on a gesture that has not written anything yet, so the anchor
// lives here instead. Consumers MUST guard on `trialId`
// (`lib/sheetAnchor.ts#isAnchorForTrial`) before using it for a given
// trial's grid; it is cleared by the UI gesture (tapping a cell of the
// anchored row again) AND consumed by `lib/syncQueue.ts` after the first
// successful write the anchor drove (one-shot — a tap arms exactly one
// run).
// ---------------------------------------------------------------------------

/**
 * The tapped ROW the next values-only recording writes INTO: `plot` +
 * `subsample` (row identity — the Excel `SheetRow.row` cross-ref is never a
 * key). A tap is deliberately ROW-ONLY: the write always starts at the row's
 * first empty cell (the server scans forward from column C), so no column is
 * ever pinned.
 */
export type SheetSelected = {
  trialId: string;
  plot: number;
  subsample: number;
};

export const sheetSelectedAtom = atom<SheetSelected | null>(null);

// ---------------------------------------------------------------------------
// Trial sheet atoms (N2 — read-only grid)
//
// ⚠️ TWO HOSTS, ONE ATOM SET (PLAN_2026-10-07 §11 risk 1): the embedded
// `SheetPanel` on /record and the `/trials/:trialId/sheet` route (`SheetView`)
// are MUTUALLY EXCLUSIVE routes today — never render both in one tab, or they
// would drive each other's status pill (each host's fetch would flip the
// other's pill). `useTrialSheet` clears this block on unmount AND on
// `trialId` change, so switching trials never flashes the previous workbook's
// grid; the persisted snapshot in `lib/sheetSnapshot.ts` makes the resulting
// refetch instant and offline-safe.
// The `sheetRefreshTickAtom` signal above is deliberately NOT part of this
// block: clearing or switching hosts neither resets nor replays it.
// ---------------------------------------------------------------------------

/** Last-good full payload for the mounted host, or null when nothing is loaded. */
export const trialSheetAtom = atom<TrialSheet | null>(null);

/** Lifecycle of the sheet fetch — see `lib/sheetStatus.ts` for the union. */
export const trialSheetStatusAtom = atom<TrialSheetStatus>('idle');

/** Last sheet fetch failure (plain server string / transport message). */
export const trialSheetErrorAtom = atom<string | null>(null);

/**
 * ISO timestamp of the last successful network payload — or the snapshot's
 * `savedAt` right after hydrate, so the stale badge can say "saved …".
 */
export const trialSheetUpdatedAtAtom = atom<string | null>(null);

/**
 * Where the displayed payload came from: `'network'` = a live fetch confirmed
 * it, `'cache'` = hydrated/persisted snapshot (B1 — lets the pill say
 * `offline — showing saved copy`).
 */
export const trialSheetSourceAtom = atom<TrialSheetSource>('network');