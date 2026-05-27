import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  base: "/admin/",
  plugins: [vue()],
  build: {
    outDir: "../opencodex_proxy/static/admin",
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks: {
          vue: ["vue"],
          element: ["element-plus", "@element-plus/icons-vue"]
        }
      }
    }
  },
  server: {
    proxy: {
      "/admin/api": "http://127.0.0.1:8000"
    }
  }
});
