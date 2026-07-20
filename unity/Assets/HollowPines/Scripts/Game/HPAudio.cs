// All game audio, synthesized at startup — the Unity port of client/src/core/AudioEngine.ts.
// There are no asset files: every cue is a short DSP fill written into an AudioClip, and the
// ambient bed is looping generated noise. The synthesis recipes below are ported sample-for-sample
// from the web build's buildCues(), so Hollow Pines sounds the same on both engines.
//
// Positional cues (roar, branch snaps, remote footsteps) play through pooled 3D AudioSources so
// they pan and attenuate from their world position; UI / own-action cues are 2D. The master
// volume is NOT handled here — HPSettings drives AudioListener.volume, so this engine inherits
// the settings slider for free.
//
// This is engine-side only: it renders sounds from events and SyncVar changes it observes, and
// never decides gameplay.
using System.Collections.Generic;
using HollowPines.Sim;
using UnityEngine;

namespace HollowPines.Game
{
    public class HPAudio : MonoBehaviour
    {
        public static HPAudio Instance { get; private set; }

        public const string Roar = "roar";
        public const string FootstepSoft = "footstep_soft";
        public const string FootstepHeavy = "footstep_heavy";
        public const string BranchSnap = "branch_snap";
        public const string FlashlightClick = "flashlight_click";
        public const string PingDrop = "ping_drop";
        public const string VideoCaptured = "video_captured";
        public const string FreezeSting = "freeze_sting";
        public const string GrabImpact = "grab_impact";
        public const string CaveWhoosh = "cave_whoosh";
        public const string NightSting = "night_sting";
        public const string Victory = "victory";
        public const string Defeat = "defeat";
        public const string ReviveChannel = "revive_channel";
        public const string ReviveSuccess = "revive_success";
        public const string Heartbeat = "heartbeat";
        // Evidence + the two new persona abilities (no web-build counterpart — synthesized to match).
        public const string EvidenceBanked = "evidence_banked";
        public const string EvidenceDestroyed = "evidence_destroyed";
        public const string CameraFlash = "camera_flash";
        public const string BatterySwap = "battery_swap";

        private const int SampleRate = 44100;
        private const float MasterTrim = 0.85f; // headroom — many cues can overlap
        private const int PoolSize = 20;

        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private AudioSource[] _pool;
        private int _next;
        private AudioSource _wind, _creek;
        private float _hbIntensity, _hbTimer;
        private System.Random _rng = new System.Random(12345);

        /// <summary>Create the engine on the systems object if it isn't there yet (WorldBuilder calls this).</summary>
        public static HPAudio Ensure(GameObject host)
        {
            if (Instance != null) return Instance;
            return host.GetComponent<HPAudio>() ?? host.AddComponent<HPAudio>();
        }

        private void Awake()
        {
            Instance = this;
            BuildCues();
            BuildPool();
            StartAmbience();
        }

        // ------------------------------------------------------------------ playback

        /// <summary>Non-positional one-shot (UI / your own actions).</summary>
        public void PlayOnce(string cue, float volume = 1f)
        {
            var src = Take(cue);
            if (src == null) return;
            src.spatialBlend = 0f;
            src.volume = volume * MasterTrim;
            src.Play();
        }

        /// <summary>Positional one-shot at a world point — pans and falls off from there.</summary>
        public void PlayAt(string cue, Vector3 pos, float volume = 1f, float minDistance = 12f)
        {
            var src = Take(cue);
            if (src == null) return;
            // Sit the sound just above the ground so it isn't buried under the terrain.
            var world = WorldBuilder.World;
            if (world != null) pos.y = (float)world.GetHeight(pos.x, pos.z) + 1.2f;
            src.transform.position = pos;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.minDistance = minDistance;
            src.maxDistance = Mathf.Max(minDistance * 12f, 140f);
            src.volume = volume * MasterTrim;
            src.Play();
        }

        /// <summary>The local player's own footstep (heavy = Bigfoot; Wren treads quietly).</summary>
        public void PlayFootstep(bool sprinting, bool heavy, float volumeMul = 1f)
        {
            PlayOnce(heavy ? FootstepHeavy : FootstepSoft,
                (heavy ? 0.3f : sprinting ? 0.22f : 0.15f) * volumeMul);
        }

        /// <summary>Hunters' dread bed: 0 = Bigfoot far away, 1 = right on top of you.</summary>
        public void SetHeartbeat(float intensity)
        {
            _hbIntensity = Mathf.Clamp01(intensity);
        }

        private AudioSource Take(string cue)
        {
            if (!_clips.TryGetValue(cue, out AudioClip clip) || _pool == null) return null;
            // Round-robin; prefer a free source but never block a cue on a full pool.
            for (int i = 0; i < PoolSize; i++)
            {
                var s = _pool[(_next + i) % PoolSize];
                if (s.isPlaying) continue;
                _next = (_next + i + 1) % PoolSize;
                s.clip = clip;
                return s;
            }
            var fallback = _pool[_next];
            _next = (_next + 1) % PoolSize;
            fallback.Stop();
            fallback.clip = clip;
            return fallback;
        }

        private void BuildPool()
        {
            _pool = new AudioSource[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("Sfx" + i);
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f;
                s.dopplerLevel = 0f;
                _pool[i] = s;
            }
        }

        // ------------------------------------------------------------------ ambience + heartbeat

        private void StartAmbience()
        {
            // Wind: brown noise (already smooth/lowpassed by construction), always audible, gusting.
            var windGo = new GameObject("WindBed");
            windGo.transform.SetParent(transform, false);
            _wind = windGo.AddComponent<AudioSource>();
            _wind.clip = NoiseClip("wind", 3f, brown: true);
            _wind.loop = true;
            _wind.spatialBlend = 0f;
            _wind.volume = 0.12f;
            _wind.Play();

            // Creek: babbling water at the near edge of the lake — positional, so it guides you there.
            var creekGo = new GameObject("CreekBed");
            creekGo.transform.SetParent(transform, false);
            var world = WorldBuilder.EnsureWorld();
            double lx = WorldData.Lake.X, lz = WorldData.Lake.Z;
            double dl = System.Math.Sqrt(lx * lx + lz * lz);
            if (dl < 1e-3) dl = 1;
            // Step from the lake centre toward the map centre by its radius — the shore facing camp.
            var edge = new Vector3(
                (float)(lx - lx / dl * WorldData.Lake.Rx),
                0f,
                (float)(lz - lz / dl * WorldData.Lake.Rz));
            edge.y = (float)world.GetHeight(edge.x, edge.z) + 0.5f;
            creekGo.transform.position = edge;
            _creek = creekGo.AddComponent<AudioSource>();
            _creek.clip = NoiseClip("creek", 3f, brown: false, bandpass: true);
            _creek.loop = true;
            _creek.spatialBlend = 1f;
            _creek.rolloffMode = AudioRolloffMode.Linear;
            _creek.minDistance = 6f;
            _creek.maxDistance = 40f; // the web build's ~30 m swell, with a little tail
            _creek.volume = 0.16f;
            _creek.dopplerLevel = 0f;
            _creek.Play();
        }

        private void Update()
        {
            // Gusting wind — a slow LFO on the bed's gain (the web build's 12 s gust cycle).
            if (_wind != null)
                _wind.volume = 0.10f + 0.05f * Mathf.Sin(Time.time * 0.08f * Mathf.PI * 2f);

            UpdateHeartbeat();
        }

        /// <summary>Retriggered heartbeat: beats quicken and swell as Bigfoot closes on a searcher.</summary>
        private void UpdateHeartbeat()
        {
            var me = HPPlayer.Local;
            var gm = GameManager.Instance;
            bool playing = gm != null && gm.MatchPhase.Value == GameManager.PhasePlaying;

            if (me == null || me.IsBigfoot || !playing)
            {
                _hbIntensity = 0f;
            }
            else
            {
                // 40 m silent -> 10 m pounding, scaled by Theo's sharper hearing.
                float mul = (float)Specialties.HearRangeMul(me.Specialty.Value);
                float intensity = 0f;
                foreach (var p in HPPlayer.All)
                {
                    if (p == null || !p.IsBigfoot) continue;
                    // A crouching Bigfoot can't be heard — no dread beat gives it away either.
                    if (p.Crouched.Value) continue;
                    float dx = p.transform.position.x - me.transform.position.x;
                    float dz = p.transform.position.z - me.transform.position.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    intensity = Mathf.Max(intensity, Mathf.Clamp01((40f * mul - d) / (30f * mul)));
                }
                _hbIntensity = intensity;
            }

            if (_hbIntensity > 0.02f)
            {
                _hbTimer -= Time.deltaTime;
                if (_hbTimer <= 0f)
                {
                    _hbTimer = Mathf.Lerp(1.3f, 0.42f, _hbIntensity);
                    PlayOnce(Heartbeat, 0.25f + 0.55f * _hbIntensity);
                }
            }
            else _hbTimer = 0f;
        }

        // ------------------------------------------------------------------ procedural synthesis

        /// <summary>Allocate a mono clip of `dur` seconds and let `fill` write the samples.</summary>
        private AudioClip Synth(string name, float dur, System.Action<float[], int> fill)
        {
            int len = Mathf.Max(1, Mathf.FloorToInt(dur * SampleRate));
            var data = new float[len];
            fill(data, SampleRate);
            var clip = AudioClip.Create(name, len, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private double Rand() => _rng.NextDouble() * 2 - 1; // -1..1, the TS `Math.random()*2-1`

        private void BuildCues()
        {
            // Bigfoot's signature: a descending growl — sawtooth + amplitude-modulated lowpassed noise.
            _clips[Roar] = Synth(Roar, 1.6f, (d, sr) =>
            {
                double lp = 0, ph = 0;
                float dur = (float)d.Length / sr;
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr, p = t / dur;
                    double f = 150 * System.Math.Pow(0.42, p); // 150 -> ~63 Hz
                    ph += f / sr;
                    double saw = (ph % 1) * 2 - 1;
                    lp += 0.06 * (Rand() - lp);
                    double growl = 0.6 + 0.4 * System.Math.Sin(2 * System.Math.PI * 22 * t);
                    double env = System.Math.Min(1, t / 0.12) * System.Math.Exp(-System.Math.Max(0, t - 0.12) * 1.6);
                    d[i] = (float)((saw * 0.55 + lp * 0.8 * growl) * env * 0.9);
                }
            });

            // Footsteps: short lowpassed-noise thuds; heavier = lower cutoff, longer, louder (Bigfoot).
            _clips[FootstepSoft] = Step(FootstepSoft, 0.16f, 0.05, 0.5);
            _clips[FootstepHeavy] = Step(FootstepHeavy, 0.24f, 0.03, 0.95);

            // Branch snap: a sharp crack with a small secondary crackle.
            _clips[BranchSnap] = Synth(BranchSnap, 0.14f, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    double env = System.Math.Exp(-t * 60) + 0.4 * System.Math.Exp(-System.Math.Max(0, t - 0.04) * 90);
                    d[i] = (float)(Rand() * env * 0.7);
                }
            });

            // Flashlight: a tiny click transient.
            _clips[FlashlightClick] = Synth(FlashlightClick, 0.05f, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                    d[i] = (float)(Rand() * System.Math.Exp(-((double)i / sr) * 240) * 0.5);
            });

            // Ping: a soft two-tone blip.
            _clips[PingDrop] = TwoTone(PingDrop, 0.3f, 880, 1320, 0.12, 16, 0.4);

            // Captured video: a bright rising triad.
            _clips[VideoCaptured] = Arp(VideoCaptured, new double[] { 660, 880, 1175 }, 0.12f, 9, 0.35);

            // Freeze: a dissonant detuned riser with tremolo (you were roared).
            _clips[FreezeSting] = Synth(FreezeSting, 0.9f, (d, sr) =>
            {
                float dur = (float)d.Length / sr;
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr, p = t / dur;
                    double f = 180 + 220 * p;
                    double s1 = (t * f % 1) * 2 - 1;
                    double s2 = (t * f * 1.06 % 1) * 2 - 1; // dissonant detune
                    double trem = 0.7 + 0.3 * System.Math.Sin(2 * System.Math.PI * 9 * t);
                    d[i] = (float)((s1 + s2) * 0.25 * trem * System.Math.Min(1, t / 0.1) * (1 - p * 0.2));
                }
            });

            // Grab: a low pitch-down thump that submerges into lowpassed noise (you were taken).
            _clips[GrabImpact] = Synth(GrabImpact, 0.7f, (d, sr) =>
            {
                double lp = 0;
                float dur = (float)d.Length / sr;
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr, p = t / dur;
                    double f = 90 * System.Math.Pow(0.5, p);
                    double thump = System.Math.Sin(2 * System.Math.PI * f * t) * System.Math.Exp(-t * 4);
                    lp += 0.03 * (Rand() - lp);
                    d[i] = (float)((thump * 0.85 + lp * System.Math.Exp(-t * 2) * 0.5) * 0.9);
                }
            });

            // Cave fast-travel: an airy noise swell.
            _clips[CaveWhoosh] = Synth(CaveWhoosh, 0.7f, (d, sr) =>
            {
                double lp = 0;
                float dur = (float)d.Length / sr;
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr, p = t / dur;
                    lp += 0.08 * (Rand() - lp);
                    d[i] = (float)(lp * System.Math.Sin(System.Math.PI * p) * 0.7);
                }
            });

            // New night: an ominous low swell (root + minor third + octave).
            _clips[NightSting] = Synth(NightSting, 1.2f, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    double env = System.Math.Min(1, t / 0.3) * System.Math.Exp(-System.Math.Max(0, t - 0.6) * 1.5);
                    double s = System.Math.Sin(2 * System.Math.PI * 70 * t)
                             + 0.7 * System.Math.Sin(2 * System.Math.PI * 84 * t)
                             + 0.4 * System.Math.Sin(2 * System.Math.PI * 140 * t);
                    d[i] = (float)(s * 0.2 * env);
                }
            });

            _clips[Victory] = Arp(Victory, new double[] { 523, 659, 784, 1047 }, 0.18f, 5, 0.3);
            _clips[Defeat] = Arp(Defeat, new double[] { 392, 349, 311, 233 }, 0.22f, 3.5, 0.3);

            // Revive channel: a soft pulsing tick while you hold a teammate's revive.
            _clips[ReviveChannel] = Synth(ReviveChannel, 0.12f, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    d[i] = (float)(System.Math.Sin(2 * System.Math.PI * 520 * t) *
                                   System.Math.Min(1, t / 0.01) * System.Math.Exp(-t * 24) * 0.3);
                }
            });
            // Revive success: a warm rising triad (your teammate is back up).
            _clips[ReviveSuccess] = Arp(ReviveSuccess, new double[] { 523, 698, 880 }, 0.13f, 7, 0.32);

            // --- Evidence + new abilities. Same synthesis vocabulary as the web cues above. ---

            // Cast taken: a solid confirming triad, warmer and lower than the video chime so the
            // two win paths sound distinct.
            _clips[EvidenceBanked] = Arp(EvidenceBanked, new double[] { 392, 523, 659 }, 0.14f, 7, 0.32);

            // Evidence crushed underfoot: a short gritty crunch — the sound of losing something.
            _clips[EvidenceDestroyed] = Synth(EvidenceDestroyed, 0.3f, (d, sr) =>
            {
                double lp = 0;
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    lp += 0.12 * (Rand() - lp);
                    double env = System.Math.Exp(-t * 11) * (0.7 + 0.3 * System.Math.Sin(2 * System.Math.PI * 34 * t));
                    d[i] = (float)(lp * env * 1.1);
                }
            });

            // Camera flash: a capacitor whine that snaps into a bright crack.
            _clips[CameraFlash] = Synth(CameraFlash, 0.5f, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    // Whine rising into the trigger at 0.16 s, then the discharge.
                    double whine = t < 0.16
                        ? System.Math.Sin(2 * System.Math.PI * (2200 + 5200 * (t / 0.16)) * t) * 0.10 * (t / 0.16)
                        : 0;
                    double u = t - 0.16;
                    double crack = u >= 0 ? Rand() * System.Math.Exp(-u * 42) * 0.75 : 0;
                    d[i] = (float)(whine + crack);
                }
            });

            // Battery hand-off: a small plastic clack plus a low confirming thunk.
            _clips[BatterySwap] = Synth(BatterySwap, 0.24f, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    double clack = Rand() * System.Math.Exp(-t * 90) * 0.4;
                    double thunk = System.Math.Sin(2 * System.Math.PI * 180 * t) * System.Math.Exp(-t * 16) * 0.35;
                    d[i] = (float)(clack + thunk);
                }
            });

            // Heartbeat: a low lub-dub.
            _clips[Heartbeat] = Synth(Heartbeat, 0.5f, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    double s = System.Math.Sin(2 * System.Math.PI * 55 * t) * System.Math.Exp(-t * 18);
                    double u = t - 0.14;
                    if (u > 0) s += 0.8 * System.Math.Sin(2 * System.Math.PI * 50 * u) * System.Math.Exp(-u * 20);
                    d[i] = (float)(s * 0.9);
                }
            });
        }

        private AudioClip Step(string name, float dur, double cut, double gain)
        {
            return Synth(name, dur, (d, sr) =>
            {
                double lp = 0;
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    lp += cut * (Rand() - lp);
                    d[i] = (float)(lp * System.Math.Exp(-(t / dur) * 6) * gain);
                }
            });
        }

        /// <summary>Sequential sine "arpeggio" of `freqs`, each segment `seg` seconds, decaying at `dk`.</summary>
        private AudioClip Arp(string name, double[] freqs, float seg, double dk, double gain)
        {
            return Synth(name, seg * freqs.Length, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    int k = Mathf.Min(freqs.Length - 1, Mathf.FloorToInt(t / seg));
                    double u = t - k * seg;
                    d[i] = (float)(System.Math.Sin(2 * System.Math.PI * freqs[k] * t) * System.Math.Exp(-u * dk) * gain);
                }
            });
        }

        /// <summary>Two sine blips back-to-back (f1, then f2 at `split` seconds), each decaying at `dk`.</summary>
        private AudioClip TwoTone(string name, float dur, double f1, double f2, double split, double dk, double gain)
        {
            return Synth(name, dur, (d, sr) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / sr;
                    double s = t < split
                        ? System.Math.Sin(2 * System.Math.PI * f1 * t) * System.Math.Exp(-t * dk)
                        : System.Math.Sin(2 * System.Math.PI * f2 * (t - split)) * System.Math.Exp(-(t - split) * dk);
                    d[i] = (float)(s * gain);
                }
            });
        }

        /// <summary>Looping noise: white, integrated to brown (wind), or crudely bandpassed (water).</summary>
        private AudioClip NoiseClip(string name, float seconds, bool brown, bool bandpass = false)
        {
            return Synth(name, seconds, (d, sr) =>
            {
                double last = 0, prev = 0;
                for (int i = 0; i < d.Length; i++)
                {
                    double w = Rand();
                    if (brown)
                    {
                        last = (last + 0.02 * w) / 1.02;
                        d[i] = (float)(last * 3.5);
                    }
                    else if (bandpass)
                    {
                        // One-pole lowpass minus its own slower follower ≈ a babbling band around ~1 kHz.
                        last += 0.25 * (w - last);
                        prev += 0.02 * (last - prev);
                        d[i] = (float)((last - prev) * 0.9);
                    }
                    else d[i] = (float)w;
                }

                // Crossfade the loop seam so the bed doesn't tick once per lap.
                int fade = Mathf.Min(2000, d.Length / 8);
                for (int i = 0; i < fade; i++)
                {
                    float k = (float)i / fade;
                    d[i] = Mathf.Lerp(d[d.Length - fade + i], d[i], k);
                }
            });
        }
    }
}
