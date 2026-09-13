import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5174,
    watch: {
      // 编辑器/工具写文件时会先建一个临时目录再原子替换，目录名形如
      // `.style.css.<pid>.<uuid>.tmpdir`。Vite 的 chokidar 会去 watch 里面的临时文件，
      // Windows 上那个文件经常已被删掉/仍被占用，直接抛 EBUSY 把整个 dev server 带崩。
      // 这些中间产物没有任何监听价值，直接忽略。
      ignored: ['**/.*.tmpdir/**', '**/*.tmp', '**/dist/**']
    },
    proxy: {
      '/api': {
        target: 'http://localhost:5100',
        changeOrigin: true
      }
    }
  }
})
