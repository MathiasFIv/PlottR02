import { useAtom, useAtomValue } from 'jotai';
import { useEffect } from 'react';
import { useSheetCellEdit } from '../hooks/useSheetCellEdit';
import { useTrialSheet } from '../hooks/useTrialSheet';
import { deriveEndOfSheet, deriveNextTarget } from '../lib/nextTarget';
import { decideAnchorTap, isAnchorForTrial } from '../lib/sheetAnchor';
import type { SheetCellTap } from '../lib/sheetAnchor';
import { isNonArmSheetError } from '../lib/sheetStatus';
import { rehydrateSheetTarget, targetHighlight, targetSelection } from '../lib/sheetTarget';
import { sheetLastWrittenAtom, sheetSelectedAtom } from '../store/atoms';
import { SheetGrid } from './SheetGrid';
import { SheetEditNotice } from './SheetEditNotice';
import { SheetMetaStrip } from './SheetMetaStrip';
import { SheetStates } from './SheetStates';
import { SheetStatusPill } from './SheetStatusPill';

type SheetPanelProps = {
  trialId: string;
};

/**
 * The RecordView embed wrapper (D2 / §6): the sheet is visible *while*
 * recording. It owns its whole lifecycle — status pill, metadata strip and
 * the grid — from the `trialId` prop alone, so it never reaches into
 * RecordView's state; switching trials in the picker changes the prop, which
 * clears the sheet atoms, refetches and rehydrates the snapshot.
 *
 * **N2 shows ALL rows** (deliberate transitional state — it validates the
 * phone layout one phase early). N3 narrows this to the focused view
 * (active plot ± 1) and adds the `[This plot | All rows]` toggle: that phase
 * only has to swap the `rows` passed to `SheetGrid` below.
 *
 * The attention badge stays in the mic card; this panel adds nothing but its
 * own status pill, so the two never compete.
 *
 * The N3 current-cell highlight is derived DURING RENDER from
 * `sheetLastWrittenAtom` (the last-written pair + `blockFull`, trial-guarded)
 * + the payload on screen: `deriveNextTarget` STAYS on the written row while
 * its category block is unfilled and advances to the NEXT row (crossing plot
 * boundaries) once it is full; only the true end of the sheet is null, and
 * `targetHighlight` maps it onto the grid, so a refresh or snapshot hydrate
 * re-derives it for free and the persisted pair is re-seeded by the effect
 * below (mount / trial change).
 *
 * **Write anchor (tap-to-anchor):** the grid is tappable through
 * `onSelectCell` below. A tapped ROW becomes the anchor (`sheetSelectedAtom`,
 * trial-guarded with `isAnchorForTrial` and mapped through `targetSelection`
 * — the same identity rule as the highlight); the tap is ROW-ONLY for the
 * WRITE, so it fills from that row's first empty cell and pins no column -
 * the tapped CELL is carried only to render the anchor highlight.
 * Tapping the currently-anchored row again clears the anchor. A tap on a
 * fully-filled row still selects it — an honest write then reports "already
 * filled" via the sync banner. The anchor atom is NOT cleared by this
 * component's trial change either — that is the point of declaring it
 * outside the payload block.
 */
export function SheetPanel({ trialId }: SheetPanelProps) {
  const { sheet, status, source, error, updatedAt } = useTrialSheet(trialId);
  /** N3 last-written pair + blockFull — read live; written by the sync engine. */
  const target = useAtomValue(sheetLastWrittenAtom);
  /** Only THIS trial's pair may tint this trial's grid (plot numbers repeat). */
  const forTrial = target?.trialId === trialId ? target : null;
  /** Write anchor — read AND written here (tap gesture); syncQueue also consumes it. */
  const [anchor, setAnchor] = useAtom(sheetSelectedAtom);
  /** Only THIS trial's tap may anchor this trial's grid. */
  const forTrialAnchor = isAnchorForTrial(anchor, trialId);
  /**
   * Double-tap-to-edit state + handlers. Kept separate from the anchor: the
   * first tap anchors (as before), the second opens this editor. The hook
   * clears both its atoms on unmount / trial change.
   */
  const edit = useSheetCellEdit(trialId);

  // Seed / re-seed from the persisted last-written target on mount
  // and on trial change (the atom survives route changes; storage is the
  // source of truth after a reload).
  useEffect(() => {
    rehydrateSheetTarget(trialId);
  }, [trialId]);

  const columns = sheet?.columns ?? null;
  const rows = sheet?.trialId === trialId ? sheet.rows : null;
  // A non-ARM 400 wins over the copy: the server says the sheet is gone.
  // So does a `not-found`: a stale snapshot behind a 404 must render the
  // not-found state, not a grid under a "not found" pill.
  const hasPayload =
    columns !== null &&
    rows !== null &&
    status !== 'not-found' &&
    !isNonArmSheetError(error);

  // Next row: STAY on the written row while its block is unfilled, else the
  // NEXT row (crossing plot boundaries) — payload-derived, never arithmetic,
  // never a guessed pair; only the true end of the sheet is null.
  const next =
    rows !== null ? deriveNextTarget(rows, forTrial, forTrial?.blockFull ?? false) : null;
  const endOfSheet = deriveEndOfSheet(rows, forTrial);

  /**
   * Tap-to-anchor. The DECISION is shared with the `views/SheetView.tsx`
   * host in `lib/sheetAnchor.ts#decideAnchorTap` (one grid, two hosts — no
   * duplicated rules); this host only applies it:
   * - a tap on the anchored row → clear the anchor;
   * - any other row → anchor it (row-only; the write starts at that row's
   *   first empty cell).
   * The tap never touches `sheetLastWrittenAtom`: it is an intent, not a
   * written fact, so `Next:` / auto-continue cannot move on a gesture.
   */
  function handleSelectCell(cell: SheetCellTap): void {
    const decision = decideAnchorTap(cell, forTrialAnchor, trialId);
    setAnchor(decision.kind === 'clear' ? null : decision.anchor);
  }

  return (
    <div className="flex flex-col gap-3 min-w-0 sheet:flex-1 sheet:min-h-0">
      <div className="flex items-center gap-3 flex-wrap">
        <h2 className="text-lg font-semibold text-base-content mr-auto">
          Sheet
        </h2>
        <SheetStatusPill status={status} source={source} savedAt={updatedAt} />
      </div>

      {sheet?.metadata != null && <SheetMetaStrip metadata={sheet.metadata} />}

      <SheetStates
        status={status}
        error={error}
        hasPayload={hasPayload}
        rowCount={hasPayload && rows !== null ? rows.length : null}
      />

      {hasPayload && (
        <SheetEditNotice
          error={edit.error}
          conflict={edit.conflict}
          saving={edit.saving}
          onConfirmOverwrite={edit.onConfirmOverwrite}
          onDismissConflict={edit.onDismissConflict}
        />
      )}

      {hasPayload && rows !== null && rows.length > 0 && columns !== null && (
        <SheetGrid
          columns={columns}
          rows={rows}
          highlight={targetHighlight(next, rows)}
          selected={targetSelection(forTrialAnchor, rows)}
          onSelectCell={handleSelectCell}
          onEditCell={edit.onEditCell}
          editing={edit.editing}
          editDraft={edit.draft}
          editSaving={edit.saving}
          onEditDraftChange={edit.onEditDraftChange}
          onEditCommit={edit.onEditCommit}
          onEditCancel={edit.onEditCancel}
        />
      )}

      {hasPayload && rows !== null && endOfSheet !== null && (
        <p className="text-sm text-base-content/70" role="status">
          End of sheet — last: Plot {endOfSheet.plot} · Sub {endOfSheet.subsample}
        </p>
      )}
    </div>
  );
}
