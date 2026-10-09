import { Link } from 'react-router-dom';
import type { TrialSummary } from '../types';

type TrialListProps = {
  trials: TrialSummary[];
  loading: boolean;
  error: string | null;
  /** Optional per-row handler for downloading the trial's current workbook. */
  onDownload?: (trial: TrialSummary) => void;
  /** Trial whose workbook download is currently in flight (disables its row button). */
  downloadingTrialId?: string | null;
  /** Optional per-row handler for opening the crew-assignment dialog. */
  onAssign?: (trial: TrialSummary) => void;
  /** Overrides the default empty-state line when the list has no trials. */
  emptyMessage?: string;
};

/** Presentational list of trials fetched from GET /api/Trials. */
export function TrialList({
  trials,
  loading,
  error,
  onDownload,
  downloadingTrialId,
  onAssign,
  emptyMessage,
}: TrialListProps) {
  if (loading && trials.length === 0) {
    return <p className="muted">Loading trials…</p>;
  }

  if (error) {
    return <p className="error-text">Error: {error}</p>;
  }

  if (trials.length === 0) {
    return <p className="muted">{emptyMessage ?? 'No trials uploaded yet.'}</p>;
  }

  return (
    <ul className="trial-list">
      {trials.map((trial) => (
        <li key={trial.trialId} className="trial-list__item">
          <div>
            <p className="trial-list__name">{trial.fileName}</p>
            <div className="trial-list__meta">
              <span className="trial-list__badge">{trial.status}</span>
              <span className="muted">{trial.externalId}</span>
              <span className="muted">
                {new Date(trial.createdAt).toLocaleString()}
              </span>
              {trial.createdBy && (
                <span className="muted">by {trial.createdBy}</span>
              )}
              {onDownload && (
                <button
                  type="button"
                  className="link-button"
                  disabled={downloadingTrialId === trial.trialId}
                  onClick={() => onDownload(trial)}
                >
                  Download
                </button>
              )}
              {onAssign && (
                <button
                  type="button"
                  className="link-button"
                  onClick={() => onAssign(trial)}
                >
                  Assign crew
                </button>
              )}
              {/* Read-only sheet route (N2). A non-ARM workbook renders the
                  dedicated readable state on that page rather than failing. */}
              <Link className="link-button" to={`/trials/${trial.trialId}/sheet`}>
                View sheet
              </Link>
            </div>
          </div>
          <code className="muted trial-list__id">{trial.trialId}</code>
        </li>
      ))}
    </ul>
  );
}
