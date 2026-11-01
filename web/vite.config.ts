import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

const api = process.env.API_URL ?? 'http://localhost:5298';

export default defineConfig({
  base: process.env.VITE_BASE ?? '/',
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': api, '/hubs': { target: api, ws: true } } },
  build: {
    outDir: '../src/PositionTracker.Api/wwwroot',
    emptyOutDir: true,
    rollupOptions: {
      // Leaflet (the bulk of the map pages) in a chunk of its own: loaded only by routes that show a map, cached across deployments
      output: { manualChunks: (id) => (id.includes('node_modules/leaflet/') ? 'leaflet' : undefined) },
    },
  },
  test: { environment: 'jsdom', globals: true, setupFiles: ['./src/test-setup.ts'] },
});
