import { resolve } from 'node:path'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  build: {
    rollupOptions: {
      input: {
        main: resolve(__dirname, 'index.html'),
        agentDebug: resolve(__dirname, 'agent-debug.html'),
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      // LiveTradingHost runs as its own process on its own port - these more specific prefixes
      // must be registered before the bare '/api'/'/hubs' entries below so they match first.
      '/api/live-host': {
        target: 'http://127.0.0.1:5088',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api\/live-host/, '/api/live'),
      },
      '/hubs/live': {
        target: 'http://127.0.0.1:5088',
        changeOrigin: true,
        ws: true,
      },
      '/api': {
        target: 'http://127.0.0.1:5180',
        changeOrigin: true,
      },
      // SignalR hub (WebSockets + negotiate) for Simulator live progress.
      '/hubs': {
        target: 'http://127.0.0.1:5180',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
