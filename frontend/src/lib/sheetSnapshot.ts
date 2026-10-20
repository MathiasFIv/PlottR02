/**
 * Persisted offline snapshot of the last-good sheet payload, in localStorage
 * via `safeStorage()`. Namespaced per trial (`plottR-sheet-snapshot:<trialId>`).
 * Corrupt, foreign-trial, structurally broken or oversize payloads read back as
 * null; writes are best-effort and never throw.
 */

import { safeStorage } from './defaultTarget';
import type { TrialSheet } from '../types';

const STORAGE_KEY_PREFIX = 'plottR-sheet-snapshot:';

/** Byte cap; the real payload is ~26 kB, so this only trips on corrupt/hostile data. */
export const MAX_SNAPSHOT_BYTES = 200 * 1024;

export type SheetSnapshotSource = 'network' | 'cache';

export type SheetSnapshot = {
  trialId: string;
  savedAt: string;
  source: SheetSnapshotSource;
  sheet: TrialSheet;
};

/** `removeItem` is optional so the default null-store still type-checks. */
type SnapshotStorage = Pick<Storage, 'getItem' | 'setItem'> &
  Partial<Pick<Storage, 'removeItem'>>;

export function sheetSnapshotKey(trialId: string): string {
  return `${STORAGE_KEY_PREFIX}${trialId}`;
}

function byteLength(raw: string): number {
  return new TextEncoder().encode(raw).length;
}

function isIsoTimestamp(value: unknown): value is string {
  return typeof value === 'string' && !Number.isNaN(Date.parse(value));
}

/** Null when absent, corrupt, oversize, or written for a different trial. Never throws. */
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
    if (envelope.trialId !== trialId) return null;
    if (!isIsoTimestamp(envelope.savedAt)) return null;
    if (envelope.source !== 'network' && envelope.source !== 'cache') return null;
    const sheet = envelope.sheet;
    if (typeof sheet !== 'object' || sheet === null) return null;
    // `version` must be a string and columns/rows arrays: a versionless envelope
    // reads as "payload on screen" and misclassifies the next fetch as offline.
    if (typeof sheet.version !== 'string') return null;
    if (!Array.isArray(sheet.columns) || !Array.isArray(sheet.rows)) return null;

    return {
      trialId,
      savedAt: envelope.savedAt,
      source: envelope.source,
      sheet: sheet as TrialSheet,
    };
  } catch {
    return null;
  }
}

/** Stores the last-good payload; false when skipped (oversize) or storage failed. Never throws. */
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
    return false;
  }
  if (byteLength(raw) > MAX_SNAPSHOT_BYTES) return false;

  try {
    storage.setItem(sheetSnapshotKey(trialId), raw);
    return true;
  } catch {
    return false;
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
    // Nothing left to forget.
  }
}
