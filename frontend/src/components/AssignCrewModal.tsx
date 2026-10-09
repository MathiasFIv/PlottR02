import { useEffect, useState } from 'react';
import { Button } from './Button';
import { api } from '../lib/api';
import { assignableCrew, roleLabel } from '../types';
import type { TrialSummary, User } from '../types';

type AssignCrewModalProps = {
  trial: TrialSummary;
  onClose: () => void;
};

/**
 * Per-trial "Assign crew" dialog. On open it fetches the assignable candidates
 * (GET /api/Users, widened server-side to include researchers) and the trial's
 * current crew (GET /api/Trials/{trialId}/users) in parallel, then pre-checks
 * the crew already assigned. Save replaces the assignment wholesale with a
 * single PUT (204) and closes; Cancel closes without touching the server.
 */
export function AssignCrewModal({ trial, onClose }: AssignCrewModalProps) {
  const [candidates, setCandidates] = useState<User[]>([]);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let cancelled = false;

    (async () => {
      try {
        const [users, assigned] = await Promise.all([
          api.getUsers(),
          api.listTrialUsers(trial.trialId),
        ]);
        if (cancelled) return;
        setCandidates(assignableCrew(users));
        setSelectedIds(new Set(assignableCrew(assigned).map((user) => user.id)));
      } catch (err) {
        if (!cancelled) {
          setLoadError(
            err instanceof Error ? err.message : 'Could not load crew.',
          );
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [trial.trialId]);

  function toggle(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  async function handleSave() {
    if (saving) return;
    setSaving(true);
    setSaveError(null);
    try {
      await api.assignTrialUsers(trial.trialId, [...selectedIds]);
      onClose();
    } catch (err) {
      setSaveError(
        err instanceof Error ? err.message : 'Could not assign crew.',
      );
      setSaving(false);
    }
  }

  const canSave = !loading && !loadError && candidates.length > 0;

  return (
    <div
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(17, 24, 39, 0.5)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        padding: 24,
        zIndex: 1000,
      }}
    >
      <div
        className="card"
        role="dialog"
        aria-modal="true"
        aria-labelledby="assign-crew-title"
        style={{
          width: '100%',
          maxWidth: 440,
          maxHeight: '90vh',
          overflowY: 'auto',
        }}
      >
        <div className="card__header">
          <h2 id="assign-crew-title" className="heading">
            Assign crew
          </h2>
        </div>
        <p className="muted" style={{ marginBottom: 16 }}>
          {trial.fileName}
        </p>

        {loading && <p className="muted">Loading crew…</p>}

        {!loading && loadError && <p className="error-text">{loadError}</p>}

        {!loading && !loadError && candidates.length === 0 && (
          <p className="muted">No users can be assigned yet.</p>
        )}

        {!loading && !loadError && candidates.length > 0 && (
          <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {candidates.map((user) => (
              <li key={user.id} style={{ marginBottom: 8 }}>
                <label
                  style={{
                    display: 'flex',
                    gap: 8,
                    alignItems: 'flex-start',
                    cursor: 'pointer',
                  }}
                >
                  <input
                    type="checkbox"
                    checked={selectedIds.has(user.id)}
                    onChange={() => toggle(user.id)}
                    style={{ marginTop: 3 }}
                  />
                  <span>
                    <span style={{ fontWeight: 500, color: '#111827' }}>
                      {user.name}
                    </span>
                    <span className="muted" style={{ display: 'block' }}>
                      {user.email}
                    </span>
                    {user.role && (
                      <span className="muted" style={{ display: 'block' }}>
                        {roleLabel(user.role)}
                      </span>
                    )}
                  </span>
                </label>
              </li>
            ))}
          </ul>
        )}

        {saveError && <p className="error-text">{saveError}</p>}

        <div className="modal__actions">
          <Button
            variant="secondary"
            type="button"
            onClick={onClose}
            disabled={saving}
          >
            Cancel
          </Button>
          <Button
            type="button"
            onClick={() => void handleSave()}
            disabled={!canSave || saving}
          >
            {saving ? 'Saving…' : 'Save'}
          </Button>
        </div>
      </div>
    </div>
  );
}
