/**
 * Remembered **last-written** target ("plot/subsample default memory"), keyed
 * per trial.
 *
 * The store holds the target of the LAST successful write — exactly the
 * `result.plot` / `result.subsample` the server resolved — and NEVER a
 * computed "next" pair. The next target is derived at READ time from the
 * sheet payload (`lib/nextTarget.ts`: the row after the last-written one in
 * `rows[]` order), so a run auto-continues across plot boundaries
 * (`101 4 → 102 1`) and can never invent a pair the workbook does not
 * contain — the old `subsample + 1` produced phantom pairs like `101 5` at
 * the end of a plot, which then highlighted nothing, rendered a lie, and went
 * out on the wire.
 *
 * The storage key (`plottR-default-target:<trialId>`) is unchanged, so a
 * legacy stored pair needs no migration: it simply will not match any row in
 * `rows[]` and derives `null` (no next target) until the next write lands.
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
 * Reads the last-written target for a trial, or null when absent,
 * corrupted, or invalid (storage access failures also degrade to null).
 * Never throws. The NEXT target is derived from this pair plus the sheet
 * payload (`lib/nextTarget.ts`) — never by incrementing it here.
 */
export function readDefaultTarget(
  trialId: string,
  storage: Pick<Storage, 'getItem' | 'setItem'> = safeStorage(),
): DefaultTarget | null {
  try {
    const raw = storage.getItem(keyForTrial(trialId));
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return null;
    const { plot, subsample } = parsed as {
      plot?: unknown;
      subsample?: unknown;
    };
    if (!isPositiveInt(plot) || !isPositiveInt(subsample)) return null;
    return { plot, subsample };
  } catch {
    return null; // corrupted JSON or a throwing storage — treat as unset
  }
}

/**
 * Persists the last-written target for a trial. No-ops unless both fields are
 * positive integers, so garbage can never be written. Never throws.
 */
export function writeDefaultTarget(
  trialId: string,
  target: DefaultTarget,
  storage: Pick<Storage, 'getItem' | 'setItem'> = safeStorage(),
): void {
  if (!isPositiveInt(target.plot) || !isPositiveInt(target.subsample)) return;
  try {
    storage.setItem(keyForTrial(trialId), JSON.stringify(target));
  } catch {
    // Storage full/unavailable (private browsing, quota) — degrade silently;
    // the feature simply forgets on the next run.
  }
}

/**
 * Remembers what was JUST written for a trial: stores the resolved result
 * pair as-is — `rememberWrittenTarget('t', { plot: 101, subsample: 4 })`
 * stores `{ plot: 101, subsample: 4 }`. There is deliberately NO `+ 1`:
 * the "next" target is derived from the sheet payload at read time
 * (`deriveNextTarget` in `lib/nextTarget.ts`), which is the only way to know
 * whether the workbook actually has a following row (and whether it crosses a
 * plot boundary). The caller passes the resolved result fields (the server's
 * `plot`/`subsample` on a `Written` response); no-ops unless both are
 * positive integers.
 */
export function rememberWrittenTarget(
  trialId: string,
  result: { plot?: number | null; subsample?: number | null },
  storage: Pick<Storage, 'getItem' | 'setItem'> = safeStorage(),
): void {
  if (!isPositiveInt(result.plot) || !isPositiveInt(result.subsample)) return;
  writeDefaultTarget(
    trialId,
    { plot: result.plot, subsample: result.subsample },
    storage,
  );
}