import { Game } from "./core/Game";

const canvas = document.getElementById("game") as HTMLCanvasElement;
const startBtn = document.getElementById("start-btn") as HTMLButtonElement;
const roleSel = document.getElementById("role") as HTMLSelectElement;
const nameInput = document.getElementById("name") as HTMLInputElement;
const overlay = document.getElementById("start-overlay") as HTMLDivElement;

let started = false;
startBtn.addEventListener("click", () => {
  if (started) return;
  started = true;

  const role = roleSel.value;
  const name = nameInput.value.trim() || (role === "bigfoot" ? "Bigfoot" : "Searcher");

  const game = new Game(canvas, role, name);
  game.start();

  overlay.style.display = "none";
  canvas.requestPointerLock();
});
