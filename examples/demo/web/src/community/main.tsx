import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import { CommunityApp } from './CommunityApp';
import '../styles.css';

// Only the free Community modules: no license key, no watermark. communityImports.test.ts keeps ag-grid-enterprise out of this entry.
ModuleRegistry.registerModules([AllCommunityModule]);

const container = document.getElementById('root');
if (!container) {
  throw new Error('Missing #root element');
}

createRoot(container).render(
  <StrictMode>
    <CommunityApp />
  </StrictMode>,
);
