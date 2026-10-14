import { describe, expect, it } from 'vitest';
import { nextTick, shouldConsumeTick } from './sheetRefreshTick';

/**
 * Pins the two pure rules behind recording-driven live sheet refresh: the
 * signal only ever grows (so a consumer can tell "fresh" from "already seen"
 * by seq alone), and only a fresh tick targeting the CURRENT trial may drive
 * a fetch — an unknown/other/null trial is never a reason to hit the sheet.
 */
describe('nextTick', () => {
  it('starts at seq 1 from a null previous tick', () => {
    expect(nextTick(null, 'trial-1')).toEqual({ seq: 1, trialId: 'trial-1' });
  });

  it('increments the seq on every bump', () => {
    const first = nextTick(null, 'trial-1');
    const second = nextTick(first, 'trial-1');
    const third = nextTick(second, 'trial-1');
    expect(second.seq).toBe(2);
    expect(third.seq).toBe(3);
  });

  it('carries the trial id it was called with (retargets, never merges)', () => {
    const first = nextTick(null, 'trial-1');
    expect(nextTick(first, 'trial-2')).toEqual({ seq: 2, trialId: 'trial-2' });
  });

  it('never mutates the previous tick object', () => {
    const first = nextTick(null, 'trial-1');
    nextTick(first, 'trial-2');
    expect(first).toEqual({ seq: 1, trialId: 'trial-1' });
  });
});

describe('shouldConsumeTick', () => {
  it('is false for a null tick (nothing to consume)', () => {
    expect(shouldConsumeTick(null, null, 'trial-1')).toBe(false);
    expect(shouldConsumeTick(null, 3, 'trial-1')).toBe(false);
  });

  it('is false when the seq was already seen', () => {
    expect(shouldConsumeTick({ seq: 4, trialId: 'trial-1' }, 4, 'trial-1')).toBe(
      false,
    );
  });

  it('is false when the current trial id is null (no sheet mounted)', () => {
    expect(shouldConsumeTick({ seq: 4, trialId: 'trial-1' }, null, null)).toBe(
      false,
    );
  });

  it('is false when the tick targets a different trial', () => {
    expect(shouldConsumeTick({ seq: 4, trialId: 'trial-2' }, null, 'trial-1')).toBe(
      false,
    );
  });

  it('is true for a fresh tick targeting the mounted trial', () => {
    expect(shouldConsumeTick({ seq: 4, trialId: 'trial-1' }, 3, 'trial-1')).toBe(
      true,
    );
    expect(shouldConsumeTick({ seq: 4, trialId: 'trial-1' }, null, 'trial-1')).toBe(
      true,
    );
  });
});
