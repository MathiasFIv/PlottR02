import { describe, expect, it } from 'vitest';
import type { SheetSelected } from '../store/atoms';
import {
  anchorToWire,
  decideAnchorTap,
  formatAnchor,
  isAnchorForTrial,
} from './sheetAnchor';
import type { SheetCellTap } from './sheetAnchor';

/** A tapped cell on trial-1: row 101/2, column traitOrdinal 3 (values[2]). */
const anchor: SheetSelected = {
  trialId: 'trial-1',
  plot: 101,
  subsample: 2,
  traitOrdinal: 3,
  valuesIndex: 2,
};

const otherTrialAnchor: SheetSelected = {
  trialId: 'trial-2',
  plot: 101,
  subsample: 2,
  traitOrdinal: 3,
  valuesIndex: 2,
};

describe('isAnchorForTrial', () => {
  it('returns the anchor when the trial matches', () => {
    expect(isAnchorForTrial(anchor, 'trial-1')).toBe(anchor);
  });

  it('returns null when the trial differs — plot numbers repeat across workbooks', () => {
    expect(isAnchorForTrial(anchor, 'trial-2')).toBeNull();
    expect(isAnchorForTrial(otherTrialAnchor, 'trial-1')).toBeNull();
  });

  it('returns null when either side is null', () => {
    expect(isAnchorForTrial(null, 'trial-1')).toBeNull();
    expect(isAnchorForTrial(anchor, null)).toBeNull();
    expect(isAnchorForTrial(null, null)).toBeNull();
  });
});

describe('formatAnchor', () => {
  it('formats as `Plot <n> · Sub <n> · Col <n>` with U+00B7 MIDDLE DOT separators', () => {
    const text = formatAnchor(anchor);
    expect(text).toBe('Plot 101 \u00B7 Sub 2 \u00B7 Col 3');

    // Pin the exact codepoint (0x00B7), not just a lookalike glyph: index 9
    // is the first separator, index 17 the second ('Plot 101' = 0..7).
    expect(text.charCodeAt(9)).toBe(0x00b7);
    expect(text.charCodeAt(17)).toBe(0x00b7);
    // A hyphen was the rejected option — assert it can never sneak in.
    expect(text).not.toContain('-');
    expect(text).not.toContain('\u2013'); // en dash lookalike
    // Encoding-safe: the ONLY non-ASCII codepoint allowed anywhere in the
    // string is U+00B7 (no mojibake, no lookalike separators).
    expect(
      [...text].every((ch) => {
        const code = ch.codePointAt(0) ?? 0;
        return code < 0x80 || code === 0x00b7;
      }),
    ).toBe(true);
  });

  it('formats single-digit and multi-digit coordinates verbatim', () => {
    expect(
      formatAnchor({
        trialId: 'trial-1',
        plot: 7,
        subsample: 12,
        traitOrdinal: 9,
        valuesIndex: 8,
      }),
    ).toBe('Plot 7 \u00B7 Sub 12 \u00B7 Col 9');
  });
});

describe('anchorToWire', () => {
  it('maps the tap onto { defaultTarget, traitOrdinal } — the syncQueue freeze shape', () => {
    expect(anchorToWire(anchor)).toEqual({
      defaultTarget: { plot: 101, subsample: 2 },
      traitOrdinal: 3,
    });
  });

  it('composes onto a values-only record exactly the way syncQueue freezes it', () => {
    // Mirrors `lib/syncQueue.ts`: a values-only row (no spoken plot/
    // subsample) spread with the wire mapping. `defaultTarget` is what
    // `lib/api.ts` turns into defaultPlot/defaultSubsample — the default
    // chain ("use this when the transcript omits it"), which is also what
    // keeps `usedDefaultTarget` true and the confidence review gate relaxed.
    const rec = { plot: null, subsample: null, traitOrdinal: 9 };
    const sendRec = { ...rec, ...anchorToWire(anchor) };

    expect(sendRec.defaultTarget).toEqual({ plot: 101, subsample: 2 });
    // The tapped column wins over the recorder's trait selector (9 → 3).
    expect(sendRec.traitOrdinal).toBe(3);
    // The key is PRESENT, so api.ts's `'defaultTarget' in rec` gate ships it
    // (and a frozen null would stay null — presence is what decides).
    expect('defaultTarget' in sendRec).toBe(true);
    // Spoken fields are untouched: the tap never claims a confirmed plot.
    expect(sendRec.plot).toBeNull();
    expect(sendRec.subsample).toBeNull();
  });
});

describe('decideAnchorTap', () => {
  /** The very cell `anchor` was tapped on — empty, trial-1, row 101/2 col 3. */
  const anchoredCell: SheetCellTap = {
    plot: 101,
    subsample: 2,
    traitOrdinal: 3,
    valuesIndex: 2,
    value: null,
  };

  it('a FILLED cell is refused with NO anchor change — even when a valid current anchor exists', () => {
    // Regression guard (owner decision: "refuse"): the old behaviour cleared
    // the anchor here, so one stray tap on a filled cell silently destroyed
    // a perfectly valid anchor. The decision carries NO anchor payload, so
    // the host has nothing to write to `sheetSelectedAtom`.
    expect(
      decideAnchorTap({ ...anchoredCell, value: 4.2 }, anchor, 'trial-1'),
    ).toEqual({ kind: 'refuse-filled' });

    // Same refusal when there is no anchor at all (nothing to lose, still
    // no state change) and for a filled cell the anchor does NOT point at.
    expect(decideAnchorTap({ ...anchoredCell, value: 0 }, null, 'trial-1'))
      .toEqual({ kind: 'refuse-filled' });
    expect(
      decideAnchorTap(
        { ...anchoredCell, plot: 999, value: 1.5 },
        anchor,
        'trial-1',
      ),
    ).toEqual({ kind: 'refuse-filled' });
  });

  it('an empty cell MATCHING the current anchor → clear (tap the selected cell again)', () => {
    expect(decideAnchorTap(anchoredCell, anchor, 'trial-1')).toEqual({
      kind: 'clear',
    });
    // Identity is plot/subsample/traitOrdinal only — valuesIndex is derived
    // from the same column and never participates in the comparison.
    expect(
      decideAnchorTap(
        { ...anchoredCell, valuesIndex: 99 },
        anchor,
        'trial-1',
      ),
    ).toEqual({ kind: 'clear' });
  });

  it('an empty cell DIFFERING from the current anchor → set with the exact SheetSelected', () => {
    const decision = decideAnchorTap(
      { plot: 202, subsample: 1, traitOrdinal: 5, valuesIndex: 4, value: null },
      anchor,
      'trial-1',
    );
    expect(decision).toEqual({
      kind: 'set',
      anchor: {
        trialId: 'trial-1',
        plot: 202,
        subsample: 1,
        traitOrdinal: 5,
        valuesIndex: 4,
      },
    });
  });

  it('an empty cell with NO current anchor → set', () => {
    expect(decideAnchorTap(anchoredCell, null, 'trial-1')).toEqual({
      kind: 'set',
      anchor: {
        trialId: 'trial-1',
        plot: 101,
        subsample: 2,
        traitOrdinal: 3,
        valuesIndex: 2,
      },
    });
  });
});
