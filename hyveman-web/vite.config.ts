/// <reference types="vitest/config" />
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

// The production CSP (index.html) is strict: no inline scripts. Vite's dev
// server injects an inline react-refresh preamble and an HMR websocket, so
// development relaxes script-src/connect-src while the build artifact stays
// strict (FRONTEND.md §11).
function devCsp(): Plugin {
  return {
    name: 'hyveman-dev-csp',
    apply: 'serve',
    transformIndexHtml(html) {
      return html
        .replace("script-src 'self'", "script-src 'self' 'unsafe-inline'")
        .replace("connect-src 'self'", "connect-src 'self' ws:");
    },
  };
}

// The dev server proxies /api to a local hyveman-api by default. Playwright
// e2e runs point it at the bundled mock API via HYVEMAN_API_PROXY.
const apiProxy = process.env.HYVEMAN_API_PROXY ?? 'http://127.0.0.1:5080';

export default defineConfig({
  plugins: [react(), devCsp()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiProxy, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    include: ['src/**/*.test.{ts,tsx}'],
  },
  build: {
    sourcemap: false,
    target: 'es2022',
  },
});
