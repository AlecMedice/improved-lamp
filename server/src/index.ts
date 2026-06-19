import { Server } from "@colyseus/core";
import { WebSocketTransport } from "@colyseus/ws-transport";
import { createServer } from "http";
import express from "express";
import { ForestRoom } from "./rooms/ForestRoom";

const port = Number(process.env.PORT) || 2567;

const app = express();
app.use(express.json());
app.get("/health", (_req, res) => res.json({ ok: true, game: "hollow-pines" }));

const gameServer = new Server({
  transport: new WebSocketTransport({ server: createServer(app) }),
});

gameServer.define("forest", ForestRoom);

gameServer
  .listen(port)
  .then(() => console.log(`🌲 Hollow Pines server listening on ws://localhost:${port}`))
  .catch((err) => {
    console.error("Failed to start server:", err);
    process.exit(1);
  });
