import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "VITE_");
  if (mode === "production" && env.VITE_DATA_ADAPTER === "mock") throw new Error("Production builds prohibit VITE_DATA_ADAPTER=mock.");
  return {
    plugins: [react()],
    build: {
      outDir: "dist",
      sourcemap: true,
    },
    server: {
      port: 5173,
      proxy: {
        "/api": "http://localhost:5080",
      },
    },
    test: {
      environment: "jsdom",
      setupFiles: "./src/test/setup.ts",
      css: true,
      include: ["src/**/*.{test,spec}.{ts,tsx}"],
    },
  };
});