import type { SideBarDef } from 'ag-grid-enterprise';

// The Columns panel without the Pivot Mode switch and the Pivot drop zone: the library refuses pivot requests (AG-5),
// so the page must not offer them. Module-level, because ag-grid-react compares props by reference.
export const SIDE_BAR: SideBarDef = {
  toolPanels: [
    {
      id: 'columns',
      labelDefault: 'Columns',
      labelKey: 'columns',
      iconKey: 'columns',
      toolPanel: 'agColumnsToolPanel',
      toolPanelParams: { suppressPivotMode: true, suppressPivots: true },
    },
    'filters',
  ],
};
