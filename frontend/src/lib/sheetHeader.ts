/**
 * Two-tier column header from the API's `columns` (never hardcoded). Tier 1
 * groups consecutive columns with equal `partRated` into runs (span reflects
 * the file); tier 2 is one cell per column (pest code).
 */

import type { SheetColumn } from '../types';

const EMPTY_LABEL = '—';

type SheetHeaderRun = {
  partRated: string | null;
  label: string;
  span: number;
};

type SheetHeaderPest = {
  traitOrdinal: number;
  column: string;
  pestCode: string | null;
  label: string;
};

type SheetHeader = {
  runs: SheetHeaderRun[];
  pests: SheetHeaderPest[];
};

function normalize(value: string | null): string | null {
  const trimmed = value === null ? '' : value.trim();
  return trimmed === '' ? null : trimmed;
}

function labelFor(normalized: string | null): string {
  return normalized ?? EMPTY_LABEL;
}

/** Builds the two-tier <thead> model from the API's `columns`. */
export function buildSheetHeader(columns: SheetColumn[]): SheetHeader {
  const runs: SheetHeaderRun[] = [];
  const pests: SheetHeaderPest[] = [];

  for (const column of columns) {
    const pestCode = normalize(column.pestCode);
    pests.push({
      traitOrdinal: column.traitOrdinal,
      column: column.column,
      pestCode,
      label: labelFor(pestCode),
    });

    const partRated = normalize(column.partRated);
    const current = runs.length > 0 ? runs[runs.length - 1] : undefined;
    if (current !== undefined && current.partRated === partRated) {
      current.span += 1;
    } else {
      runs.push({ partRated, label: labelFor(partRated), span: 1 });
    }
  }

  return { runs, pests };
}

/** One category block — consecutive columns with equal `partRated`, in array order. */
export type SheetBlock = {
  partRated: string | null;
  start: number;
  end: number;
};

/** Groups `columns` into blocks; never hardcoded to a fixed width. */
export function buildSheetBlocks(columns: SheetColumn[]): SheetBlock[] {
  const blocks: SheetBlock[] = [];

  for (const column of columns) {
    const partRated = normalize(column.partRated);
    const current = blocks.length > 0 ? blocks[blocks.length - 1] : undefined;
    if (current !== undefined && current.partRated === partRated) {
      current.end = column.traitOrdinal;
    } else {
      blocks.push({
        partRated,
        start: column.traitOrdinal,
        end: column.traitOrdinal,
      });
    }
  }

  return blocks;
}
