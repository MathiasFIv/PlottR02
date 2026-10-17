import { describe, expect, it } from 'vitest';
import {
  DOUBLE_TAP_WINDOW_MS,
  decideCellTap,
  emptyTapTracker,
} from './cellEditGesture';
import type { SheetCellTap } from './sheetAnchor';

const cellA: SheetCellTap = { plot: 101, subsample: 2, valuesIndex: 0 };
const cellB: SheetCellTap = { plot: 101, subsample: 2, valuesIndex: 1 };
const otherRow: SheetCellTap = { plot: 102, subsample: 2, valuesIndex: 0 };

describe('emptyTapTracker', () => {
  it('starts with no previous tap, so the first tap can never be a double', () => {
    const { decision } = decideCellTap(emptyTapTracker(), cellA, 1_000);
    expect(decision.kind).toBe('select');
  });
});

describe('decideCellTap', () => {
  it('treats the first tap as a select and records it', () => {
    const { decision, tracker } = decideCellTap(emptyTapTracker(), cellA, 1_000);
    expect(decision).toEqual({ kind: 'select', cell: cellA });
    expect(tracker).toEqual({ lastCell: cellA, lastAt: 1_000 });
  });

  it('treats a second tap on the SAME cell within the window as an edit', () => {
    const first = decideCellTap(emptyTapTracker(), cellA, 1_000);
    const second = decideCellTap(
      first.tracker,
      cellA,
      1_000 + DOUBLE_TAP_WINDOW_MS,
    );
    expect(second.decision).toEqual({ kind: 'edit', cell: cellA });
  });

  it('resets the tracker after an edit, so a third tap starts a fresh pair', () => {
    const first = decideCellTap(emptyTapTracker(), cellA, 1_000);
    const second = decideCellTap(first.tracker, cellA, 1_050);
    expect(second.decision.kind).toBe('edit');
    expect(second.tracker).toEqual(emptyTapTracker());

    // Third tap against the reset tracker → a plain select again.
    const third = decideCellTap(second.tracker, cellA, 1_060);
    expect(third.decision.kind).toBe('select');
  });

  it('does NOT count a tap after the window as a double', () => {
    const first = decideCellTap(emptyTapTracker(), cellA, 1_000);
    const late = decideCellTap(
      first.tracker,
      cellA,
      1_000 + DOUBLE_TAP_WINDOW_MS + 1,
    );
    expect(late.decision.kind).toBe('select');
    expect(late.tracker).toEqual({ lastCell: cellA, lastAt: 1_251 });
  });

  it('boundary: EXACTLY the window is still a double', () => {
    const first = decideCellTap(emptyTapTracker(), cellA, 0);
    const edge = decideCellTap(first.tracker, cellA, DOUBLE_TAP_WINDOW_MS);
    expect(edge.decision.kind).toBe('edit');
  });

  it('treats a tap on a DIFFERENT cell as a select, and re-bases the tracker', () => {
    const first = decideCellTap(emptyTapTracker(), cellA, 1_000);
    const other = decideCellTap(first.tracker, cellB, 1_050);
    expect(other.decision).toEqual({ kind: 'select', cell: cellB });
    expect(other.tracker).toEqual({ lastCell: cellB, lastAt: 1_050 });

    // ...so the second tap on cellB is the edit, not the tap on cellA.
    const edit = decideCellTap(other.tracker, cellB, 1_090);
    expect(edit.decision.kind).toBe('edit');
  });

  it('treats a tap on a different ROW as a select even at the same column', () => {
    const first = decideCellTap(emptyTapTracker(), cellA, 1_000);
    const other = decideCellTap(first.tracker, otherRow, 1_020);
    expect(other.decision.kind).toBe('select');
  });

  it('never mutates the tracker it is given', () => {
    const tracker = emptyTapTracker();
    const snapshot = { ...tracker };
    decideCellTap(tracker, cellA, 500);
    expect(tracker).toEqual(snapshot);
  });

  it('ignores a backwards clock instead of misreading a double', () => {
    const first = decideCellTap(emptyTapTracker(), cellA, 1_000);
    const backwards = decideCellTap(first.tracker, cellA, 900);
    expect(backwards.decision.kind).toBe('select');
  });
});
