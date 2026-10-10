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
    <div className="modal modal-open">
      <div
        className="modal-box max-w-[440px]"
        role="dialog"
        aria-modal="true"
        aria-labelledby="assign-crew-title"
      >
        <div className="flex items-center justify-between mb-3">
          <h2
            id="assign-crew-title"
            className="text-lg font-semibold text-base-content"
          >
            Assign crew
          </h2>
        </div>
        <p className="text-sm text-base-content/70 mb-4">{trial.fileName}</p>

        {loading && <p className="text-sm text-base-content/70">Loading crew…</p>}

        {!loading && loadError && (
          <p className="text-sm mt-3 text-error">{loadError}</p>
        )}

        {!loading && !loadError && candidates.length === 0 && (
          <p className="text-sm text-base-content/70">
            No users can be assigned yet.
          </p>
        )}

        {!loading && !loadError && candidates.length > 0 && (
          <ul className="list-none m-0 p-0">
            {candidates.map((user) => (
              <li key={user.id} className="mb-2">
                <label className="flex gap-2 items-start cursor-pointer">
                  <input
                    type="checkbox"
                    className="checkbox checkbox-sm mt-0.5"
                    checked={selectedIds.has(user.id)}
                    onChange={() => toggle(user.id)}
                  />
                  <span>
                    <span className="font-medium text-base-content">
                      {user.name}
                    </span>
                    <span className="block text-sm text-base-content/70">
                      {user.email}
                    </span>
                    {user.role && (
                      <span className="block text-sm text-base-content/70">
                        {roleLabel(user.role)}
                      </span>
                    )}
                  </span>
                </label>
              </li>
            ))}
          </ul>
        )}

        {saveError && <p className="text-sm mt-3 text-error">{saveError}</p>}

        <div className="flex justify-end gap-3 mt-5">
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
