import { Game } from "./core/Game";
import { Lobby } from "./core/Lobby";
import { Room } from "colyseus.js";

const canvas = document.getElementById("game") as HTMLCanvasElement;
const startBtn = document.getElementById("start-btn") as HTMLButtonElement;
const lobbyBtn = document.getElementById("lobby-btn") as HTMLButtonElement;
const roleSel = document.getElementById("role") as HTMLSelectElement;
const nameInput = document.getElementById("name") as HTMLInputElement;
const overlay = document.getElementById("start-overlay") as HTMLDivElement;

let started = false;

function launch(role: string, name: string, room?: Room) {
  if (started) return;
  started = true;
  const game = new Game(canvas, role, name, room);
  game.start();
  overlay.style.display = "none";
  canvas.requestPointerLock();
}

// Solo: straight into the world (offline-friendly), role chosen here.
startBtn.addEventListener("click", () => {
  const role = roleSel.value;
  const name = nameInput.value.trim() || (role === "bigfoot" ? "Bigfoot" : "Searcher");
  launch(role, name);
});

// Multiplayer: join the lobby; role is assigned by the server when the host starts.
lobbyBtn.addEventListener("click", async () => {
  if (started) return;
  const name = nameInput.value.trim() || "Searcher";
  lobbyBtn.disabled = true;
  lobbyBtn.textContent = "Connecting…";
  try {
    const handoff = await new Lobby().join(name);
    launch(handoff.role, handoff.name, handoff.room);
  } catch (e) {
    console.warn("Could not reach the lobby — play solo instead.", e);
    lobbyBtn.disabled = false;
    lobbyBtn.textContent = "Multiplayer lobby (server offline)";
  }
});
