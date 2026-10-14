import { describe, expect, it } from 'vitest';
import type { SheetColumn } from '../types';
import { buildSheetHeader } from './sheetHeader';

/** Builds a column the way the reader does — every field explicit. */
function column(
  traitOrdinal: number,
  column: string,
  partRated: string | null,
  pestCode: string | null,
): SheetColumn {
  return {
    traitOrdinal,
    column,
    partRated,
    pestCode,
    label: `${pestCode ?? ''} × ${partRated ?? ''}`,
  };
}

/** The frozen 9-column part-major block (cols C..K, 3 parts × 3 pests). */
const partMajor: SheetColumn[] = [
  column(1, 'C', 'LEAF1', 'SEPTTR'),
  column(2, 'D', 'LEAF1', 'ERYSGR'),
  column(3, 'E', 'LEAF1', 'PUCCST'),
  column(4, 'F', 'LEAF2', 'SEPTTR'),
  column(5, 'G', 'LEAF2', 'ERYSGR'),
  column(6, 'H', 'LEAF2', 'PUCCST'),
  column(7, 'I', 'LEAF3', 'SEPTTR'),
  column(8, 'J', 'LEAF3', 'ERYSGR'),
  column(9, 'K', 'LEAF3', 'PUCCST'),
];

describe('buildSheetHeader — tier 1 (Part Rated runs)', () => {
  it('collapses consecutive equal partRated values into three 3-wide runs', () => {
    const { runs } = buildSheetHeader(partMajor);
    expect(runs).toEqual([
      { partRated: 'LEAF1', label: 'LEAF1', span: 3 },
      { partRated: 'LEAF2', label: 'LEAF2', span: 3 },
      { partRated: 'LEAF3', label: 'LEAF3', span: 3 },
    ]);
  });

  it('derives the span from the file — never a hardcoded 3-wide block', () => {
    const { runs } = buildSheetHeader([
      column(1, 'C', 'LEAF1', 'SEPTTR'),
      column(2, 'D', 'LEAF1', 'ERYSGR'), // 2-wide part
      column(3, 'E', 'LEAF2', 'PUCCST'),
      column(4, 'F', 'LEAF3', 'SEPTTR'),
      column(5, 'G', 'LEAF3', 'ERYSGR'),
      column(6, 'H', 'LEAF3', 'PUCCST'),
      column(7, 'I', 'LEAF3', 'SEPTTR'), // 4-wide part
    ]);
    expect(runs.map((run) => run.span)).toEqual([2, 1, 4]);
  });

  it('does not merge equal partRated values that are not adjacent', () => {
    const { runs } = buildSheetHeader([
      column(1, 'C', 'LEAF1', 'SEPTTR'),
      column(2, 'D', 'LEAF2', 'SEPTTR'),
      column(3, 'E', 'LEAF1', 'SEPTTR'),
    ]);
    expect(runs.map((run) => run.partRated)).toEqual(['LEAF1', 'LEAF2', 'LEAF1']);
  });

  it('groups null/blank partRated together and labels them —, never "null"', () => {
    const { runs } = buildSheetHeader([
      column(1, 'C', 'LEAF1', 'SEPTTR'),
      column(2, 'D', null, 'ERYSGR'),
      column(3, 'E', '   ', 'PUCCST'), // blank ≡ null after trim
      column(4, 'F', 'LEAF2', 'SEPTTR'),
    ]);
    expect(runs).toEqual([
      { partRated: 'LEAF1', label: 'LEAF1', span: 1 },
      { partRated: null, label: '—', span: 2 },
      { partRated: 'LEAF2', label: 'LEAF2', span: 1 },
    ]);
  });
});

describe('buildSheetHeader — tier 2 (Pest Code)', () => {
  it('emits one cell per column, index-aligned with row.values', () => {
    const { pests } = buildSheetHeader(partMajor);
    expect(pests).toHaveLength(9);
    expect(pests[0]).toEqual({
      traitOrdinal: 1,
      column: 'C',
      pestCode: 'SEPTTR',
      label: 'SEPTTR',
    });
    // values[i] ↔ columns[i]: the tier-2 order must be the payload order.
    expect(pests.map((pest) => pest.column)).toEqual([
      'C',
      'D',
      'E',
      'F',
      'G',
      'H',
      'I',
      'J',
      'K',
    ]);
  });

  it('trims pest codes and falls back to — for null/blank', () => {
    const { pests } = buildSheetHeader([
      column(1, 'C', 'LEAF1', ' SEPTTR '),
      column(2, 'D', 'LEAF1', null),
      column(3, 'E', 'LEAF1', ''),
    ]);
    expect(pests.map((pest) => pest.label)).toEqual(['SEPTTR', '—', '—']);
    expect(pests.map((pest) => pest.pestCode)).toEqual(['SEPTTR', null, null]);
  });
});

describe('buildSheetHeader — degenerate input', () => {
  it('returns an empty header for no columns', () => {
    expect(buildSheetHeader([])).toEqual({ runs: [], pests: [] });
  });

  it('always yields spans >= 1 (no zero-width tier-1 cell)', () => {
    const { runs } = buildSheetHeader([column(1, 'C', 'LEAF1', 'SEPTTR')]);
    expect(runs.every((run) => run.span >= 1)).toBe(true);
  });
});
