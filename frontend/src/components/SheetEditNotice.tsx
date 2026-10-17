type SheetEditNoticeProps = {
  /** Inline save/validation error, or null. */
  error: string | null;
  /** True while a save is in flight (reserved for future busy messaging). */
  saving: boolean;
};

/**
 * The save-lifecycle surface for the inline cell editor, as ONE shared block
 * so the embedded panel and the full-sheet route can never drift apart (the
 * same reason `SheetStates` exists).
 *
 * Error-only: there is no conflict offer any more — both capture modes
 * (voice arm + keyboard fallback) overwrite, and the yellow armed cell
 * (`.sheet-cell--armed`) is the "this cell is being written" affordance.
 * Renders nothing when there is no error. `role="alert"` so a failed save is
 * announced.
 */
export function SheetEditNotice({ error, saving }: SheetEditNoticeProps) {
  if (error === null) return null;

  return (
    <p className="text-sm text-error" role="alert" aria-busy={saving || undefined}>
      {error}
    </p>
  );
}
