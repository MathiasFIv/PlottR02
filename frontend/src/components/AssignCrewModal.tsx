import { useEffect, useState } from 'react';
import { Button } from './Button';
import { api } from '../lib/api';
import { assignableCrew, roleLabel } from '../types';
import type { TrialSummary, User } from '../types';

type AssignCrewModalProps = {
  trial: TrialSummary;
  /**
   * Fired after a successful save (the assignment was replaced) — lets the
   * caller refetch, e.g. so an "Assigned to you" badge appears the moment the
   * signed-in user is added to a trial's crew.
   */
  onSaved?: () => void;
  onClose: () => void;
};

/**
 * Per-trial "Assign crew" dialog. On open it fetches the assignable candidates
 * (GET /api/Users, widened server-side to include researchers) and the trial's
 * current crew (GET /api/Trials/{trialId}/users) in parallel, then pre-checks
 * the crew already assigned. Each user renders as a click-to-toggle row in a
 * single list (no checkboxes) — any 1–N selection is allowed, and selected rows
 * are highlighted with a check. Save replaces the assignment wholesale with a
 * single PUT (204) and closes; Cancel closes without touching the server.
 */
export function AssignCrewModal({ trial, onSaved, onClose }: AssignCrewModalProps) {
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
      onSaved?.();
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
        className="modal-box max-w-[520px]"
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
          <>
            <div className="flex items-center justify-between mb-3">
              <p className="text-sm text-base-content/70">
                Click a row to add or remove a crew member.
              </p>
              <p className="text-sm text-base-content/70 tabular-nums">
                {selectedIds.size} selected
              </p>
            </div>
            <ul className="list max-h-[min(60vh,26rem)] overflow-y-auto lg:overscroll-contain">
              {candidates.map((user) => {
                const selected = selectedIds.has(user.id);
                return (
                  <li key={user.id}>
                    <button
                      type="button"
                      aria-pressed={selected}
                      onClick={() => toggle(user.id)}
                      className={`btn btn-ghost btn-block justify-start rounded-none text-left h-auto min-h-0 py-3 ${selected ? 'bg-accent/10' : ''}`}
                    >
                      <span className="min-w-0">
                        <span className="block font-medium text-base-content truncate">
                          {user.name}
                        </span>
                        <span className="block text-sm text-base-content/70 truncate">
                          {user.email}
                        </span>
                        {user.role && (
                          <span className="block text-sm text-base-content/70">
                            {roleLabel(user.role)}
                          </span>
                        )}
                      </span>
                      {selected && (
                        <svg
                          className="w-4 h-4 text-accent shrink-0"
                          viewBox="0 0 20 20"
                          fill="currentColor"
                          aria-hidden="true"
                        >
                          <path
                            fillRule="evenodd"
                            d="M16.7 5.3a1 1 0 0 1 0 1.4l-7 7a1 1 0 0 1-1.4 0l-3-3a1 1 0 1 1 1.4-1.4L9 11.6l6.3-6.3a1 1 0 0 1 1.4 0Z"
                            clipRule="evenodd"
                          />
                        </svg>
                      )}
                    </button>
                  </li>
                );
              })}
            </ul>
          </>
        )}

        {saveError && <p className="text-sm mt-3 text-error">{saveError}</p>}

<div className="modal-action">
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
