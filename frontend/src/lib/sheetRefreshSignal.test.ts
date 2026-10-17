import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { getDefaultStore } from 'jotai/vanilla';
import { sheetRefreshTickAtom } from '../store/atoms';
import { bumpSheetRefresh } from './sheetRefreshSignal';

/**
 * `bumpSheetRefresh` is the only NON-pure piece of the refresh signal (it
 * writes through `getDefaultStore()`), so this pins its two rules: the seq is
 * read from and written back to the SAME store the hosts observe, and a
 * null/empty trial id — nothing to refresh — never bumps.
 */
describe('bumpSheetRefresh', () => {
  const store = getDefaultStore();

  beforeEach(() => {
    store.set(sheetRefreshTickAtom, null);
  });

  afterEach(() => {
    store.set(sheetRefreshTickAtom, null);
  });

  it('bumps the tick for a trial, starting at seq 1 from null', () => {
    bumpSheetRefresh('trial-1');
    expect(store.get(sheetRefreshTickAtom)).toEqual({
      seq: 1,
      trialId: 'trial-1',
    });
  });

  it('increments the seq on each bump and retargets', () => {
    bumpSheetRefresh('trial-1');
    bumpSheetRefresh('trial-1');
    bumpSheetRefresh('trial-2');
    expect(store.get(sheetRefreshTickAtom)).toEqual({
      seq: 3,
      trialId: 'trial-2',
    });
  });

  it('never bumps for a null or empty trial id', () => {
    bumpSheetRefresh(null);
    bumpSheetRefresh('');
    expect(store.get(sheetRefreshTickAtom)).toBeNull();
  });
});
