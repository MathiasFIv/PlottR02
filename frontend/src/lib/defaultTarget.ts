/**
 * Remembered **last-written** target ("plot/subsample default memory"), keyed
 * per trial.
 *
 * The store holds the target of the LAST successful write — exactly the
 * `result.plot` / `result.subsample` the server resolved — plus `blockFull`
 * (whether that write filled the category block it landed in), and NEVER a
 * computed "next" pair. The next target is derived at READ time from the
 * sheet payload (`lib/nextTarget.ts`): STAY on this subsample while the block
 * is unfilled (append more values to the same block), advance to the next
 * subsample of the SAME plot once it is full — and only within the same plot;
 * at the end of a plot there is no next target (no wrap, no cross-plot
 * advance). The old `subsample + 1` arithmetic is likewise never used here.
 *
 * The storage key (`plottR-default-target:<trialId>`) is unchanged, so a
 * legacy stored pair needs no migration: it lacks `blockFull`, reads as null,
 * and derives no next target until the next write lands.
 *
 * When the operator says "plot 101, subsample 1, values 3, 6, 1" and then
 * only "values 4, 2, 6", the app reuses the last written target to compute
 * where the run continues. Explicitly saying a plot/subsample again is the
 * only way to re-append to an already-done subsample — **spoken values always
 * win over these defaults**, both here (the store is only a fallback) and on
 * the server (it substitutes `defaultPlot`/`defaultSubsample` only when the
 * transcript named no plot: `defaultTarget.ts` wire contract below).
 *
 * The store is namespaced by trial (`plottR-default-target:<trialId>`): a
 * values-only utterance on a DIFFERENT trial must never silently apply the
 * previous trial's cursor — plot numbers repeat freely across workbooks.
 *
 * The wire contract: `POST /api/Recordings/sync` accepts the OPTIONAL
 * `defaultPlot` / `defaultSubsample` multipart fields; the server substitutes
 * them only when the transcript/confirmed plot/subsample is absent (guarded so
 * a default subsample applies only when no subsample was said AND either no
 * plot was said or the said plot equals the default plot). The sync response
 * reports `usedDefaultTarget` and its resolved `plot`/`subsample`, which is
 * what this store reads back **as-is** (`rememberWrittenTarget`) — the sheet
 * payload then supplies the next pair at read time.
 *
 * Dependency-injectable: every function takes an optional storage
 * (`Pick<Storage, 'getItem' | 'setItem'>`) and defaults to a guarded
 * `window.localStorage` accessor that degrades to a throwaway null-store when
 * there is no DOM — so importing this module never crashes in Node (vitest
 * runs the unit suite in plain Node, no jsdom).
 */

/** Namespaced per trial: `plottR-default-target:<trialId>`. */
const STORAGE_KEY_PREFIX = 'plottR-default-target:';

/** Shape stored under the trial key — both fields are positive integers. */
export interface DefaultTarget {
  plot: number;
  subsample: number;
}

/**
 * What the store actually persists: the last-written pair PLUS whether that
 * write filled the category block it landed in (`blockFull`). The flag is the
 * STAY/advance switch for `deriveNextTarget` — unfilled → stay on the same
 * subsample (append more values to the same block), filled → advance to the
 * next subsample of the SAME plot. Legacy stored pairs (no `blockFull`) read
 * as null until the next write re-seeds them.
 */
export interface DefaultTargetMemory extends DefaultTarget {
  blockFull: boolean;
}

/** Derives the storage key for a trial. `trialId` is trusted to be a string. */
function keyForTrial(trialId: string): string {
  return `${STORAGE_KEY_PREFIX}${trialId}`;
}

/**
 * A null-store that never throws and never retains anything. Used when
 * `window` is absent (Node tests, SSR) so reads behave as "nothing stored".
 */
const NOOP_STORAGE: Pick<Storage, 'getItem' | 'setItem'> = {
  getItem: () => null,
  setItem: () => {},
};

/**
 * Browser-localStorage accessor guarded for non-DOM environments. Mirrors the
 * codebase's localStorage use (see `lib/tokens.ts`) but wrapped so the module
 * is importable in tests running in Node.
 */
export function safeStorage(): Pick<Storage, 'getItem' | 'setItem'> {
  return typeof window !== 'undefined' && window.localStorage != null
    ? window.localStorage
    : NOOP_STORAGE;
}

/** True when `n` is an integer > 0. Everything else is untrusted input. */
function isPositiveInt(n: unknown): n is number {
  return typeof n === 'number' && Number.isInteger(n) && n > 0;
}

/**
 * Reads the last-written memory for a trial, or null when absent,
 * corrupted, or invalid (storage access failures also degrade to null) —
 * including LEGACY entries that lack `blockFull` (they read as null rather
 * than guessing a flag). Never throws. The NEXT target is derived from this
 * memory plus the sheet payload (`lib/nextTarget.ts`) — never by incrementing
 * it here.
 */
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
    if (typeof blockFull !== 'boolean') return null; // legacy pair — no flag
    return { plot, subsample, blockFull };
  } catch {
    return null; // corrupted JSON or a throwing storage — treat as unset
  }
}

/**
 * Persists the last-written memory for a trial. No-ops unless both fields are
 * positive integers and `blockFull` is a boolean, so garbage can never be
 * written. Never throws.
 */
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
    // Storage full/unavailable (private browsing, quota) — degrade silently;
    // the feature simply forgets on the next run.
  }
}

/**
 * Remembers what was JUST written for a trial: stores the resolved result
 * pair AS WRITTEN plus `blockFull` — `rememberWrittenTarget('t', { plot: 101,
 * subsample: 4, blockFull: true })` stores exactly that. There is
 * deliberately NO `+ 1`: the "next" target is derived from the sheet payload
 * at read time (`deriveNextTarget` in `lib/nextTarget.ts`), which is the only
 * way to know whether the workbook actually has a following subsample in the
 * SAME plot (and whether the block is now full). The caller passes the
 * resolved result fields (the server's `plot`/`subsample` on a `Written`
 * response) plus the computed flag; no-ops unless both fields are positive
 * integers and `blockFull` is a boolean.
 */
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