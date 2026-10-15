import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  // Deployed under https://1up.co.in/CIPadmin/ — assets are emitted with this prefix.
  // Dev server still serves at '/' so nothing changes locally.
  base: process.env.VITE_BASE || '/',
  plugins: [react()],
  resolve: {
    alias: { '@pdf-editor': path.resolve(__dirname, '../../../PDF_Editor/src') },
  },
  build: {
    rollupOptions: {
      output: {
        // IIS (8.5 and 10 alike) ships NO MIME mapping for .mjs, so it answers a hard
        // 404 for the PDF.js worker even though the file is on disk — the SPA rewrite
        // never fires precisely because the file EXISTS. PDF.js then logs
        // "Setting up fake worker" and runs the 1.2 MB worker on the main thread.
        // Emit that one asset as .js, which every IIS version maps, instead of
        // depending on server configuration. The else-branch is Vite's own default,
        // so every other asset keeps its normal name.
        assetFileNames: (info: { name?: string; names?: string[] }) => {
          const names: string[] =
            info.names && info.names.length ? info.names : info.name ? [info.name] : [];
          const isMjs = names.some((n) => typeof n === 'string' && n.endsWith('.mjs'));
          return isMjs ? 'assets/[name]-[hash].js' : 'assets/[name]-[hash][extname]';
        },
      },
    },
  },
  server: {
    port: 5173,
    fs: { allow: [path.resolve(__dirname, '../../../PDF_Editor')] },
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
});
