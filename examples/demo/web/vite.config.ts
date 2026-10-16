import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The API listens on 127.0.0.1:5080; proxying /api keeps the browser on one origin, so no CORS is involved in dev.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:5080',
    },
  },
  // Two entries: the Enterprise page (index.html) and the Community-only page (community.html).
  build: {
    rollupOptions: {
      input: {
        main: 'index.html',
        community: 'community.html',
      },
    },
  },
  test: {
    environment: 'node',
  },
});
