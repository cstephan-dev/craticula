import { describe, expect, it } from 'vitest';

// Every source file of the page, read as text (Vite's glob import; no Node types needed).
const sources = import.meta.glob<string>('/src/**/*.{ts,tsx}', { query: '?raw', import: 'default', eager: true });

/** Every module specifier of the static imports and re-exports in a source file (type-only ones included). */
function specifiers(source: string): string[] {
  return [...source.matchAll(/(?:import|export)\s[^;]*?from\s+['"]([^'"]+)['"]|import\s+['"]([^'"]+)['"]/g)].map((m) => m[1] ?? m[2]);
}

/** '/src/community/main.tsx' + '../api' -> '/src/api' */
function join(from: string, specifier: string): string {
  const parts = from.split('/').slice(0, -1);
  for (const part of specifier.split('/')) {
    if (part === '..') {
      parts.pop();
    } else if (part !== '.') {
      parts.push(part);
    }
  }
  return parts.join('/');
}

function resolveLocal(from: string, specifier: string): string | undefined {
  const base = join(from, specifier);
  return [`${base}.ts`, `${base}.tsx`, `${base}/index.ts`, `${base}/index.tsx`].find((candidate) => candidate in sources);
}

/** Everything reachable from an entry through relative imports, and every package it names. */
function reachable(entry: string): { files: string[]; packages: string[] } {
  const files = new Set<string>();
  const packages = new Set<string>();
  const queue = [entry];
  while (queue.length > 0) {
    const file = queue.pop() as string;
    if (files.has(file)) {
      continue;
    }
    files.add(file);
    for (const specifier of specifiers(sources[file])) {
      if (specifier.startsWith('.')) {
        const next = resolveLocal(file, specifier);
        if (next) {
          queue.push(next);
        }
      } else {
        packages.add(specifier);
      }
    }
  }
  return { files: [...files], packages: [...packages] };
}

describe('the Community entry', () => {
  const { files, packages } = reachable('/src/community/main.tsx');

  it('reaches no ag-grid-enterprise module (AG-6: the page runs without a license)', () => {
    expect(packages.filter((name) => name.startsWith('ag-grid-enterprise'))).toEqual([]);
  });

  it('uses the Community packages and walks the files it should', () => {
    expect(packages).toContain('ag-grid-community');
    expect(packages).toContain('ag-grid-react');
    expect(files).toContain('/src/community/CommunityApp.tsx');
    expect(files).toContain('/src/api.ts');
  });

  it('would notice an Enterprise import: the scanner finds one in a synthetic source, and the main entry has one', () => {
    expect(specifiers("import type { X } from 'ag-grid-enterprise';\nexport { y } from './y';")).toEqual([
      'ag-grid-enterprise',
      './y',
    ]);
    expect(reachable('/src/main.tsx').packages).toContain('ag-grid-enterprise');
  });
});
