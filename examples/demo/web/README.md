# Craticula demo: web page

A Vite + React + TypeScript page with one AG Grid (server-side row model) that reads the same orders from PostgreSQL or
MongoDB through the demo API. Needs Node 20.19+ and the API on `http://127.0.0.1:5080`. The full quickstart (databases, seed,
API, click-through checklist, troubleshooting) is in [../README.md](../README.md); this file covers the page only.

```bash
npm install
npm run dev        # http://localhost:5173, /api is proxied to the API
npm run lint && npm run test -- --run && npm run build
```

## Browser tests

`e2e/` holds Playwright tests that drive this page in Chromium against a running demo (databases seeded, API on 5080, `npm run dev`). They are local only:
`npm run e2e` is not part of `npm run test` (vitest skips `e2e/`) and no pipeline runs it.

```bash
npm run e2e:install                       # once: downloads Chromium (about 150 to 300 MB, needs internet)
npm run e2e                               # every provider the API lists; the others are skipped with a reason
E2E_DESTRUCTIVE=1 npm run e2e             # also the error banners: stops and restarts database containers
npx playwright test enterprise-grouping   # one spec; -g "mysql" for one provider
```

- Settings: `DEMO_WEB_URL` (default `http://localhost:5173`), `DEMO_API_URL` (default `http://127.0.0.1:5080`). Viewport 1920 wide (the grid draws only the
  columns in view), UTC time zone, `en-US`.
- Layout: `e2e/*.spec.ts` are the scenarios; `e2e/pages/grid.ts` is the only file that knows AG Grid's DOM (selectors); `e2e/support/` holds the drivers
  (`actions.ts`), the capture of the rows the grid received (`rows.ts`), the comparison with PostgreSQL (`compare.ts`) and the list of accepted differences
  with their reasons (`divergences.ts`). A new provider needs a line in `src/api.ts` and, where its behaviour differs, a rule there.
- [e2e/COVERAGE.md](e2e/COVERAGE.md) says which scenario each spec covers and what is not automated. The demo README ([../README.md](../README.md#browser-tests-optional))
  has the full instructions, including the two-batch run for all ten providers (about 10 GB of RAM).
- Output goes to `test-results/` and `playwright-report/`, both git-ignored.

## The Community page

`community.html` is a second Vite entry (`src/community/`) that imports only `ag-grid-community` and `ag-grid-react`: the
Infinite Row Model, text, number and date filters, no set filter, no grouping, no license key. A test
(`communityImports.test.ts`) walks its imports and fails if anything reaches `ag-grid-enterprise`. The built Community entry
shares one chunk with the main page that holds the Community core; the Enterprise code is only in the main page's chunk.
`npm run build` builds both entries.

## AG Grid version and license

- AG Grid 36.2.0, pinned exactly: `ag-grid-community`, `ag-grid-enterprise` and `ag-grid-react` (the React wrapper is a
  third AG Grid package). The library supports AG Grid v31 to v36; this demo uses the newest.
- The server-side row model is an Enterprise feature. Without a license key the grid runs in **evaluation mode**: a
  watermark and console warnings are expected. No key is stored in this repository.
- To remove the watermark with your own key, create `web/.env.local` (gitignored):

  ```
  VITE_AG_GRID_LICENSE=<your key>
  ```

## What the page sends

The grid's own request goes to `/api/<postgres|mongo>/rows` with two adaptations; anyone copying this demo must
reproduce both:

- Going out, `groupKeys` are wrapped as typed values (`{ "type": "string", "value": "Germany" }`, or `{ "type": "null" }`)
  because the library parses typed values, not the plain strings the grid holds.
- Coming back, aggregate result keys `<colId>_<aggFunc>` (`amount_sum`) are renamed to the column id (`amount`), because
  the grid reads an aggregate from the column's field.

Number and date filters offer "In range" too: `src/inRange.ts` translates it into `customFilters` with exclusive bounds before the request is sent (see the demo README, section "The In range filter"). The boolean column is a set filter on
`"true"` / `"false"`. Set-filter values come from `/api/<provider>/values` and are loaded again each time a filter opens.
 The CSV button posts the current filter and sort to `/api/<provider>/export.csv`.
