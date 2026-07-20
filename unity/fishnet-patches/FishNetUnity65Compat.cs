// LOCAL PATCH (Hollow Pines) — FishNet 4.7.2 on Unity 6000.5+.
//
// Unity 6.5 changed Scene.handle from int to a SceneHandle struct and made the implicit
// int conversions compile errors (CS0619). FishNet 4.7.2 (Apr 2026, latest at patch time)
// predates this and stores/compares scene handles as int throughout.
//
// This shim restores an int view of the handle. FishNet only needs these ints to be unique
// per loaded scene within a session (dictionary keys / equality); truncating the raw 64-bit
// value preserves that in practice. Defined in the global namespace so the ~16 patched call
// sites (marked "Unity 6.5 patch" — see unity/fishnet-patches in the game repo) need no using.
//
// Delete this file when upgrading to a FishNet release with native Unity 6.5 support.
using UnityEngine.SceneManagement;

internal static class FishNetUnity65Compat
{
    /// <summary>Scene.handle as the session-unique int FishNet 4.7.2 expects (pre-6.5 behavior).</summary>
    public static int HandleInt(this Scene scene)
    {
        return unchecked((int)scene.handle.GetRawData());
    }
}
