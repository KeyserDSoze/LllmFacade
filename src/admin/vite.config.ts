import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const api = process.env.LLLMFACADE_API_URL || 'http://127.0.0.1:5080';

// The production build is served by the ASP.NET Core API (same origin); in development Vite proxies to it.
export default defineConfig({
  plugins: [react()],
  base: '/',
  server: {
    host: '127.0.0.1',
    port: 5174,
    proxy: { '/api': api, '/v1': api, '/health': api }
  }
});
