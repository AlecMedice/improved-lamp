# Jul 10 — Where we are + a quick test plan

A snapshot of what's been built this session and a hands-on checklist to try it later. All of this is
on the branch `claude/fable-latest-plan-m8fd98` (not merged). Nothing here needs a special build — just
run the game normally.

---

## Where we are now

**Shipped this session (all committed + pushed, CI-green):**

1. **Server-authority hardening (integrity).**
   - Filming (the hunter win) is recomputed server-side — range + aim cone + line-of-sight — so `inView`
     from the client is just a hint. No more filming Bigfoot through walls / while facing away.
   - Battery/stamina run through a server envelope (battery only decreases; stamina can't regen faster
     than the sim). Speed gate is now a time-charged token bucket (move-spam earns no free distance).
   - The `?devRole` backdoor is gated off in production (`ALLOW_DEV_ROLE`).

2. **Tests + CI.** A vitest suite (35 tests: shared-sim determinism, anti-cheat helpers, specialty
   deal/getters) runs in CI alongside the typechecks.

3. **Story.** `docs/STORY.md` reworked (five friends / cryptozoology quest, broadened legend, 8pm→8am
   nights) and `docs/CHARACTER_FUNC_DEV.md` written (the specialty design + build plan).

4. **Characters — the searcher specialties.**
   - **Enabling layer:** each searcher is dealt a random, distinct character at match start (name shown
     in the HUD). A debug switch (`?devSpecialty=…` + the `\` key) forces/cycles personas for testing.
   - **Sam / Wren / Theo specialties are live** (see the checklist below).

**Not built yet (next steps):**
- **Eli** — longer film range + a flash ability (stun/reveal). *(step 3)*
- **Mara** — intentionally identity-only until a non-film evidence system exists. *(step 4)*
- **Sam's battery hand-off** — waits on battery pickups. *(step 4)*

---

## Setup (2+ tabs)

See `docs/TEST_PLAN.md` §2–3 for the full multi-tab setup. Quick version:

```bash
cd server && NIGHT_SECONDS=60 npm run dev     # short 60s nights; leave running
cd client && npm run dev                       # open the Vite URL it prints (http://localhost:5173)
```

Open separate tabs (use separate browser profiles / incognito so each has its own controls). Force roles
and personas with URL params — dev mode honours them automatically:

| Tab | URL | Plays as |
|-----|-----|----------|
| 1 (host) | `/?devRole=searcher&devSpecialty=endurance` | **Sam** (host) |
| 2 | `/?devRole=bigfoot` | Bigfoot |
| 3 | `/?devRole=searcher&devSpecialty=tracking` | **Wren** |
| 4 | `/?devRole=searcher&devSpecialty=sound` | **Theo** |

In each tab: type a name → "Multiplayer lobby". In tab 1 press **Start**.

---

## Test checklist

Tick these off as you go. "Expected" is what should happen if it's working.

### Personas & debug switch
- [ ] After Start, each searcher shows a **character name** in the HUD (top-left pill). Bigfoot shows none.
- [ ] Without `?devSpecialty`, personas are **random but distinct** each match.
- [ ] `?devSpecialty=photo` spawns you as **Eli**; pressing **`\`** cycles your persona live (HUD updates).
  *(Only bound when `?devSpecialty` is in the URL.)*

### 🩹 Sam (Endurance) — tab 1
- [ ] Stamina bar lasts noticeably longer — you can **sprint further** before it empties (max is 150, shown
  as a full bar that drains slower).
- [ ] Reviving a downed teammate is **faster** (~2.4s vs the normal ~4s). *(Needs Bigfoot to roar+grab a
  teammate first; hold **E** by the downed player.)*

### 🥾 Wren (Tracking) — tab 3
- [ ] The **clue trail on the map (M)** shows from farther away and stays visible longer than for others.
- [ ] Press **`G`** to drop an **amber diamond marker** on the map — every teammate sees it (check another
  searcher's map). It fades after a while; there's a short cooldown between drops.
- [ ] Other players hear **Wren's footsteps at about half volume** (compare to another searcher walking).

### 🎙️ Theo (Sound) — tab 4
- [ ] The map trail goes "in contact" (shows) when Bigfoot is **farther away** than it would for others.
- [ ] When Bigfoot **roars**, a red **▲ ROAR** arrow appears and **points back toward where the roar came
  from**, staying up ~10s and rotating as you turn.
- [ ] Filming Bigfoot fills the clip **slightly faster** than normal.

### 🔬 Mara / 📷 Eli (not done yet)
- [ ] Both are **fully playable** as normal searchers (shared kit: film, revive, dazzle, vault) — they
  just have **no special ability yet**. (Eli's flash + film range and Mara's analysis are still to come.)

### Integrity spot-checks (Track A)
- [ ] You can only bank film while **actually looking at Bigfoot with line-of-sight** — try filming with a
  tree between you (should not progress) or facing away (should not progress).
- [ ] Flashlight **turns off at 0 battery** and won't turn back on.
- [ ] Nights run **8pm → 8am**, three of them, with a fade between.

---

## Notes / things to watch
- Sam's stamina bar is normalised to his own max, so a "full" Sam bar is really 150 — it just *drains
  slower*. That's intended.
- The `\` hot-swap and `?devSpecialty` are **debug-only** (gated by `ALLOW_DEV_ROLE`, off in production).
- If something feels too strong/weak, the numbers are all in one place: `shared/sim/specialties.ts`
  (the "Standard" tier). Jot down what felt off and we'll retune.
