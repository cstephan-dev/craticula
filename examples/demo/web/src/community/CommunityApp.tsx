import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AgGridReact } from 'ag-grid-react';
import type { ColDef, GridApi, GridReadyEvent } from 'ag-grid-community';
import { ApiError, fetchHealth, type Provider } from '../api';
import { createProviderGate } from '../providerGate';
import { ErrorBanner } from '../components/ErrorBanner';
import { ProviderSwitch } from '../components/ProviderSwitch';
import { QueryPanel, type CompiledQuery } from '../components/QueryPanel';
import { COMMUNITY_COLUMNS } from './columns';
import { createFlatDatasource, requestToClipboardText, type FlatRequest } from './flatRequest';

type Health = 'checking' | 'ok' | 'down';

// Module-level: ag-grid-react compares props by reference.
const DEFAULT_COL_DEF: ColDef = { sortable: true, filter: true, resizable: true };

export function CommunityApp() {
  const [provider, setProvider] = useState<Provider>('postgres');
  const [health, setHealth] = useState<Health>('checking');
  const [error, setError] = useState<ApiError | null>(null);
  const [panelOpen, setPanelOpen] = useState(true);
  const [query, setQuery] = useState<CompiledQuery | null>(null);
  const [copied, setCopied] = useState(false);

  const apiRef = useRef<GridApi | null>(null);
  const [gate] = useState(() => createProviderGate('postgres'));

  useEffect(() => {
    let cancelled = false;
    fetchHealth().then(
      () => !cancelled && setHealth('ok'),
      () => !cancelled && setHealth('down'),
    );
    return () => {
      cancelled = true;
    };
  }, []);

  const datasource = useMemo(
    () =>
      createFlatDatasource(
        provider,
        {
          onRowsError: setError,
          onQuery: (request, view) => setQuery({ request, view: view as CompiledQuery['view'] }),
        },
        () => gate.isCurrent(provider),
      ),
    [provider, gate],
  );

  const switchProvider = useCallback((next: Provider) => {
    gate.select(next);
    setError(null);
    setProvider(next);
  }, [gate]);

  const onGridReady = useCallback((event: GridReadyEvent) => {
    apiRef.current = event.api;
  }, []);

  const copyRequest = useCallback(async () => {
    const request = query?.request as FlatRequest | undefined;
    if (!request) {
      return;
    }
    try {
      await navigator.clipboard.writeText(requestToClipboardText(request));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      setCopied(false);
    }
  }, [query]);

  return (
    <div className="app">
      <header className="header">
        <div className="title">
          <h1>Craticula demo: AG Grid Community</h1>
          <p className="muted">
            Infinite Row Model, no license key, no watermark. <a href="/">Enterprise page</a>
          </p>
        </div>
        <div className="controls">
          <ProviderSwitch provider={provider} onChange={switchProvider} />
          <span className={`health ${health}`} title="GET /api/health">
            API: {health === 'ok' ? 'up' : health === 'down' ? 'unreachable' : 'checking'}
          </span>
          <button type="button" onClick={() => void copyRequest()} title="The exact request body the grid last sent">
            {copied ? 'Copied' : 'Copy request'}
          </button>
          <button type="button" aria-pressed={panelOpen} onClick={() => setPanelOpen((open) => !open)}>
            Query panel
          </button>
        </div>
      </header>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />
      <main className="content">
        <div className="grid">
          <AgGridReact
            rowModelType="infinite"
            datasource={datasource}
            columnDefs={COMMUNITY_COLUMNS}
            defaultColDef={DEFAULT_COL_DEF}
            cacheBlockSize={100}
            maxBlocksInCache={10}
            infiniteInitialRowCount={100}
            onGridReady={onGridReady}
          />
        </div>
        {panelOpen && <QueryPanel query={query} />}
      </main>
    </div>
  );
}
