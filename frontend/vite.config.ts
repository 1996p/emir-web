import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    // Запросы к API идут через тот же адрес, поэтому cookie входа работает без CORS
    proxy: { '/api': 'http://localhost:5080' },
  },
});
