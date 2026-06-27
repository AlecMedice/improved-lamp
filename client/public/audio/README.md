# Audio overrides (optional)

Hollow Pines **synthesizes its sound at runtime** (see `client/src/core/AudioEngine.ts`), so the
game has full audio with no files here. This folder is purely for *upgrading* individual one-shot
cues with real recordings — drop a file in, no code change required.

## How it works

On startup the engine fetches `audio/manifest.json`. If it exists, it loads each listed cue from
`audio/<name>.ogg` and uses it instead of the procedural version. If the manifest is missing
(the default), the game stays 100% procedural and makes no extra network requests.

The continuous ambience (gusting **wind** + proximity **creek**) and the **heartbeat** are
generated as live audio graphs, not one-shot buffers — they're not override targets.

## To add an override

1. Add an `.ogg` (or browser-decodable) file named exactly after the cue (see list below).
2. List that cue name in `manifest.json`, e.g.:

   ```json
   ["roar", "footstep_heavy"]
   ```

## Recognized cue names

| Cue | When it plays |
|-----|---------------|
| `roar` | Bigfoot roars (heard positionally by everyone) |
| `footstep_soft` / `footstep_heavy` | Searcher / Bigfoot footsteps (own + remote, positional) |
| `branch_snap` | A branch breaks where Bigfoot stepped |
| `flashlight_click` | Searcher toggles the flashlight |
| `ping_drop` | Searcher drops a stakeout ping |
| `video_captured` | A solid video is banked |
| `freeze_sting` | You are frozen by a roar |
| `grab_impact` | You are grabbed / incapacitated (also Bigfoot's grab swing) |
| `cave_whoosh` | Bigfoot cave fast-travel |
| `night_sting` | A new night begins |
| `victory` / `defeat` | Match end |
| `heartbeat` | Searcher proximity-dread bed (rises as Bigfoot nears) |

> The procedural fallback is always there, so overrides are an enhancement, never a requirement.
