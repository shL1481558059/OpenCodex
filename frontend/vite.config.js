import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

const backend = process.env.VITE_BACKEND_ORIGIN || "https://localhost:8443";
const adminProxyRoutes = [
  "/session",
  "/login",
  "/logout",
  "/users",
  "/api-keys",
  "/config",
  "/logs",
  "/log-filter-options",
  "/stats",
  "/stats/active-channels",
  "/stats/active-channels/stream",
  "/web-search",
  "/model-providers",
  "/model-infos",
  "/channels",
  "/discover-models",
  "/test-channel"
];
const proxy = Object.fromEntries(
  adminProxyRoutes.map((route) => [
    `/admin${route}`,
    {
      target: backend,
      changeOrigin: true,
      secure: false,
      rewrite: (path) => path.replace(/^\/admin(?=\/)/, "")
    }
  ])
);

export default defineConfig({
  base: "/admin/",
  plugins: [vue()],
  build: {
    outDir: "dist/admin",
    emptyOutDir: true,
    chunkSizeWarningLimit: 550,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes("node_modules")) {
            if (id.includes("echarts")) return "echarts";
            if (id.includes("@element-plus/icons-vue")) return "element-icons";
            if (id.includes("element-plus")) return "element";
            if (id.includes("@popperjs")) return "popper";
            if (id.includes("async-validator")) return "async-validator";
            if (id.includes("vue")) return "vue";
            return;
          }
          // Split each page component into its own chunk
          const pageChunks = [
            "Channels.vue",
            "AccessKeys.vue",
            "Users.vue",
            "WebSearch.vue",
            "Logs.vue",
            "Login.vue",
            "Dashboard.vue"
          ];
          for (const name of pageChunks) {
            if (id.includes(name)) {
              return "page-" + name.replace(".vue", "").toLowerCase();
            }
          }
        }
      }
    }
  },
  server: {
    proxy
  }
});
