import { defineConfig } from "vite";
import { fileURLToPath } from "node:url";

// Set VITE_SERVER_URL to point the client at a non-default Colyseus server.
export default defineConfig({
  server: { port: 5173 },
  // `@shared/*` → the repo-root `shared/` folder (deterministic world shared with the server).
  // Vite doesn't read tsconfig `paths`, so the alias is declared here too.
  resolve: {
    alias: {
      "@shared": fileURLToPath(new URL("../shared", import.meta.url)),
    },
  },
});
