/**
 * Sheet refresh signal — the non-React writer for `sheetRefreshTickAtom`.
 *
 * Extracted from `lib/syncQueue.ts` so the inline cell editor
 * (`hooks/useSheetCellEdit.ts`) can signal a re-check WITHOUT importing the
 * whole sync engine (audioDb / IndexedDB / the voice pipeline) into the sheet
 * components. The rule is unchanged: bump ONLY where the server was actually
 * contacted for a write attempt — a tick is a *check*, not a claim, and the
 * sheet's `changed:false` fast path answers without touching the payload
 * atoms, so a no-op outcome can never fabricate a change. A null or empty
 * trial id has no sheet to refresh, so it never bumps.
 *
 * Same non-React write path as `refreshAttentionCount` in `syncQueue.ts` —
 * the fixed `<Provider store={getDefaultStore()}>` in `main.tsx` makes these
 * writes reach React; `useTrialSheet` is the single observer.
 */

import { getDefaultStore } from 'jotai/vanilla';
import { sheetRefreshTickAtom } from '../store/atoms';
import { nextTick } from './sheetRefreshTick';

export function bumpSheetRefresh(trialId: string | null): void {
  if (trialId === null || trialId === '') return;
  const store = getDefaultStore();
  store.set(sheetRefreshTickAtom, nextTick(store.get(sheetRefreshTickAtom), trialId));
}
