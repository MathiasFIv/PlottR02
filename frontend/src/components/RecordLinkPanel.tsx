import { Link } from 'react-router-dom';

type RecordLinkPanelProps = {
  /** Extra classes — e.g. grid placement. */
  className?: string;
};

// Decorative preview of the ratings sheet — same shape as the real SheetGrid
// (sticky key column + per-trait value columns): 1 = rated, 0 = empty.
const MINI_COLUMNS = ['PC1', 'PC2', 'PC3', 'PC4', 'PC5', 'PC6'];
const MINI_ROWS = [
  { plot: '01', values: [1, 1, 0, 1, 0, 0] },
  { plot: '02', values: [0, 1, 1, 1, 1, 0] },
  { plot: '03', values: [1, 0, 0, 1, 0, 1] },
  { plot: '04', values: [0, 0, 1, 0, 1, 1] },
];

/**
 * Dashboard launcher card that routes to /record (the recorder). Replaces the
 * Record link that previously lived in the topbar nav. Shows a small inert
 * copy of the ratings grid so the panel has some visual depth next to the
 * other dashboard cards.
 */
export function RecordLinkPanel({ className = '' }: RecordLinkPanelProps) {
  return (
    <section
      className={`card card-border bg-base-100 shadow-sm p-6 flex flex-col ${className}`}
    >
      <h2 className="text-lg font-semibold text-base-content">Record</h2>
      <p className="text-sm text-base-content/70 mt-1 mb-3">
        Rate plots in your assigned trials — tap an empty cell, key in the
        rating, and keep moving down the sheet.
      </p>

      {/* Visual copy of the sheet grid — decorative only, never interactive.
          Centered in the free space so the panel reads as a full-height tile
          next to its siblings. */}
      <div className="flex-1 flex flex-col justify-center min-h-0">
        <div className="rounded-lg border border-base-300 bg-base-200/40 p-2 mb-4 overflow-hidden select-none pointer-events-none">
        <div className="text-[10px] uppercase tracking-wide text-base-content/50 mb-1.5 px-1">
          Part Rated · Example trial
        </div>

        <div className="flex items-center gap-1 mb-1">
          <span className="w-16 shrink-0 rounded border border-base-300 bg-base-100 px-1 py-0.5 text-[10px] font-semibold text-base-content/60">
            Plot (Sub)
          </span>
          {MINI_COLUMNS.map((c) => (
            <span
              key={c}
              className="flex-1 text-center text-[10px] font-medium text-base-content/50"
            >
              {c}
            </span>
          ))}
        </div>

        {MINI_ROWS.map((row, i) => (
          <div
            key={row.plot}
            className={`flex items-center gap-1 mb-1 ${
              i === 1 ? 'rounded bg-warning/20 px-1 -mx-1' : ''
            }`}
          >
            <span className="w-16 shrink-0 rounded border border-base-300 bg-base-100 px-1 py-0.5 text-[10px] font-mono text-base-content/60">
              ({row.plot})
            </span>
            {row.values.map((v, j) => (
              <span
                key={j}
                className={`h-3 flex-1 rounded-sm ring-1 ring-inset ${
                  v ? 'bg-accent/70 ring-accent/40' : 'bg-base-200 ring-base-300'
                }`}
              />
            ))}
          </div>
        ))}

        <div className="flex items-center gap-3 mt-1.5 px-1 text-[10px] text-base-content/50">
          <span className="flex items-center gap-1">
            <span className="inline-block h-2 w-2 rounded-sm bg-accent/70" />
            rated
          </span>
          <span className="flex items-center gap-1">
            <span className="inline-block h-2 w-2 rounded-sm bg-base-200 ring-1 ring-inset ring-base-300" />
            empty
          </span>
          <span className="flex items-center gap-1">
            <span className="inline-block h-2 w-2 rounded-sm bg-warning/40" />
            current row
          </span>
        </div>
        </div>
      </div>

      <Link className="btn btn-primary w-full" to="/record">
        Open recorder
      </Link>
    </section>
  );
}