import { describe, expect, it } from 'vitest';
import { SIDE_BAR } from './gridOptions';

describe('the side bar', () => {
  const columns = SIDE_BAR.toolPanels?.find((panel) => typeof panel !== 'string' && panel.id === 'columns');

  it('keeps the Columns panel', () => {
    expect(columns).toBeDefined();
  });

  it('hides the Pivot Mode switch and the pivot drop zone, because the library refuses pivot requests (AG-5)', () => {
    expect(columns).toMatchObject({ toolPanelParams: { suppressPivotMode: true, suppressPivots: true } });
  });

  it('keeps the Filters panel', () => {
    expect(SIDE_BAR.toolPanels).toContain('filters');
  });
});
