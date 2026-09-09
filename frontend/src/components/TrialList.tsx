import type { TrialSummary } from '../types';

type TrialListProps = {
  trials: TrialSummary[];
  loading: boolean;
  error: string | null;
};

/** Presentational list of trials fetched from GET /api/Trials. */
export function TrialList({ trials, loading, error }: TrialListProps) {
  if (loading && trials.length === 0) {
    return <p className="muted">Loading trials…</p>;
  }

  if (error) {
    return <p className="error-text">Error: {error}</p>;
  }

  if (trials.length === 0) {
    return <p className="muted">No trials uploaded yet.</p>;
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
            </div>
          </div>
          <code className="muted trial-list__id">{trial.trialId}</code>
        </li>
      ))}
    </ul>
  );
}
