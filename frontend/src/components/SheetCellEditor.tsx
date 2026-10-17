type SheetCellEditorProps = {
  /** Controlled draft text (the host owns the value in `sheetCellEditAtom`). */
  value: string;
  /** Fired on every keystroke. */
  onChange: (next: string) => void;
  /** Fired on Enter and on blur — the host validates, saves and dedupes. */
  onCommit: () => void;
  /** Fired on Escape — the host closes without saving. */
  onCancel: () => void;
  /** True while a save is in flight: the input is disabled to block re-entry. */
  disabled: boolean;
  /** Accessible name, e.g. `Edit plot 101 subsample 2, column E`. */
  label: string;
};

/**
 * The inline number input shown over an edited value cell (double-tap to open).
 *
 * Deliberately dumb: it renders the controlled draft and reports intent —
 * `onChange` / `onCommit` / `onCancel` — and owns NO save logic. The host hook
 * (`hooks/useSheetCellEdit.ts`) is the single writer of the draft + status
 * atoms, so it is also the one place that validates the draft
 * (`lib/sheetCellEdit.ts#parseCellDraft`), fires the request, and DEDUPES a
 * double commit: Enter commits and clears the edit atom, the input then
 * unmounts, and a React blur that may still fire is swallowed by the host's
 * live-edit identity check.
 *
 * Commit triggers are Enter and blur (tap-away / tab-away); Escape cancels.
 * Select-all on focus so an existing value is replaced by the first keystroke
 * without the operator having to clear it.
 */
export function SheetCellEditor({
  value,
  onChange,
  onCommit,
  onCancel,
  disabled,
  label,
}: SheetCellEditorProps) {
  return (
    <input
      className="sheet-cell__editor"
      type="number"
      inputMode="numeric"
      min={0}
      max={10}
      step={1}
      autoFocus
      disabled={disabled}
      aria-label={label}
      value={value}
      onChange={(event) => onChange(event.target.value)}
      onFocus={(event) => event.currentTarget.select()}
      onKeyDown={(event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          onCommit();
        } else if (event.key === 'Escape') {
          event.preventDefault();
          onCancel();
        }
      }}
      onBlur={onCommit}
    />
  );
}
