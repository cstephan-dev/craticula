# Craticula demo: web page

A Vite + React + TypeScript page with one AG Grid (server-side row model) that reads the same orders from PostgreSQL or
MongoDB through the demo API. Needs Node 20.19+ and the API on `http://127.0.0.1:5080`. The full quickstart (databases, seed,
API, click-through checklist, troubleshooting) is in [../README.md](../README.md); this file covers the page only.

```bash
npm install
npm run dev        # http://localhost:5173, /api is proxied to the API
npm run lint && npm run test -- --run && npm run build
```

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

Number and date filters offer only the operators the library parses (no `inRange`). The boolean column is a set filter on
`"true"` / `"false"`. Set-filter values come from `/api/<provider>/values` and are loaded again each time a filter opens.
 The CSV button posts the current filter and sort to `/api/<provider>/export.csv`.
