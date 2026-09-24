/// <reference types="vitest/config" />
import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

/**
 * Packages that change on their own release schedule rather than ours.
 * Anything here is a deliberate choice: adding a package that we do change
 * often would defeat the split by invalidating the chunk anyway.
 */
const Stable = [
  'react',
  'react-dom',
  'react-router',
  'oidc-client-ts',
  'react-oidc-context',
  'scheduler',
]

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    // Mirrors the "@/*" path in tsconfig.app.json. Both are needed and they
    // must agree: TypeScript resolves the alias for the type check, Vite
    // resolves it for the bundle, and neither reads the other's config.
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
    },
  },
  build: {
    rollupOptions: {
      output: {
        // React, the router and the OIDC client in their own chunk.
        //
        // Not about first load -- they are needed immediately either way --
        // but about the second visit. Application code changes on every
        // deploy and these do not, so keeping them apart means a district
        // office that opens this daily re-downloads the part that changed
        // and keeps the part that did not. On the links this system is built
        // for, that is the difference that recurs.
        // A function rather than the object form: rolldown, which Vite 8
        // bundles with, only accepts the former.
        manualChunks(id: string) {
          if (!id.includes('node_modules')) {
            return undefined
          }

          return Stable.some((name) => id.includes(`node_modules/${name}/`))
            ? 'vendor'
            : undefined
        },
      },
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
