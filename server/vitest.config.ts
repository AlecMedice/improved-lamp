import { defineConfig } from "vitest/config";

// Server-side test suite. Covers the pure server-authority helpers (antiCheat) and the shared
// deterministic sim that both client and server rely on. Tests deliberately avoid importing the
// Colyseus Room/schema (legacy decorators) so esbuild needs no decorator handling — the anti-cheat
// math and the sim are pure and testable on their own.
export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    environment: "node",
  },
});
