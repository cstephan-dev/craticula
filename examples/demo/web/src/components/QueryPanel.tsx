import { useState } from 'react';
import { ApiError, type QueryView } from '../api';

export interface CompiledQuery {
  /** What the grid sent (the server-side page and the Community page send different shapes); the panel does not read it. */
  request: unknown;
  view: QueryView | ApiError;
}

interface Props {
  query: CompiledQuery | null;
}

export function QueryPanel({ query }: Props) {
  const [copied, setCopied] = useState(false);
  const view = query?.view;

  const copy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      setCopied(false);
    }
  };

  return (
    <aside className="query-panel" aria-label="Compiled query">
      <h2>Compiled query</h2>
      {!view && <p className="muted">Waiting for the first request.</p>}
      {view instanceof ApiError && (
        <p className="panel-error">
          <strong>{view.kind}</strong>: {view.message}
        </p>
      )}
      {view && !(view instanceof ApiError) && (
        <>
          <p>
            <span className="tag">{view.kind}</span> <span className="muted">{view.dialect}</span>
          </p>
          <pre>{view.text}</pre>
          <h3>Parameters ({view.parameters.length})</h3>
          <pre>{JSON.stringify(view.parameters, null, 2)}</pre>
          <button type="button" onClick={() => void copy(view.text)}>
            {copied ? 'Copied' : 'Copy query'}
          </button>
        </>
      )}
    </aside>
  );
}
