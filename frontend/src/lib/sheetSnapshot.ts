/**
 * Persisted offline snapshot of the last-good sheet payload (decision B1,
 * storage backend settled A-6: **`localStorage` via the existing
 * `safeStorage()` pattern** from `lib/defaultTarget.ts:57`).
 *
 * Why localStorage (PLAN_2026-10-07 §10 A-6): synchronous hydrate — no async
 * boot path, trivially testable, and the session token already lives in
 * `localStorage`, so if storage is evicted the user is logged out anyway and
 * the snapshot going with it is moot.
 *
 * Shape written per trial:
 *
 * ```jsonc
 * { "trialId": "…", "savedAt": "2026-10-07T09:45:13.402Z",
 *   "source": "network", "sheet": { /* full TrialSheet *\/ } }
 * ```
 *
 * - `savedAt` (ISO) → the "saved at" detail on the stale/offline badge.
 * - `source` → where the payload came from when it was saved; the hook flips
 *   its own atom to `'cache'` while hydrating so the pill can say
 *   `offline — showing saved copy`.
 *
 * Namespaced per trial (`plottR-sheet-snapshot:<trialId>`): plot numbers and
 * payloads repeat freely across workbooks, so one trial must never hydrate
 * another trial's grid.
 *
 * Guards: corrupt JSON, a mismatched/foreign trial id, a missing/broken
 * envelope, a structurally broken sheet (missing/wrong-typed `version`, or
 * non-array `columns`/`rows`) and an oversize payload (> 200 kB — the real
 * payload is ~26 kB) all read back as `null`, i.e. "no snapshot". Writes are
 * best-effort and never throw (quota / private browsing degrade to "no
 * snapshot next run"). Every function takes an injectable storage so the
 * suite runs in plain Node.
 */

import { safeStorage } from './defaultTarget';
import type { TrialSheet } from '../types';

/** Namespaced per trial: `plottR-sheet-snapshot:<trialId>`. */
const STORAGE_KEY_PREFIX = 'plottR-sheet-snapshot:';

/**
 * Hard cap on the serialized snapshot (bytes). The real payload is ~26 kB, so
 * this only trips on a corrupt/hostile payload — a snapshot is never allowed
 * to eat a meaningful slice of the ~5 MB localStorage budget.
 */
export const MAX_SNAPSHOT_BYTES = 200 * 1024;

/** Where a stored payload came from when it was saved. */
export type SheetSnapshotSource = 'network' | 'cache';

/** Envelope persisted under one trial's key. */
export type SheetSnapshot = {
  trialId: string;
  /** ISO-8601 timestamp of the write. */
  savedAt: string;
  source: SheetSnapshotSource;
  /** The last-good full payload. */
  sheet: TrialSheet;
};

/**
 * Storage surface this module needs. `removeItem` is optional so the default
 * (`safeStorage()`'s non-DOM null-store, which only implements get/set) still
 * type-checks; `deleteSheetSnapshot` degrades to a no-op without it.
 */
type SnapshotStorage = Pick<Storage, 'getItem' | 'setItem'> &
  Partial<Pick<Storage, 'removeItem'>>;

/** Derives the storage key for a trial. `trialId` is trusted to be a string. */
export function sheetSnapshotKey(trialId: string): string {
  return `${STORAGE_KEY_PREFIX}${trialId}`;
}

/** UTF-8 byte length — the cap is a byte cap, not a character cap. */
function byteLength(raw: string): number {
  return new TextEncoder().encode(raw).length;
}

function isIsoTimestamp(value: unknown): value is string {
  return typeof value === 'string' && !Number.isNaN(Date.parse(value));
}

/**
 * Reads the stored snapshot for a trial, or null when absent, corrupt,
 * oversize, or written for a different trial. Never throws.
 */
export function readSheetSnapshot(
  trialId: string,
  storage: SnapshotStorage = safeStorage(),
): SheetSnapshot | null {
  try {
    const raw = storage.getItem(sheetSnapshotKey(trialId));
    if (raw === null || raw.length === 0) return null;
    if (byteLength(raw) > MAX_SNAPSHOT_BYTES) return null;

    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return null;

    const envelope = parsed as Partial<SheetSnapshot>;
    // Cross-trial guard: a foreign/mismatched envelope is never trusted, even
    // if it somehow ended up under this trial's key.
    if (envelope.trialId !== trialId) return null;
    if (!isIsoTimestamp(envelope.savedAt)) return null;
    if (envelope.source !== 'network' && envelope.source !== 'cache') return null;
    const sheet = envelope.sheet;
    if (typeof sheet !== 'object' || sheet === null) return null;
    // Structural guard: the hook seeds `versionRef` from `sheet.version`, and
    // `undefined !== null` is true — a versionless envelope would make a later
    // failure look like "payload on screen" and misclassify as `offline`.
    // `columns`/`rows` must be arrays for the same reason (they are
    // non-null in every snapshot we ever write).
    if (typeof sheet.version !== 'string') return null;
    if (!Array.isArray(sheet.columns) || !Array.isArray(sheet.rows)) return null;

    return {
      trialId,
      savedAt: envelope.savedAt,
      source: envelope.source,
      sheet: sheet as TrialSheet,
    };
  } catch {
    return null; // corrupted JSON or a throwing storage — treat as unset
  }
}

/**
 * Persists the last-good payload for a trial (write-through: immediately
 * readable by `readSheetSnapshot`). Returns true when it was stored; false
 * when it was skipped (oversize) or storage failed. Never throws.
 */
export function writeSheetSnapshot(
  trialId: string,
  sheet: TrialSheet,
  source: SheetSnapshotSource,
  storage: SnapshotStorage = safeStorage(),
): boolean {
  const envelope: SheetSnapshot = {
    trialId,
    savedAt: new Date().toISOString(),
    source,
    sheet,
  };

  let raw: string;
  try {
    raw = JSON.stringify(envelope);
  } catch {
    return false; // non-serializable payload (circular refs) — skip
  }
  if (byteLength(raw) > MAX_SNAPSHOT_BYTES) return false;

  try {
    storage.setItem(sheetSnapshotKey(trialId), raw);
    return true;
  } catch {
    return false; // quota exceeded / private browsing — degrade silently
  }
}

/** Removes the stored snapshot for a trial. Never throws. */
export function deleteSheetSnapshot(
  trialId: string,
  storage: SnapshotStorage = safeStorage(),
): void {
  try {
    storage.removeItem?.(sheetSnapshotKey(trialId));
  } catch {
    // A throwing storage has nothing left to forget.
  }
}
