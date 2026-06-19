import { defineConfig } from "vite";

// Set VITE_SERVER_URL to point the client at a non-default Colyseus server.
export default defineConfig({
  server: { port: 5173 },
});
