import { useAtomValue, useSetAtom } from 'jotai';
import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Button } from '../components/Button';
import { FormField } from '../components/FormField';
import {
  currentUserAtom,
  lastRatingResultAtom,
  ratingCellsAtom,
  ratingReadErrorAtom,
  ratingRecordErrorAtom,
} from '../store/atoms';
import { useAuth } from '../hooks/useAuth';
import { useRatings } from '../hooks/useRatings';

/**
 * Trait ordinal → display-name mapping (locked from the workbook template).
 * The value stored in the state / sent to the API is the ordinal (1..9).
 */
const TRAITS = [
  { ordinal: 1, name: 'SEPTTR × LEAF1' },
  { ordinal: 2, name: 'SEPTTR × LEAF2' },
  { ordinal: 3, name: 'SEPTTR × LEAF3' },
  { ordinal: 4, name: 'ERYSGR × LEAF1' },
  { ordinal: 5, name: 'ERYSGR × LEAF2' },
  { ordinal: 6, name: 'ERYSGR × LEAF3' },
  { ordinal: 7, name: 'PUCCST × LEAF1' },
  { ordinal: 8, name: 'PUCCST × LEAF2' },
  { ordinal: 9, name: 'PUCCST × LEAF3' },
] as const;

const SUBSAMPLES = [1, 2, 3, 4] as const;

function traitName(ordinal: number): string {
  return TRAITS.find((t) => t.ordinal === ordinal)?.name ?? `Trait ${ordinal}`;
}

/**
 * E2E pilot view for giving ratings. Card A records one plot/trait value across
 * the selected subsamples (POST /api/Ratings, one command per submission).
 * Card B reads the current values of a plot column (GET /api/Ratings/{plot}/{trait})
 * for verification — empty cells render as "unrated", never as 0.
 */
export function RatingsView() {
  const currentUser = useAtomValue(currentUserAtom);
  const navigate = useNavigate();
  const { logout } = useAuth();
  const {
    ratingCells,
    recordLoading,
    recordError,
    readLoading,
    readError,
    recordRating,
    readPlotColumn,
    clearResult,
  } = useRatings();
  const setRatingCells = useSetAtom(ratingCellsAtom);
  const setRecordError = useSetAtom(ratingRecordErrorAtom);
  const setReadError = useSetAtom(ratingReadErrorAtom);

  // Reset stale rating atoms when leaving the view.
  useEffect(() => {
    return () => {
      clearResult();
      setRatingCells([]);
      setRecordError(null);
      setReadError(null);
    };
  }, [clearResult, setRatingCells, setRecordError, setReadError]); // mount-only cleanup

  // ----- Card A: record rating form -----
  const [plot, setPlot] = useState('');
  const [traitOrdinal, setTraitOrdinal] = useState('1');
  const [value, setValue] = useState('');
  const [subsamples, setSubsamples] = useState<number[]>([...SUBSAMPLES]);
  const [recordFailed, setRecordFailed] = useState(false);

  // ----- Card B: read current values -----
  const [readPlot, setReadPlot] = useState('');
  const [readTraitOrdinal, setReadTraitOrdinal] = useState('1');
  const [readFailed, setReadFailed] = useState(false);

  const lastResult = useAtomValue(lastRatingResultAtom);

  // Client-side validation mirrors the backend: plot is a positive integer,
  // value is an integer in 0..10, at least one subsample is required.
  const plotNumber = Number(plot);
  const plotValid = Number.isInteger(plotNumber) && plotNumber >= 1;
  const valueNumber = Number(value);
  const valueValid =
    value.trim() !== '' &&
    Number.isInteger(valueNumber) &&
    valueNumber >= 0 &&
    valueNumber <= 10;
  const formValid = plotValid && valueValid && subsamples.length > 0;

  const readPlotNumber = Number(readPlot);
  const readPlotValid = Number.isInteger(readPlotNumber) && readPlotNumber >= 1;

  function toggleSubsample(index: number) {
    setSubsamples((prev) =>
      prev.includes(index) ? prev.filter((s) => s !== index) : [...prev, index],
    );
    clearResult();
  }

  function handleLogout() {
    logout();
    navigate('/login', { replace: true });
  }

  async function handleRecordRating(event: FormEvent) {
    event.preventDefault();
    if (!formValid || recordLoading) {
      return;
    }
    setRecordFailed(false);
    clearResult();
    const result = await recordRating({
      plot: plotNumber,
      traitOrdinal: Number(traitOrdinal),
      value: valueNumber,
      subsampleIndices: [...subsamples].sort((a, b) => a - b),
    });

    if (result === null) {
      setRecordFailed(true);
      return;
    }

    // Auto-refresh Card B when it is already showing the same plot + trait.
    if (
      readPlotValid &&
      readPlotNumber === result.plot &&
      Number(readTraitOrdinal) === result.traitOrdinal
    ) {
      void readPlotColumn(result.plot, result.traitOrdinal);
    }
  }

  async function handleLoadColumn() {
    if (!readPlotValid || readLoading) {
      return;
    }
    setReadFailed(false);
    const cells = await readPlotColumn(readPlotNumber, Number(readTraitOrdinal));
    if (cells === null) {
      setReadFailed(true);
    }
  }

  return (
    <div>
      <header className="topbar">
        <span className="topbar__brand">PlottR 0.2</span>
          <div className="topbar__user">
            {currentUser && (
              <span className="topbar__identity">
                Signed in as <strong>{currentUser.name}</strong>
                {currentUser.role ? ` (${currentUser.role})` : ''}
              </span>
            )}
            <Link className="link-button" to="/record">
              Record
            </Link>
            <Link className="link-button" to="/">
              Dashboard
            </Link>
            <Button variant="secondary" onClick={handleLogout}>
              Log out
            </Button>
          </div>
      </header>

      <main className="ratings">
        <section className="card">
          <h2 className="heading">Record rating</h2>
          <form onSubmit={handleRecordRating}>
            <FormField
              label="Plot"
              htmlFor="rating-plot"
              inputProps={{
                value: plot,
                onChange: (e) => {
                  setPlot(e.target.value);
                  clearResult();
                },
                placeholder: 'e.g. 101',
                type: 'number',
                min: 1,
                step: 1,
                required: true,
              }}
            />
            <FormField label="Trait" htmlFor="rating-trait">
              <select
                id="rating-trait"
                name="rating-trait"
                className="input"
                value={traitOrdinal}
                onChange={(e) => {
                  setTraitOrdinal(e.target.value);
                  clearResult();
                }}
              >
                {TRAITS.map((t) => (
                  <option key={t.ordinal} value={t.ordinal}>
                    {t.name}
                  </option>
                ))}
              </select>
            </FormField>
            <FormField
              label="Value"
              htmlFor="rating-value"
              inputProps={{
                value,
                onChange: (e) => {
                  setValue(e.target.value);
                  clearResult();
                },
                type: 'number',
                min: 0,
                max: 10,
                step: 1,
                required: true,
              }}
            />
            <p className="rating-hint">Scale 0–10, integers only.</p>

            <fieldset className="rating-subsamples">
              <legend className="rating-subsamples__legend">
                Subsamples (at least one required)
              </legend>
              {SUBSAMPLES.map((index) => (
                <label key={index} className="rating-subsample">
                  <input
                    type="checkbox"
                    checked={subsamples.includes(index)}
                    onChange={() => toggleSubsample(index)}
                  />
                  Subsample {index}
                </label>
              ))}
            </fieldset>

            <Button type="submit" disabled={!formValid || recordLoading}>
              {recordLoading ? 'Recording…' : 'Record rating'}
            </Button>
          </form>

          {recordFailed && recordError && <p className="error-text">{recordError}</p>}

          {lastResult && (
            <div className="rating-result" role="status">
              <p className="success-text">
                Recorded value {lastResult.value} for plot {lastResult.plot},{' '}
                {traitName(lastResult.traitOrdinal)}.
                {lastResult.emptyPreserved && ' Existing empty cells were preserved.'}
              </p>
              {!lastResult.emptyPreserved && (
                <p className="warning-text" role="alert">
                  Some requested cells were not written — the command addressed
                  subsamples that don't exist in this workbook.
                </p>
              )}
              {lastResult.writtenCells.length > 0 && (
                <ul className="result-list">
                  {lastResult.writtenCells.map((cell) => (
                    <li key={cell.subsample} className="result-list__item">
                      Subsample {cell.subsample} → <strong>{cell.value}</strong>{' '}
                      <span className="muted">({cell.cellAddress})</span>
                    </li>
                  ))}
                </ul>
              )}
              {lastResult.untouchedSubsamples.length > 0 && (
                <p className="muted rating-hint">
                  Untouched subsamples (not found in the workbook):{' '}
                  {lastResult.untouchedSubsamples.join(', ')}
                </p>
              )}
            </div>
          )}
        </section>

        <section className="card">
          <div className="card__header">
            <h2 className="heading">Read current values</h2>
            <Button
              type="button"
              variant="secondary"
              onClick={() => void handleLoadColumn()}
              disabled={!readPlotValid || readLoading}
            >
              {readLoading ? 'Loading…' : 'Load'}
            </Button>
          </div>

          <FormField
            label="Plot"
            htmlFor="rating-read-plot"
            inputProps={{
              value: readPlot,
              onChange: (e) => setReadPlot(e.target.value),
              placeholder: 'e.g. 101',
              type: 'number',
              min: 1,
              step: 1,
            }}
          />
          <FormField label="Trait" htmlFor="rating-read-trait">
            <select
              id="rating-read-trait"
              name="rating-read-trait"
              className="input"
              value={readTraitOrdinal}
              onChange={(e) => setReadTraitOrdinal(e.target.value)}
            >
              {TRAITS.map((t) => (
                <option key={t.ordinal} value={t.ordinal}>
                  {t.name}
                </option>
              ))}
            </select>
          </FormField>

          {readFailed && readError && <p className="error-text">{readError}</p>}

          {!readLoading && !readFailed && ratingCells.length === 0 && (
            <p className="muted">No column loaded yet — choose a plot and trait, then press Load.</p>
          )}

          {!readLoading && !readFailed && ratingCells.length > 0 && (
            <ul className="rating-column-list">
              {ratingCells.map((cell) => (
                <li key={cell.subsample} className="rating-column-item">
                  <span>
                    Subsample {cell.subsample}{' '}
                    <span className="muted">({cell.cellAddress})</span>
                  </span>
                  {cell.value === null ? (
                    <span className="muted">unrated</span>
                  ) : (
                    <span className="rating-column-value">{cell.value}</span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
      </main>
    </div>
  );
}