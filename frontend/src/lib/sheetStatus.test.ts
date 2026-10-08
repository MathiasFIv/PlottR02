import { describe, expect, it } from 'vitest';
import {
  classifySheetFailure,
  describeSheetStatus,
  formatSavedAt,
  isNonArmSheetError,
} from './sheetStatus';

describe('formatSavedAt', () => {
  it('formats an ISO timestamp as local YYYY-MM-DD HH:mm', () => {
    // Built from local components so the assertion holds in any timezone.
    const iso = new Date(2026, 9, 7, 9, 45, 13).toISOString();
    expect(formatSavedAt(iso)).toBe('2026-10-07 09:45');
  });

  it('returns null for junk instead of throwing', () => {
    expect(formatSavedAt('not a date')).toBeNull();
    expect(formatSavedAt('')).toBeNull();
    expect(formatSavedAt(null)).toBeNull();
  });
});

describe('describeSheetStatus', () => {
  it('says "Loading sheet…" with no cached copy behind it', () => {
    const pill = describeSheetStatus('loading', 'network', null);
    expect(pill.label).toBe('Loading sheet…');
    expect(pill.tone).toBe('muted');
    expect(pill.detail).toBeNull();
  });

  it('says the copy is stale while a snapshot is on screen and still fetching', () => {
    const pill = describeSheetStatus('loading', 'cache', '2026-10-07T07:45:13.402Z');
    expect(pill.label).toBe('stale — showing saved copy');
    expect(pill.tone).toBe('warn');
    expect(pill.detail).toMatch(/^saved \d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
  });

  it('distinguishes a live network payload from a live saved copy', () => {
    expect(describeSheetStatus('live', 'network', null).label).toBe('live');
    const cached = describeSheetStatus('live', 'cache', null);
    expect(cached.label).toBe('live — saved copy');
    expect(cached.tone).toBe('ok');
    // source=cache but no usable savedAt → no misleading "saved" suffix.
    expect(cached.detail).toBeNull();
  });

  it('reports offline while a copy stays on screen', () => {
    const pill = describeSheetStatus('offline', 'cache', null);
    expect(pill.label).toBe('offline — showing saved copy');
    expect(pill.tone).toBe('warn');
  });

  it('keeps the "saved …" detail on offline even when source is network', () => {
    // Last payload came from the network, a later refresh failed: the label
    // still claims a saved copy, so it must still cite the timestamp.
    const pill = describeSheetStatus('offline', 'network', '2026-10-07T07:45:13.402Z');
    expect(pill.label).toBe('offline — showing saved copy');
    expect(pill.detail).toMatch(/^saved \d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
    // Unusable savedAt → still no misleading suffix.
    expect(describeSheetStatus('offline', 'network', null).detail).toBeNull();
  });

  it('uses the error tone for not-found and error, muted for idle', () => {
    expect(describeSheetStatus('not-found', 'network', null).tone).toBe('error');
    expect(describeSheetStatus('error', 'network', null).tone).toBe('error');
    expect(describeSheetStatus('idle', 'network', null)).toEqual({
      status: 'idle',
      source: 'network',
      label: 'Sheet not loaded',
      tone: 'muted',
      detail: null,
    });
  });
});

describe('isNonArmSheetError', () => {
  it('recognises the server plain-string 400 for a workbook without a sheet', () => {
    expect(isNonArmSheetError("Sheet 'Ratings' was not found")).toBe(true);
    expect(isNonArmSheetError("Sheet 'ratings' was not found in this workbook.")).toBe(
      true,
    );
  });

  it('does not match ordinary failures', () => {
    expect(isNonArmSheetError(null)).toBe(false);
    expect(isNonArmSheetError('Not found')).toBe(false);
    expect(isNonArmSheetError('was not found')).toBe(false); // no Sheet '…' shape
  });
});

describe('classifySheetFailure', () => {
  it('sends the non-ARM 400 to error — the host renders the readable state', () => {
    expect(classifySheetFailure("Sheet 'Ratings' was not found", 400, false)).toBe(
      'error',
    );
    // Even with a payload on screen, the dedicated state wins over "offline".
    expect(classifySheetFailure("Sheet 'Ratings' was not found", 400, true)).toBe(
      'error',
    );
  });

  it('maps 404 to not-found (trial/workbook gone — not retryable)', () => {
    expect(classifySheetFailure('No such trial', 404, false)).toBe('not-found');
    expect(classifySheetFailure(null, 404, true)).toBe('not-found');
  });

  it('keeps the copy on screen (offline) when a payload is already loaded', () => {
    expect(classifySheetFailure(null, 500, true)).toBe('offline');
    expect(classifySheetFailure('boom', null, true)).toBe('offline');
  });

  it('maps everything else to error', () => {
    expect(classifySheetFailure('boom', 500, false)).toBe('error');
    expect(classifySheetFailure(null, null, false)).toBe('error'); // offline before fetch
  });
});
