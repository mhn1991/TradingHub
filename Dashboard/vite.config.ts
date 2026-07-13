import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
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
