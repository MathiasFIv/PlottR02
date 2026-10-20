// STEADY load test: ramp 0 -> ~20 VUs, hold ~2 min, ramp down (~3 min total).
//
// Runs only on `workflow_dispatch` and the weekly schedule (Mon 03:00 UTC).
// NOT wired into PRs. Bootstraps an admin + trial in setup() once, then each VU
// loops reads and idempotent writes against that single trial.
//
// The trial's per-write serialization (TrialWriteGate) means a single trial
// bounds write concurrency; reads still run in parallel. This is intentional:
// it measures the API/DB under sustained, realistic single-trial contention.
//
// Never touches the voice/transcription endpoints (they cost real money).

import { check, sleep } from 'k6';
import {
  STEADY_SCENARIO,
  STEADY_THRESHOLDS,
  randomCellPayload,
} from '../lib/config.js';
import { setup as bootstrap } from '../lib/setup.js';
import { editCell, getSheet } from '../lib/http.js';

export const options = {
  scenarios: STEADY_SCENARIO,
  thresholds: STEADY_THRESHOLDS,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'max'],
};

export function setup() {
  return bootstrap();
}

export default function (data) {
  // Read path.
  const sheetRes = getSheet(data.token, data.trialId);
  check(sheetRes, {
    'GET sheet status is 200': (r) => r.status === 200,
  });

  // Idempotent write path (overwrite: true).
  const payload = randomCellPayload();
  const cellRes = editCell(data.token, data.trialId, payload);
  check(cellRes, {
    'POST cell status is 200': (r) => r.status === 200,
  });

  // Small think-time so 20 VUs do not pin a 100% CPU loop against a single trial.
  sleep(1);
}
