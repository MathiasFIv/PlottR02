import { formatConflictPrior } from '../lib/sheetCellEditOutcome';
import { Button } from './Button';

type SheetEditNoticeProps = {
  /** Inline save/validation error, or null. */
  error: string | null;
  /** The 409 offer — the cell already holds `priorValue`. */
  conflict: { priorValue: number | null; value: number } | null;
  /** True while a save is in flight — the buttons are disabled. */
  saving: boolean;
  /** Confirms the overwrite (retries `api.editCell` with `overwrite: true`). */
  onConfirmOverwrite: () => void;
  /** "Keep existing" — closes the editor without writing. */
  onDismissConflict: () => void;
};

/**
 * The save-lifecycle surface for the inline cell editor, as ONE shared block
 * so the embedded panel and the full-sheet route can never drift apart (the
 * same reason `SheetStates` exists).
 *
 * Renders nothing when there is neither an error nor a conflict to offer.
 * `role="alert"` for the error (a failed save needs announcing) and the
 * conflict offer is a labelled pair of actions — never an un-dismissable
 * dialog, since the operator is mid-edit and Escape still closes the input.
 */
export function SheetEditNotice({
  error,
  conflict,
  saving,
  onConfirmOverwrite,
  onDismissConflict,
}: SheetEditNoticeProps) {
  if (error === null && conflict === null) return null;

  if (conflict !== null) {
    // A conflict means the cell is NOT writable (non-empty, not `"missing"`),
    // so a null `priorValue` is non-numeric text — never "empty".
    const prior = formatConflictPrior(conflict.priorValue);
    return (
      <div
        className="text-sm bg-warning/15 text-warning-content rounded-md px-3 py-2 flex flex-wrap items-center gap-3"
        role="alert"
      >
        <p className="mr-auto">
          Cell already has {prior}. Overwrite with {conflict.value}?
        </p>
        <Button type="button" onClick={onConfirmOverwrite} disabled={saving}>
          Overwrite
        </Button>
        <Button
          type="button"
          variant="secondary"
          onClick={onDismissConflict}
          disabled={saving}
        >
          Keep existing
        </Button>
      </div>
    );
  }

  return (
    <p className="text-sm text-error" role="alert">
      {error}
    </p>
  );
}
