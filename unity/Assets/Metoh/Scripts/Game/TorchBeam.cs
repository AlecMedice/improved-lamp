// The geometry behind Shaders/TorchBeam.shader — a cone of triangles hung off a spot light.
using UnityEngine;

namespace Metoh.Game
{
    public class TorchBeam
    {
        private readonly GameObject _go;
        private readonly Mesh _mesh;
        private readonly Material _mat;

        /// <summary>
        /// How much of the light's actual range the visible shaft covers.
        ///
        /// Far less than all of it, and deliberately. The torch throws 90 m, but a shaft drawn to 90 m
        /// is a 54 m-wide cone of additive geometry filling most of the screen — it would wash the
        /// frame out and cost a fortune in overdraw, which is the one thing an integrated GPU is worst
        /// at. Air only scatters enough to see the beam close to the source anyway.
        /// </summary>
        private const float VisibleFraction = 0.26f;

        private TorchBeam(GameObject go, Mesh mesh, Material mat)
        {
            _go = go; _mesh = mesh; _mat = mat;
        }

        public static TorchBeam Build(Transform parent, float lightRange, float spotAngleDeg)
        {
            var shader = Shader.Find("Metoh/TorchBeam");
            // Shader.Find only catches a MISSING file — a shader that fails to COMPILE returns a valid
            // object that renders magenta. Bailing out on null at least keeps a missing file from
            // taking the torch with it; a compile failure is a Console error, not something detectable
            // from here (the same trap UNITY_PORT_NOTES flags for Metoh/Snowpack).
            if (shader == null) return null;

            float length = lightRange * VisibleFraction;
            float radius = length * Mathf.Tan(spotAngleDeg * 0.5f * Mathf.Deg2Rad);
            var mesh = BuildCone(length, radius, HPQuality.HighDetail ? 18 : 12);

            var mat = new Material(shader);
            mat.SetColor("_Color", MeshUtil.Rgb(0xffe9c4));
            mat.SetFloat("_Intensity", 0.30f);

            var go = new GameObject("Beam");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            // A translucent shaft of air neither casts nor receives shadows, and letting it try does
            // both of the wrong things: a shadow cast BY the beam, and the beam going dark inside its
            // own light's shadow.
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            // Lightprobes on an unlit additive mesh are pure cost.
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

            // Motes live under the beam, so they turn on and off with it and cost nothing while the
            // torch is stowed. They are also what makes the shaft read as AIR rather than as a
            // translucent solid — the cone alone has no internal structure to catch the eye.
            Weather.AttachMotes(go.transform, length, radius);

            go.SetActive(false);
            return new TorchBeam(go, mesh, mat);
        }

        public void SetOn(bool on)
        {
            if (_go != null && _go.activeSelf != on) _go.SetActive(on);
        }

        public void Dispose()
        {
            if (_mesh != null) Object.Destroy(_mesh);
            if (_mat != null) Object.Destroy(_mat);
            if (_go != null) Object.Destroy(_go);
        }

        /// <summary>
        /// Open cone along +Z, apex at the origin — the axis a Unity spot light shines down.
        ///
        /// No end cap. The far end is fully faded by the shader, so a cap would only ever be an
        /// invisible disc of overdraw; and if the fade were ever retuned brighter, a visible flat disc
        /// hanging in the air is a far worse artefact than a beam that simply stops.
        /// </summary>
        private static Mesh BuildCone(float length, float radius, int segments)
        {
            segments = Mathf.Max(segments, 6);
            int cols = segments + 1;

            var verts = new Vector3[cols * 2];
            var uvs = new Vector2[cols * 2];
            for (int s = 0; s < cols; s++)
            {
                // Wrapped so the seam column lands exactly on column 0 while carrying its own U.
                float a = (s % segments) / (float)segments * Mathf.PI * 2f;
                verts[s] = new Vector3(0f, 0f, 0f);                                        // apex ring
                verts[cols + s] = new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, length);
                // u = across the beam (0 on the axis, 1 at the wall), v = along it.
                uvs[s] = new Vector2(0f, 0f);
                uvs[cols + s] = new Vector2(1f, 1f);
            }

            var tris = new int[segments * 6];
            for (int s = 0; s < segments; s++)
            {
                int a = s, b = s + 1, c = cols + s, d = cols + s + 1;
                tris[s * 6 + 0] = a; tris[s * 6 + 1] = c; tris[s * 6 + 2] = d;
                tris[s * 6 + 3] = a; tris[s * 6 + 4] = d; tris[s * 6 + 5] = b;
            }

            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            // Bounds by hand: the apex ring is degenerate, so RecalculateBounds produces a box that is
            // correct but the renderer still culls against — fine here, but stated explicitly because a
            // beam popping out at the screen edge is the usual symptom of getting this wrong.
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
