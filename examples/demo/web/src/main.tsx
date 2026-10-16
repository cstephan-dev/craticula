import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { AllEnterpriseModule, LicenseManager, ModuleRegistry } from 'ag-grid-enterprise';
import { App } from './App';
import './styles.css';

// Evaluation mode unless a key is supplied locally (gitignored .env.local). The key never lives in the repository.
const licenseKey = import.meta.env.VITE_AG_GRID_LICENSE;
if (licenseKey) {
  LicenseManager.setLicenseKey(licenseKey);
}

ModuleRegistry.registerModules([AllEnterpriseModule]);

const container = document.getElementById('root');
if (!container) {
  throw new Error('Missing #root element');
}

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
