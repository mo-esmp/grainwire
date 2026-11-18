import { defineConfig } from "vite";

// `npm run build` writes the site into the server's wwwroot, so `dotnet run` serves it.
// `npm run dev` serves it with hot reload and forwards /mqtt to the server on port 5200.
export default defineConfig({
  build: {
    outDir: "../Chess.Server/wwwroot",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/mqtt": { target: "ws://localhost:5200", ws: true },
    },
  },
});
