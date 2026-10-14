/**
 * Pure rules for the recording-driven sheet refresh signal
 * (`sheetRefreshTickAtom` in `store/atoms.ts`).
 *
 * **A tick is a check, not a claim.** The sync engine bumps it whenever it
 * contacts the server for a send attempt (2xx with or without cells written,
 * a failed send, a later sweep replay, any correction outcome) — and the
 * mounted sheet host answers with one `GET .../sheet`. That sheet's
 * `changed:false` fast path (~100 B) leaves the payload atoms untouched, so a
 * no-op outcome can never fabricate a change; bumping on "the server was
 * asked" is therefore always safe, while local early returns (unknown id,
 * in-flight guard) never bump because no question was asked.
 *
 * No store access, no timers, no listeners — both functions are pure so the
 * trigger and consume rules are unit-testable under the node-only house setup.
 */

import type { SheetRefreshTick } from '../store/atoms';

/**
 * The next tick for `trialId`: `null` → `{seq:1, trialId}`, otherwise the
 * previous seq + 1. The trial id is (re)written on every bump — a tick always
 * targets the trial of the send attempt that produced it.
 */
export function nextTick(
  prev: SheetRefreshTick | null,
  trialId: string,
): SheetRefreshTick {
  if (prev === null) return { seq: 1, trialId };
  return { seq: prev.seq + 1, trialId };
}

/**
 * True only for a fresh tick targeting the currently mounted trial.
 * False when there is no tick, when the seq was already seen (consumed), or
 * when the trial id is null/mismatched. The consumer evaluates this from its
 * PRE-consumption state and then spends the seq regardless, so a tick for
 * another trial can never replay later — see the effect in
 * `hooks/useTrialSheet.ts`.
 */
export function shouldConsumeTick(
  tick: SheetRefreshTick | null,
  seenSeq: number | null,
  trialId: string | null,
): boolean {
  if (tick === null) return false;
  if (trialId === null) return false;
  if (seenSeq !== null && tick.seq === seenSeq) return false;
  if (tick.trialId !== trialId) return false;
  return true;
}
