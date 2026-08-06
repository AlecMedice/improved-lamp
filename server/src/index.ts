import { Server } from "@colyseus/core";
import { WebSocketTransport } from "@colyseus/ws-transport";
import { createServer } from "http";
import express from "express";
import { MountainRoom } from "./rooms/MountainRoom";

const port = Number(process.env.PORT) || 2567;

const app = express();
app.use(express.json());
app.get("/health", (_req, res) => res.json({ ok: true, game: "metoh" }));

const gameServer = new Server({
  transport: new WebSocketTransport({ server: createServer(app) }),
});

gameServer.define("mountain", MountainRoom);

gameServer
  .listen(port)
  .then(() => console.log(`🌲 Metoh server listening on ws://localhost:${port}`))
  .catch((err) => {
    console.error("Failed to start server:", err);
    process.exit(1);
  });
