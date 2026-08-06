// DEV — the play-test log. A session transcript written to a file an agent can read WHILE the
// editor is still running.
//
// Why this exists: play-test feedback arrives as "the Yeti got stuck when it let go" — a sentence
// about a symptom, several seconds after the cause. Unity's own Editor.log is the wrong tool for
// answering it twice over: it is a build/import log with gameplay events scattered through it, and
// the running editor holds it open with a share mode that makes casual reading unreliable. So this
// writes its own file, containing only what the match did, opened with FileShare.ReadWrite so it
// can be tailed live from outside the editor.
//
// Two kinds of line land here:
//   EVENT  — explicit gameplay moments (grab, release, roar, revive, night rollover, AI state).
//   UNITY  — anything that reaches the Console, so an exception mid-match is in the same timeline
//            as the gameplay that caused it. That adjacency is the whole point.
//
// Written buffered and flushed once a second: a match generates a few hundred lines a minute, and
// per-line flushing on a spinning disk is a visible hitch.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Metoh.Game
{
    public static class HPLog
    {
        /// <summary>File name in the log directory. The previous session is kept alongside it.</summary>
        private const string FileName = "metoh-playtest.log";
        private const string PrevName = "metoh-playtest.prev.log";

        private static StreamWriter _writer;
        private static readonly object Gate = new object();
        private static float _nextFlush;
        private static bool _installed;
        private static bool _failed; // a broken log must never take the game down with it

        /// <summary>Where the file lives. Printed to the Console on start so it's findable.</summary>
        public static string Path { get; private set; } = "";

        /// <summary>
        /// Install before the first scene loads, so a crash during world build is still captured.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (_installed || _failed) return;
            _installed = true;

            try
            {
                string dir = LogDirectory();
                Directory.CreateDirectory(dir);
                Path = System.IO.Path.Combine(dir, FileName);

                // Keep exactly one previous session. Play-testing is a loop of "play, describe what
                // went wrong, fix, play again" — the run before the current one is often the one
                // being described, and anything older is noise.
                string prev = System.IO.Path.Combine(dir, PrevName);
                if (File.Exists(Path))
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(Path, prev);
                }

                // FileShare.ReadWrite is the entire reason this class exists rather than reusing
                // Editor.log — without it nothing else can open the file while Unity holds it.
                var fs = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };

                Application.logMessageReceivedThreaded += OnUnityLog;
                Application.quitting += Shutdown;
#if UNITY_EDITOR
                // Leaving Play mode is NOT a quit, so Application.quitting never fires in the editor —
                // which is exactly where every play-test happens. Without this the last second of the
                // session (often the interesting one) is still sitting in the buffer.
                UnityEditor.EditorApplication.playModeStateChanged += state =>
                {
                    if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) Shutdown();
                };
#endif

                Raw($"=== Metoh play-test log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                Raw($"unity {Application.unityVersion}   platform {Application.platform}   " +
                    $"editor {Application.isEditor}");
                Raw($"screen {Screen.width}x{Screen.height}   renderScale {HPSettings.RenderScale:0.00}");
                Raw("");
                Flush();

                Debug.Log($"[HPLog] play-test log → {Path}");
            }
            catch (Exception e)
            {
                _failed = true;
                _writer = null;
                Debug.LogWarning($"[HPLog] disabled — could not open the log file: {e.Message}");
            }
        }

        /// <summary>
        /// In the editor the log belongs next to the project's own Logs/ folder, where it sits beside
        /// Editor.log and is trivial to find. A built player has no project folder, so it goes to
        /// persistentDataPath, which is the only reliably writable location.
        /// </summary>
        private static string LogDirectory()
        {
            if (Application.isEditor)
            {
                // dataPath is "<project>/Assets" in the editor.
                string project = Directory.GetParent(Application.dataPath)?.FullName;
                if (!string.IsNullOrEmpty(project)) return System.IO.Path.Combine(project, "Logs");
            }
            return Application.persistentDataPath;
        }

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Record a gameplay moment. <paramref name="tag"/> is a short uppercase category (GRAB,
        /// ROAR, NIGHT, AI…) so the file can be grepped by concern.
        /// </summary>
        public static void Event(string tag, string message)
        {
            if (_writer == null) return;
            Raw($"[{Clock()}] {tag,-8} {message}");
        }

        /// <summary>
        /// Record a moment only when it differs from the last value logged under the same key — for
        /// things sampled every frame (an AI state, a status) where only the TRANSITIONS matter.
        /// Without this an AI state line would be 60 identical rows a second.
        /// </summary>
        public static void Change(string key, string tag, string value)
        {
            if (_writer == null) return;
            lock (Gate)
            {
                if (_last.TryGetValue(key, out string prev) && prev == value) return;
                _last[key] = value;
            }
            Event(tag, $"{key}: {value}");
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> _last =
            new System.Collections.Generic.Dictionary<string, string>();

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (_writer == null) return;

            // The look-axis readout is a half-second heartbeat from a bug that was already fixed; it
            // would be most of the file. Same for the bot movement probe. Both stay in the Console.
            if (condition.StartsWith("[look]") || condition.StartsWith("[bot]")) return;
            // Don't re-log our own startup line.
            if (condition.StartsWith("[HPLog]")) return;

            string kind = type == LogType.Log ? "log" : type.ToString().ToLowerInvariant();
            Raw($"[{Clock()}] UNITY    {kind}: {condition}");

            // A stack trace is noise for an ordinary Debug.Log and the single most useful thing there
            // is for a failure, so it's attached only to the failures.
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                foreach (string line in stackTrace.Split('\n'))
                {
                    string s = line.TrimEnd();
                    if (s.Length > 0) Raw("             | " + s);
                }
            }
        }

        private static void Raw(string line)
        {
            if (_writer == null) return;
            try
            {
                lock (Gate)
                {
                    _writer.WriteLine(line);
                    // Flushing on a timer rather than per line. Checked here (not in an Update) so the
                    // class needs no MonoBehaviour and works before any scene exists.
                    if (Time.realtimeSinceStartup >= _nextFlush)
                    {
                        _nextFlush = Time.realtimeSinceStartup + 1f;
                        _writer.Flush();
                    }
                }
            }
            catch
            {
                // A logging failure must never surface as a gameplay bug. Drop the line and stop.
                _failed = true;
                _writer = null;
            }
        }

        /// <summary>Force everything to disk — called at the end of a match, so the file is complete
        /// the moment the tester alt-tabs out to describe what happened.</summary>
        public static void Flush()
        {
            if (_writer == null) return;
            try { lock (Gate) _writer.Flush(); } catch { /* see Raw */ }
        }

        private static void Shutdown()
        {
            if (_writer == null) return;
            Event("SESSION", "shutting down");
            try
            {
                lock (Gate)
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch { /* nothing useful to do while quitting */ }
            _writer = null;
        }

        /// <summary>
        /// Seconds since the process started, which is what correlates with "about a minute in it
        /// did X". Match time is logged separately by the events that know it.
        /// </summary>
        private static string Clock()
        {
            float t = Time.realtimeSinceStartup;
            int m = (int)(t / 60f);
            float s = t - m * 60f;
            return m.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   s.ToString("00.0", CultureInfo.InvariantCulture);
        }
    }
}
