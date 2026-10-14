import { defineConfig } from 'vitest/config';

// Unit tests only — no DOM environment (React rendering is covered elsewhere /
// not needed for the IndexedDB wrapper). fake-indexeddb runs in plain Node.
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
});