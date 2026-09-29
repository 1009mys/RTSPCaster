import process from 'node:process'
import { defineConfig, loadEnv } from 'vite'
import plugin from '@vitejs/plugin-vue'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.RTSPCASTER_BACKEND_URL || 'http://127.0.0.1:5058'
  const proxy = {
    '/api': {
      target,
      // Preserve Host so same-origin browser requests pass Backend's origin policy.
      changeOrigin: false,
      // SSE stays open; buffering/time limits belong to the production proxy.
      timeout: 0,
      proxyTimeout: 0,
    },
  }
  return {
    plugins: [plugin()],
    server: { host: '127.0.0.1', port: 4848, strictPort: true, proxy },
    preview: { host: '127.0.0.1', port: 4848, strictPort: true, proxy },
  }
})
