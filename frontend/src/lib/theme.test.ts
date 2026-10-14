import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  applyTheme,
  isTheme,
  readStoredTheme,
  resolveInitialTheme,
  systemTheme,
  THEME_STORAGE_KEY,
  toggleTheme,
  writeStoredTheme,
} from './theme';

/**
 * House style: hand-rolled fakes, no DOM environment, no mock library. Every
 * DOM/global is a duck-typed object installed with `vi.stubGlobal` (the same
 * pattern as `api.test.ts` / `sheetSnapshot.test.ts`). Nothing at module top
 * level touches `window`/`document`, so this file boots in plain Node.
 */

type StorageStub = Pick<Storage, 'getItem' | 'setItem'>;

/** Map-backed localStorage stub — no DOM, no reliance on a global. */
function memoryStorage(
  initial: Record<string, string> = {},
): StorageStub & { map: Map<string, string> } {
  const map = new Map(Object.entries(initial));
  return {
    map,
    getItem: (key) => map.get(key) ?? null,
    setItem: (key, value) => {
      map.set(key, value);
    },
  };
}

/**
 * Installs a duck-typed `window` carrying `localStorage` + a `matchMedia` that
 * reports dark only when `systemDark` is set. Returns the storage so tests can
 * assert what was written.
 */
function stubWindow(opts: { storage?: StorageStub; systemDark?: boolean } = {}): StorageStub {
  const storage = opts.storage ?? memoryStorage();
  vi.stubGlobal('window', {
    localStorage: storage,
    matchMedia: (query: string) => ({
      matches: opts.systemDark === true && query.includes('prefers-color-scheme: dark'),
    }),
  });
  return storage;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('isTheme', () => {
  it('accepts exactly the two theme ids', () => {
    expect(isTheme('plottr-light')).toBe(true);
    expect(isTheme('plottr-dark')).toBe(true);
  });

  it('rejects everything else', () => {
    for (const value of [undefined, null, '', 'light', 'dark', 'plottr', 42, {}]) {
      expect(isTheme(value)).toBe(false);
    }
  });
});

describe('toggleTheme', () => {
  it('flips light → dark → light', () => {
    expect(toggleTheme('plottr-light')).toBe('plottr-dark');
    expect(toggleTheme('plottr-dark')).toBe('plottr-light');
  });
});

describe('systemTheme', () => {
  it('maps a matching dark media query to plottr-dark', () => {
    stubWindow({ systemDark: true });
    expect(systemTheme()).toBe('plottr-dark');
  });

  it('maps a non-matching query to plottr-light', () => {
    stubWindow({ systemDark: false });
    expect(systemTheme()).toBe('plottr-light');
  });

  it('degrades to light when window/matchMedia is absent', () => {
    // No stub at all: Node has no `window`.
    expect(systemTheme()).toBe('plottr-light');
  });
});

describe('readStoredTheme / resolveInitialTheme', () => {
  it('reads a valid stored theme', () => {
    stubWindow({ storage: memoryStorage({ [THEME_STORAGE_KEY]: 'plottr-dark' }) });
    expect(readStoredTheme()).toBe('plottr-dark');
  });

  it('returns null for an invalid stored value', () => {
    stubWindow({ storage: memoryStorage({ [THEME_STORAGE_KEY]: 'papaya' }) });
    expect(readStoredTheme()).toBeNull();
  });

  it('resolves the stored choice over the system preference', () => {
    stubWindow({
      storage: memoryStorage({ [THEME_STORAGE_KEY]: 'plottr-dark' }),
      systemDark: false,
    });
    expect(resolveInitialTheme()).toBe('plottr-dark');
  });

  it('falls back to the system preference when nothing is stored', () => {
    stubWindow({ storage: memoryStorage(), systemDark: true });
    expect(resolveInitialTheme()).toBe('plottr-dark');
  });

  it('returns null when storage throws on read', () => {
    stubWindow({
      storage: {
        getItem: () => {
          throw new Error('private browsing');
        },
        setItem: () => {},
      },
    });
    expect(readStoredTheme()).toBeNull();
  });
});

describe('writeStoredTheme', () => {
  it('persists the chosen theme under THEME_STORAGE_KEY', () => {
    const storage = stubWindow();
    writeStoredTheme('plottr-dark');
    expect(storage.getItem(THEME_STORAGE_KEY)).toBe('plottr-dark');
  });

  it('swallows a throwing storage (private mode) without throwing', () => {
    stubWindow({
      storage: {
        getItem: () => null,
        setItem: () => {
          throw new Error('quota exceeded');
        },
      },
    });
    expect(() => writeStoredTheme('plottr-dark')).not.toThrow();
  });

  it('is a no-op when there is no window', () => {
    expect(() => writeStoredTheme('plottr-light')).not.toThrow();
  });
});

describe('applyTheme', () => {
  /** Duck-typed document that records every DOM mutation applyTheme performs. */
  function stubDocument(): {
    root: { dataset: Record<string, string>; style: { colorScheme: string } };
    metaContent: { value: string | null };
  } {
    const root = { dataset: {} as Record<string, string>, style: { colorScheme: '' } };
    const metaContent = { value: null as string | null };
    vi.stubGlobal('document', {
      documentElement: root,
      querySelector: (selector: string) =>
        selector.includes('theme-color')
          ? {
              setAttribute: (_name: string, value: string) => {
                metaContent.value = value;
              },
            }
          : null,
    });
    return { root, metaContent };
  }

  it('writes data-theme, color-scheme and theme-color for dark', () => {
    const { root, metaContent } = stubDocument();
    applyTheme('plottr-dark');
    expect(root.dataset.theme).toBe('plottr-dark');
    expect(root.style.colorScheme).toBe('dark');
    expect(metaContent.value).toBe('#1f2937');
  });

  it('writes the light values too', () => {
    const { root, metaContent } = stubDocument();
    applyTheme('plottr-light');
    expect(root.dataset.theme).toBe('plottr-light');
    expect(root.style.colorScheme).toBe('light');
    expect(metaContent.value).toBe('#059669');
  });

  it('never throws when there is no document', () => {
    expect(() => applyTheme('plottr-dark')).not.toThrow();
  });
});
