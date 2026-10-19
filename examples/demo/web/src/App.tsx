import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AgGridReact } from 'ag-grid-react';
import type { ColDef, GridApi, GridReadyEvent } from 'ag-grid-enterprise';
import { ApiError, fetchExport, fetchHealth, providerToSelect, visibleProviders, type HealthResponse, type Provider } from './api';
import { buildColumnDefs, type GridContext } from './columns';
import { asApiError, createDatasource, createProviderGate } from './datasource';
import { buildExportRequest } from './exportRequest';
import { PAGE_SIZE, SIDE_BAR } from './gridOptions';
import { ErrorBanner } from './components/ErrorBanner';
import { ProviderSwitch } from './components/ProviderSwitch';
import { QueryPanel, type CompiledQuery } from './components/QueryPanel';

type Health = 'checking' | 'ok' | 'down';

// Module-level: ag-grid-react compares props by reference, so an inline object or array would be re-applied on every
// render (the side bar would jump back to its first panel).
const DEFAULT_COL_DEF: ColDef = { sortable: true, filter: true, resizable: true };
const AUTO_GROUP_COLUMN_DEF: ColDef = { minWidth: 220, headerName: 'Group' };

export function App() {
  const [provider, setProvider] = useState<Provider>('postgres');
  const [health, setHealth] = useState<Health>('checking');
  const [reported, setReported] = useState<HealthResponse | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [panelOpen, setPanelOpen] = useState(true);
  const [paged, setPaged] = useState(false);
  const [query, setQuery] = useState<CompiledQuery | null>(null);
  const [exporting, setExporting] = useState(false);

  const apiRef = useRef<GridApi | null>(null);
  // The provider on screen, for the datasources to ask (switchProvider is the only writer).
  const [gate] = useState(() => createProviderGate('postgres'));

  const columnDefs = useMemo(() => buildColumnDefs({ onError: setError }), []);
  // The set-filter callbacks read the provider from the grid context, so the column definitions never change.
  const gridContext = useMemo<GridContext>(() => ({ provider }), [provider]);
  const fieldIds = useMemo(() => new Set(columnDefs.flatMap((c) => ('field' in c && c.field ? [c.field] : []))), [columnDefs]);

  // A new datasource object per provider makes the grid reload; filters, sort and grouping stay as they are. Each one
  // knows its own provider: once the user switches, its late answers are ignored.
  const datasource = useMemo(
    () =>
      createDatasource(
        provider,
        {
          onRowsError: setError,
          onQuery: (request, view) => setQuery({ request, view }),
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

  useEffect(() => {
    let cancelled = false;
    fetchHealth().then(
      (answer) => {
        if (!cancelled) {
          setReported(answer);
          // The API may serve fewer providers than the page knows: never stay on one that is not offered.
          const target = providerToSelect(gate.current(), visibleProviders(answer));
          if (target !== gate.current()) {
            switchProvider(target);
          }
          setHealth('ok');
        }
      },
      () => !cancelled && setHealth('down'),
    );
    return () => {
      cancelled = true;
    };
  }, [gate, switchProvider]);

  const visible = useMemo(() => visibleProviders(reported), [reported]);

  const onGridReady = useCallback((event: GridReadyEvent) => {
    apiRef.current = event.api;
  }, []);

  const exportCsv = useCallback(async () => {
    const api = apiRef.current;
    if (!api) {
      return;
    }
    setExporting(true);
    try {
      const body = buildExportRequest(api.getFilterModel(), api.getColumnState(), fieldIds);
      const blob = await fetchExport(provider, body);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `orders-${provider}.csv`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setExporting(false);
    }
  }, [provider, fieldIds]);

  return (
    <div className="app">
      <header className="header">
        <div className="title">
          <h1>Craticula demo</h1>
          <p className="muted">AG Grid Enterprise evaluation mode: watermark expected</p>
        </div>
        <div className="controls">
          <ProviderSwitch provider={provider} onChange={switchProvider} providers={visible} />
          <span className={`health ${health}`} title="GET /api/health">
            API: {health === 'ok' ? 'up' : health === 'down' ? 'unreachable' : 'checking'}
          </span>
          <button type="button" onClick={() => void exportCsv()} disabled={exporting}>
            {exporting ? 'Exporting...' : 'Export CSV'}
          </button>
          <button type="button" aria-pressed={paged} onClick={() => setPaged((on) => !on)} title="Numbered pages instead of infinite scroll">
            Pages
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
            rowModelType="serverSide"
            serverSideDatasource={datasource}
            columnDefs={columnDefs}
            context={gridContext}
            defaultColDef={DEFAULT_COL_DEF}
            autoGroupColumnDef={AUTO_GROUP_COLUMN_DEF}
            rowGroupPanelShow="always"
            sideBar={SIDE_BAR}
            pagination={paged}
            paginationPageSize={PAGE_SIZE}
            paginationPageSizeSelector={false}
            cacheBlockSize={PAGE_SIZE}
            maxBlocksInCache={10}
            serverSideInitialRowCount={100}
            onGridReady={onGridReady}
          />
        </div>
        {panelOpen && <QueryPanel query={query} />}
      </main>
    </div>
  );
}
