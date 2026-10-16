import { describe, expect, it } from 'vitest';
import type { SheetSelected } from '../store/atoms';
import {
  anchorToWire,
  decideAnchorTap,
  formatAnchor,
  isAnchorForTrial,
} from './sheetAnchor';
import type { SheetCellTap } from './sheetAnchor';

/** A tapped ROW on trial-1: plot 101, subsample 2. Row-only — no column. */
const anchor: SheetSelected = {
  trialId: 'trial-1',
  plot: 101,
  subsample: 2,
};

const otherTrialAnchor: SheetSelected = {
  trialId: 'trial-2',
  plot: 101,
  subsample: 2,
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
  it('formats as `Plot <n> · Sub <n>` with a single U+00B7 MIDDLE DOT', () => {
    const text = formatAnchor(anchor);
    expect(text).toBe('Plot 101 \u00B7 Sub 2');

    // Pin the exact codepoint (0x00B7), not just a lookalike glyph. Layout:
    // "Plot " 0..4 · "101" 5..7 · " " 8 · "·" 9 · " Sub " 10..14 · "2" 15.
    expect(text.length).toBe(16);
    expect(text.charCodeAt(9)).toBe(0x00b7);
    // A hyphen was the rejected option — assert neither it nor dash lookalikes
    // can sneak in.
    expect(text).not.toContain('-');
    expect(text).not.toContain('\u2013'); // en dash lookalike
    expect(text).not.toContain('\u2014'); // em dash lookalike
    // Encoding-safe: the ONLY non-ASCII codepoint allowed anywhere in the
    // string is U+00B7 (no mojibake, no lookalike separators).
    expect(
      [...text].every((ch) => {
        const code = ch.codePointAt(0) ?? 0;
        return code < 0x80 || code === 0x00b7;
      }),
    ).toBe(true);
  });

  it('formats single-digit and multi-digit coordinates verbatim (no padding)', () => {
    expect(
      formatAnchor({ trialId: 'trial-1', plot: 7, subsample: 12 }),
    ).toBe('Plot 7 \u00B7 Sub 12');
    expect(
      formatAnchor({ trialId: 'trial-1', plot: 414, subsample: 1 }),
    ).toBe('Plot 414 \u00B7 Sub 1');
  });
});

describe('anchorToWire', () => {
  it('maps the tap onto { defaultTarget } ONLY — no traitOrdinal', () => {
    expect(anchorToWire(anchor)).toEqual({
      defaultTarget: { plot: 101, subsample: 2 },
    });
    // The wire mapping is row-only: it never carries a column pin, because
    // the server scans from column C for the row's first empty cell.
    expect(Object.keys(anchorToWire(anchor))).toEqual(['defaultTarget']);
    expect('traitOrdinal' in anchorToWire(anchor)).toBe(false);
  });

  it('composes onto a values-only record exactly the way syncQueue freezes it', () => {
    // Mirrors `lib/syncQueue.ts`: a values-only row (no spoken plot/
    // subsample) spread with the wire mapping. `defaultTarget` is what
    // `lib/api.ts` turns into defaultPlot/defaultSubsample — the default
    // chain ("use this when the transcript omits it"), which is also what
    // keeps `usedDefaultTarget` true and the confidence review gate relaxed.
    const rec = { plot: null, subsample: null, traitOrdinal: 7 };
    const sendRec = { ...rec, ...anchorToWire(anchor) };

    expect(sendRec.defaultTarget).toEqual({ plot: 101, subsample: 2 });
    // The tap is ROW-ONLY: it never overrides the recorder's own trait
    // selector — the rec keeps its own traitOrdinal (7).
    expect(sendRec.traitOrdinal).toBe(7);
    expect('traitOrdinal' in anchorToWire(anchor)).toBe(false);
    // The key is PRESENT, so api.ts's `'defaultTarget' in rec` gate ships it
    // (and a frozen null would stay null — presence is what decides).
    expect('defaultTarget' in sendRec).toBe(true);
    // Spoken fields are untouched: the tap never claims a confirmed plot.
    expect(sendRec.plot).toBeNull();
    expect(sendRec.subsample).toBeNull();
  });

  it('a record WITHOUT its own traitOrdinal gains none from the tap', () => {
    const rec: { plot: null; subsample: null } = { plot: null, subsample: null };
    const sendRec = { ...rec, ...anchorToWire(anchor) };
    expect('traitOrdinal' in sendRec).toBe(false);
    expect(sendRec).toEqual({ plot: null, subsample: null, defaultTarget: { plot: 101, subsample: 2 } });
  });
});

describe('decideAnchorTap', () => {
  /** The very row `anchor` was tapped on — trial-1, 101/2. */
  const anchoredRow: SheetCellTap = { plot: 101, subsample: 2 };

  it('a row MATCHING the current anchor → clear (tap the selected row again)', () => {
    expect(decideAnchorTap(anchoredRow, anchor, 'trial-1')).toEqual({
      kind: 'clear',
    });
  });

  it('a DIFFERENT row → set with the exact row-only SheetSelected', () => {
    const decision = decideAnchorTap({ plot: 202, subsample: 1 }, anchor, 'trial-1');
    expect(decision).toEqual({
      kind: 'set',
      anchor: { trialId: 'trial-1', plot: 202, subsample: 1 },
    });
  });

  it('a row with NO current anchor → set', () => {
    expect(decideAnchorTap(anchoredRow, null, 'trial-1')).toEqual({
      kind: 'set',
      anchor: { trialId: 'trial-1', plot: 101, subsample: 2 },
    });
  });

  it('same plot but a different subsample is a DIFFERENT row → set', () => {
    expect(decideAnchorTap({ plot: 101, subsample: 3 }, anchor, 'trial-1')).toEqual({
      kind: 'set',
      anchor: { trialId: 'trial-1', plot: 101, subsample: 3 },
    });
  });

  it('the decision carries the CALLER\'s trialId, not the current anchor\'s', () => {
    // The host always passes its own (guarded) trial; the pure decision
    // simply stamps it on. A cross-trial `current` never reaches here
    // (isAnchorForTrial guards it), but the stamp is the caller's trial.
    expect(decideAnchorTap({ plot: 5, subsample: 1 }, anchor, 'trial-9')).toEqual({
      kind: 'set',
      anchor: { trialId: 'trial-9', plot: 5, subsample: 1 },
    });
  });
});
