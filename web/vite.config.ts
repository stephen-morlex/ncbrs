/// <reference types="vitest/config" />
import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    // Mirrors the "@/*" path in tsconfig.app.json. Both are needed and they
    // must agree: TypeScript resolves the alias for the type check, Vite
    // resolves it for the bundle, and neither reads the other's config.
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],

    // Generated output and vendored components are not ours to test: the
    // first is regenerated from the OpenAPI documents and checked by the
    // contract job, the second is upstream's. Counting them would put a
    // coverage figure on code nobody here writes.
    coverage: {
      exclude: ['src/api/generated/**', 'src/components/ui/**', 'src/test/**'],
    },
  },
})
