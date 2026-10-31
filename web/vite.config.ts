import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

const api = process.env.API_URL ?? 'http://localhost:5298';

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': api, '/hubs': { target: api, ws: true } } },
  build: { outDir: '../src/PositionTracker.Api/wwwroot', emptyOutDir: true },
  test: { environment: 'jsdom', globals: true, setupFiles: ['./src/test-setup.ts'] },
});
