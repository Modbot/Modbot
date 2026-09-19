import path from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': path.resolve(__dirname, './src') } },
  build: {
    // Served by Modbot.My from wwwroot: one container, no separate deploy. The folder is build
    // output only and is not committed.
    outDir: '../Modbot.My/wwwroot',
    emptyOutDir: true,
    rollupOptions: {
      // One file per route. They load the same app; they differ only in the head, so the link
      // preview a chat app draws is the one for the page the link opens.
      input: {
        main: path.resolve(__dirname, 'index.html'),
        register: path.resolve(__dirname, 'register.html'),
        go: path.resolve(__dirname, 'go.html'),
      },
    },
  },
  server: {
    proxy: { '/api': 'http://localhost:8080' },
  },
})
