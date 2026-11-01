import { defineConfig } from 'vitest/config';
import type { Plugin } from 'vite';
import react from '@vitejs/plugin-react';

// The entry links people open from a message (/e/<id> spectator, /p participant): the chunks of their page and the CSS those chunks need,
// written into <meta> tags so public/boot.js can start downloading them in parallel with the entry bundle instead of after it.
function routePreload(): Plugin {
  const routes: Record<string, string> = { e: 'pages/Spectator.tsx', p: 'pages/Participant.tsx' };
  let base = '/';
  return {
    name: 'route-preload',
    apply: 'build',
    configResolved(c) { base = c.base; },
    transformIndexHtml: {
      order: 'post',
      handler(_html, ctx) {
        const bundle = ctx.bundle;
        if (!bundle) return;
        const chunks = Object.values(bundle).filter((o) => o.type === 'chunk');
        const closure = (names: string[], seen = new Set<string>()): Set<string> => {
          for (const n of names) {
            const c = bundle[n];
            if (c?.type !== 'chunk' || seen.has(n)) continue;
            seen.add(n);
            closure(c.imports, seen);
          }
          return seen;
        };
        const entry = chunks.find((c) => c.isEntry);
        const inEntry = entry ? closure([entry.fileName]) : new Set<string>();
        const tags: { tag: 'meta'; attrs: Record<string, string>; injectTo: 'head-prepend' }[] = [];
        for (const [route, page] of Object.entries(routes)) {
          const chunk = chunks.find((c) => c.facadeModuleId?.replaceAll('\\', '/').endsWith('/src/' + page));
          if (!chunk) continue;
          const files = [...closure([chunk.fileName])].filter((f) => !inEntry.has(f));
          const css = files.flatMap((f) => { const c = bundle[f]; return c?.type === 'chunk' ? [...(c.viteMetadata?.importedCss ?? [])] : []; });
          tags.push({ tag: 'meta', attrs: { name: `pt-js-${route}`, content: files.map((f) => base + f).join(' ') }, injectTo: 'head-prepend' });
          tags.push({ tag: 'meta', attrs: { name: `pt-css-${route}`, content: css.map((f) => base + f).join(' ') }, injectTo: 'head-prepend' });
        }
        return tags;
      },
    },
  };
}

const api = process.env.API_URL ?? 'http://localhost:5298';

export default defineConfig({
  base: process.env.VITE_BASE ?? '/',
  plugins: [react(), routePreload()],
  server: { port: 5173, proxy: { '/api': api, '/hubs': { target: api, ws: true } } },
  build: {
    outDir: '../src/PositionTracker.Api/wwwroot',
    emptyOutDir: true,
    rollupOptions: {
      // Leaflet (the bulk of the map pages) in a chunk of its own: loaded only by routes that show a map, cached across deployments
      output: {
        manualChunks: (id) => {
          if (id.includes('node_modules/leaflet/')) return 'leaflet';
          // the tiny CommonJS helper is used by Leaflet and by the QR library: its own chunk, or the participant page would pull in Leaflet
          if (id.includes('commonjsHelpers')) return 'cjs';
          return undefined;
        },
      },
    },
  },
  test: { environment: 'jsdom', globals: true, setupFiles: ['./src/test-setup.ts'] },
});
