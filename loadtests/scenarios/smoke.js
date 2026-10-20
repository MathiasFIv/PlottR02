// SMOKE load test: 1 VU for 30s.
//
// Gate scenario. Runs on `workflow_dispatch` and (per the requirement) may be
// wired into gating. It bootstraps an admin + trial in setup(), then per
// iteration exercises one authenticated read and one idempotent write.
//
// Never touches the voice/transcription endpoints (they cost real money).

import { check } from 'k6';
import {
  SMOKE_SCENARIO,
  SMOKE_THRESHOLDS,
  randomCellPayload,
} from '../lib/config.js';
import { setup as bootstrap } from '../lib/setup.js';
import { editCell, getSheet } from '../lib/http.js';

export const options = {
  scenarios: SMOKE_SCENARIO,
  thresholds: SMOKE_THRESHOLDS,
  // Keep the exit code meaningful: a failed threshold fails the run.
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'max'],
};

export function setup() {
  return bootstrap();
}

export default function (data) {
  // Read endpoint: the per-trial sheet view (exercises the workbook read path).
  const sheetRes = getSheet(data.token, data.trialId);
  check(sheetRes, {
    'GET sheet status is 200': (r) => r.status === 200,
    'GET sheet returns a body': (r) => r.body && r.body.length > 0,
  });

  // Write endpoint: idempotent single-cell edit (overwrite: true).
  const payload = randomCellPayload();
  const cellRes = editCell(data.token, data.trialId, payload);
  check(cellRes, {
    'POST cell status is 200': (r) => r.status === 200,
    'POST cell reports written': (r) => {
      if (r.status !== 200) return false;
      const body = r.json();
      return body && body.written === true;
    },
  });
}
