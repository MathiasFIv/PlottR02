/**
 * Per-trial "last-written" target memory. Holds the pair AS WRITTEN, never a
 * computed next pair — the next target is derived from the sheet payload at
 * read time (`lib/nextTarget.ts`). Legacy entries lacking `blockFull` read as
 * null; the storage key is `plottR-default-target:<trialId>`.
 */

const STORAGE_KEY_PREFIX = 'plottR-default-target:';

export interface DefaultTarget {
  plot: number;
  subsample: number;
}

/** Last-written pair plus whether that write filled its category block. */
export interface DefaultTargetMemory extends DefaultTarget {
  blockFull: boolean;
}

function keyForTrial(trialId: string): string {
  return `${STORAGE_KEY_PREFIX}${trialId}`;
}

/** Null-store used when `window` is absent, so reads behave as "nothing stored". */
const NOOP_STORAGE: Pick<Storage, 'getItem' | 'setItem'> = {
  getItem: () => null,
  setItem: () => {},
};

/** Browser localStorage, guarded so the module is importable under Node tests. */
export function safeStorage(): Pick<Storage, 'getItem' | 'setItem'> {
  return typeof window !== 'undefined' && window.localStorage != null
    ? window.localStorage
    : NOOP_STORAGE;
}

function isPositiveInt(n: unknown): n is number {
  return typeof n === 'number' && Number.isInteger(n) && n > 0;
}

/** Reads the stored memory; null when absent, corrupt, legacy (no `blockFull`) or unreadable. Never throws. */
export function readDefaultTarget(
  trialId: string,
  storage: Pick<Storage, 'getItem' | 'setItem'> = safeStorage(),
): DefaultTargetMemory | null {
  try {
    const raw = storage.getItem(keyForTrial(trialId));
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return null;
    const { plot, subsample, blockFull } = parsed as {
      plot?: unknown;
      subsample?: unknown;
      blockFull?: unknown;
    };
    if (!isPositiveInt(plot) || !isPositiveInt(subsample)) return null;
    if (typeof blockFull !== 'boolean') return null;
    return { plot, subsample, blockFull };
  } catch {
    return null;
  }
}

/** Persists the memory; no-ops on invalid input and never throws. */
export function writeDefaultTarget(
  trialId: string,
  target: DefaultTargetMemory,
  storage: Pick<Storage, 'getItem' | 'setItem'> = safeStorage(),
): void {
  if (!isPositiveInt(target.plot) || !isPositiveInt(target.subsample)) return;
  if (typeof target.blockFull !== 'boolean') return;
  try {
    storage.setItem(keyForTrial(trialId), JSON.stringify(target));
  } catch {
    // Storage full/unavailable — degrade silently.
  }
}

/** Stores the resolved result pair AS WRITTEN plus `blockFull` — never a +1 pair. */
export function rememberWrittenTarget(
  trialId: string,
  result: { plot?: number | null; subsample?: number | null; blockFull?: boolean },
  storage: Pick<Storage, 'getItem' | 'setItem'> = safeStorage(),
): void {
  if (!isPositiveInt(result.plot) || !isPositiveInt(result.subsample)) return;
  if (typeof result.blockFull !== 'boolean') return;
  writeDefaultTarget(
    trialId,
    { plot: result.plot, subsample: result.subsample, blockFull: result.blockFull },
    storage,
  );
}