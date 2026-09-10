import { atom } from 'jotai';
import type {
  CurrentUser,
  RatingCell,
  SubsampleRatingResult,
  TrialSummary,
  User,
} from '../types';

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
// Rating atoms
// ---------------------------------------------------------------------------

/** Cells returned by GET /api/Ratings/{plot}/{trait}. */
export const ratingCellsAtom = atom<RatingCell[]>([]);

/** Whether a record-rating API request is in flight. */
export const ratingRecordLoadingAtom = atom<boolean>(false);

/** Last error message from a record-rating API request, if any. */
export const ratingRecordErrorAtom = atom<string | null>(null);

/** Whether a read-plot-column API request is in flight. */
export const ratingReadLoadingAtom = atom<boolean>(false);

/** Last error message from a read-plot-column API request, if any. */
export const ratingReadErrorAtom = atom<string | null>(null);

/** The most recently recorded rating result, or null. */
export const lastRatingResultAtom = atom<SubsampleRatingResult | null>(null);

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

/** How many recordings are queued in IndexedDB waiting to sync ('pending'). */
export const pendingRecordingsCountAtom = atom<number>(0);

/** The trial currently selected on the voice-record screen. */
export const activeTrialIdAtom = atom<string | null>(null);