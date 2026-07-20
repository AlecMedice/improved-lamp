// Tiny procedural meshes for the low-poly forest (no asset files). Smooth-shaded per the
// project aesthetic: shared vertices + RecalculateNormals, never flat/voxel.
using UnityEngine;

namespace HollowPines.Game
{
    public static class MeshUtil
    {
        /// <summary>Tapered cylinder along +Y, base at y=0. Used for trunks, logs, posts.</summary>
        public static Mesh TaperedCylinder(float bottomRadius, float topRadius, float height, int segments)
        {
            var mesh = new Mesh();
            int ring = segments;
            var verts = new Vector3[ring * 2 + 2];
            for (int i = 0; i < ring; i++)
            {
                float a = i / (float)ring * Mathf.PI * 2f;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                verts[i] = new Vector3(c * bottomRadius, 0f, s * bottomRadius);
                verts[ring + i] = new Vector3(c * topRadius, height, s * topRadius);
            }
            verts[ring * 2] = new Vector3(0f, 0f, 0f);          // bottom centre
            verts[ring * 2 + 1] = new Vector3(0f, height, 0f);  // top centre

            var tris = new System.Collections.Generic.List<int>(ring * 12);
            for (int i = 0; i < ring; i++)
            {
                int j = (i + 1) % ring;
                // side (wound so faces point outward)
                tris.AddRange(new[] { i, ring + i, ring + j });
                tris.AddRange(new[] { i, ring + j, j });
                // caps
                tris.AddRange(new[] { ring * 2, j, i });
                tris.AddRange(new[] { ring * 2 + 1, ring + i, ring + j });
            }
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Cone along +Y, base at y=0, apex at height. Canopy layers, ember piles.</summary>
        public static Mesh Cone(float radius, float height, int segments)
        {
            return TaperedCylinder(radius, 0.02f, height, segments);
        }

        /// <summary>Flat ellipse disc in the XZ plane (fan). Used for the lake surface.</summary>
        public static Mesh EllipseDisc(float rx, float rz, int segments)
        {
            var mesh = new Mesh();
            var verts = new Vector3[segments + 1];
            verts[0] = Vector3.zero;
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                verts[i + 1] = new Vector3(Mathf.Cos(a) * rx, 0f, Mathf.Sin(a) * rz);
            }
            var tris = new int[segments * 3];
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                tris[i * 3] = 0;
                tris[i * 3 + 1] = j + 1;
                tris[i * 3 + 2] = i + 1;
            }
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>URP Lit material with a flat base colour (the whole art style for now).</summary>
        public static Material Lit(Color color, float smoothness = 0.05f)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(shader);
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        /// <summary>Lit material with an emissive glow (eyeshine, embers, lake sheen).</summary>
        public static Material Emissive(Color baseColor, Color emission, float intensity)
        {
            var m = Lit(baseColor);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.SetColor("_EmissionColor", emission * intensity);
            return m;
        }

        public static Color Rgb(int hex)
        {
            return new Color(((hex >> 16) & 0xff) / 255f, ((hex >> 8) & 0xff) / 255f, (hex & 0xff) / 255f);
        }
    }
}
