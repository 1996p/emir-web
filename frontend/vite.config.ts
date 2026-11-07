import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    // Запросы к API идут через тот же адрес, поэтому cookie входа работает без CORS
    proxy: { '/api': 'http://localhost:5080' },
  },
  // Сборка для показа другим компьютерам сети: тот же переход к API, сам API наружу не открывается.
  // Сборка для показа: слушает только этот компьютер. Наружу её выводит туннель ngrok, поэтому разрешены его адреса.
  preview: {
    proxy: { '/api': 'http://localhost:5080' },
    allowedHosts: ['.ngrok-free.app', '.ngrok-free.dev', '.ngrok.app', '.ngrok.io'],
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
