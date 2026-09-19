import path from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

const here = import.meta.dirname

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': path.resolve(here, './src') } },
  build: {
    // Served by Modbot.Landing from wwwroot: one container, no separate deploy. The folder is build
    // output only and is not committed. scripts/prerender.ts then writes the rendered pages into it.
    outDir: '../wwwroot',
    emptyOutDir: true,
    rollupOptions: {
      input: Object.fromEntries(
        ['index', '404', 'features', 'self-host', 'about', 'license', 'privacy', 'discord', 'instances'].map(
          (page) => [page, path.resolve(here, `${page}.html`)],
        ),
      ),
    },
  },
})
