// Procedural body geometry for the two figures in the game — the searchers and the Yeti.
//
// WHY THIS EXISTS. Both characters were a single `PrimitiveType.Capsule`, scaled. UNITY_PORT_NOTES
// §5c already made the argument for the trees and it applies here with far more force: "materials,
// not mesh density" is true of SURFACES and false of SILHOUETTES, and at night, fogged, at the range
// you actually see another player, the outline is very nearly all the information there is. A capsule
// has no shoulders, no head, no limbs, and no facing — it is the one shape that cannot read as a
// person no matter how well it is lit or how good its normal map is. It also cannot ANIMATE, and a
// figure sliding across snow with nothing moving is the loudest tell in the build.
//
// So this file builds bodies out of lathed profiles. Everything here follows the house rules:
// smooth vertex normals, generated from code with no asset files, and — the one that silently breaks
// everything if you forget it — UVs AND TANGENTS on every mesh, or URP/Lit cannot bind a normal map
// and the whole material pass renders flat (§5b).
//
// The one thing done here that MeshUtil doesn't do is SEAM WELDING. A lathe has to duplicate the
// vertex column where theta wraps, or the UV runs 1 -> 0 across the last quad; but two coincident
// vertices get two independently averaged normals from RecalculateNormals, which draws a hard
// lighting line straight down the mesh. On a boulder nobody notices. On a face, at torch range, it is
// a crack. So the seam pairs are averaged back together after the normals are computed.
using System.Collections.Generic;
using UnityEngine;

namespace Metoh.Game
{
    public static class CharacterMesh
    {
        /// <summary>
        /// One cross-section of a lathed body part, bottom-up.
        ///
        /// The cross-section is an ELLIPSE, not a circle, because almost nothing on a body is round:
        /// a torso is much wider than it is deep, a forearm is the other way about. And the ring
        /// centre can be offset fore/aft, which is what bends a spine into a hunch or pushes a muzzle
        /// out of a skull — a stack of concentric rings can only ever produce a bollard.
        /// </summary>
        public readonly struct Ring
        {
            public readonly float Y;    // height of this cross-section in the mesh's local space
            public readonly float RX;   // half-width (left-right)
            public readonly float RZ;   // half-depth (front-back)
            public readonly float OffZ; // fore/aft shift of the ring's centre — spine curve, muzzle, brow
            public readonly float OffX; // lateral shift — only used for the odd asymmetric prop

            public Ring(float y, float rx, float rz, float offZ = 0f, float offX = 0f)
            {
                Y = y; RX = rx; RZ = rz; OffZ = offZ; OffX = offX;
            }
        }

        /// <summary>
        /// Revolve a stack of rings into a closed mesh.
        ///
        /// <paramref name="jag"/> perturbs each vertex's radius by a hash of (variant, ring, segment).
        /// It is 0 for anything meant to look manufactured or anatomical, and turned up for fur, where
        /// a perfectly smooth outline is the giveaway. As in <see cref="MeshUtil.Conifer"/>, the noise
        /// is a HASH and not an RNG draw, so calling this never disturbs a seeded stream.
        ///
        /// Ends are capped with a fan unless the end ring is degenerate, in which case the ring's own
        /// coincident vertices already close the shape to a point.
        /// </summary>
        public static Mesh Lathe(IList<Ring> rings, int segments, int variant = 0, float jag = 0f)
        {
            int rc = rings.Count;
            if (rc < 2) return new Mesh();
            segments = Mathf.Max(segments, 3);

            // segments + 1 columns: the last duplicates the first so U can run cleanly to 1.
            int cols = segments + 1;
            var verts = new Vector3[rc * cols + 2]; // + two possible cap centres
            var uvs = new Vector2[verts.Length];

            for (int r = 0; r < rc; r++)
            {
                Ring ring = rings[r];
                for (int s = 0; s < cols; s++)
                {
                    // Wrap the hash's segment index so the seam column perturbs identically to the
                    // column it duplicates — otherwise the jag itself splits the mesh open.
                    int sw = s % segments;
                    float k = jag > 0f ? 1f + (MeshUtil.Hash01(variant * 6151 + r * 211 + sw * 29) - 0.5f) * 2f * jag : 1f;

                    // (cos, sin) into (x, z), exactly as MeshUtil.TaperedCylinder and Conifer lay
                    // their rings out. This is not a style choice: the triangle winding below is
                    // copied from those, and swapping the pair to (sin, cos) reverses the direction
                    // of travel around the ring, which flips every face inward and backface-culls the
                    // whole figure into an invisible hole. The quarter-turn phase offset only ROTATES
                    // the ring — it puts the duplicated seam column on the figure's back (-Z), where
                    // a residual UV artefact is never being looked at — and leaves handedness alone.
                    float a = s / (float)segments * Mathf.PI * 2f - Mathf.PI * 0.5f;
                    verts[r * cols + s] = new Vector3(
                        Mathf.Cos(a) * ring.RX * k + ring.OffX,
                        ring.Y,
                        Mathf.Sin(a) * ring.RZ * k + ring.OffZ);
                    // V in local metres, matching MeshUtil's convention, so a long limb doesn't smear
                    // its grain; U is 0..1 around and the material's tiling decides the repeat.
                    uvs[r * cols + s] = new Vector2(s / (float)segments, ring.Y);
                }
            }

            var tris = new List<int>(rc * segments * 6);
            for (int r = 0; r < rc - 1; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * cols + s, b = a + 1;
                    int c = (r + 1) * cols + s, d = c + 1;
                    // Same winding as MeshUtil.Conifer — outward-facing. Reverse it and the character
                    // is backface-culled into an invisible hole.
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                }
            }

            int botCentre = rc * cols, topCentre = botCentre + 1;
            verts[botCentre] = new Vector3(rings[0].OffX, rings[0].Y, rings[0].OffZ);
            uvs[botCentre] = new Vector2(0.5f, rings[0].Y);
            verts[topCentre] = new Vector3(rings[rc - 1].OffX, rings[rc - 1].Y, rings[rc - 1].OffZ);
            uvs[topCentre] = new Vector2(0.5f, rings[rc - 1].Y);

            if (!Degenerate(rings[0]))
                for (int s = 0; s < segments; s++) { tris.Add(botCentre); tris.Add(s + 1); tris.Add(s); }
            if (!Degenerate(rings[rc - 1]))
                for (int s = 0; s < segments; s++)
                {
                    int baseI = (rc - 1) * cols;
                    tris.Add(topCentre); tris.Add(baseI + s); tris.Add(baseI + s + 1);
                }

            var mesh = new Mesh { name = "Lathe" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            WeldSeamNormals(mesh, rc, cols, segments);
            mesh.RecalculateTangents(); // after the normals are final — tangents are derived from them
            mesh.RecalculateBounds();
            return mesh;
        }

        private static bool Degenerate(Ring r) => Mathf.Abs(r.RX) < 1e-4f && Mathf.Abs(r.RZ) < 1e-4f;

        /// <summary>
        /// Average the normals of the duplicated seam column back together, so the wrap is invisible.
        /// Cheap and exact: the pairs are known by index, there is nothing to search for.
        /// </summary>
        private static void WeldSeamNormals(Mesh mesh, int rc, int cols, int segments)
        {
            var normals = mesh.normals;
            for (int r = 0; r < rc; r++)
            {
                int first = r * cols;
                int last = first + segments;
                Vector3 n = (normals[first] + normals[last]).normalized;
                normals[first] = n;
                normals[last] = n;
            }
            mesh.normals = normals;
        }

        // ------------------------------------------------------------------ part builders
        //
        // Convenience shapes over Lathe. Everything is built with its ORIGIN AT THE PIVOT the rig will
        // rotate it around, not at its centre of mass — a thigh hangs DOWN from the hip, so it is
        // built spanning y = 0 to y = -length. Getting this wrong is the difference between a leg that
        // swings and a leg that orbits.

        /// <summary>
        /// A limb segment hanging downward from a pivot at the origin: rounded at both ends, thickest
        /// a third of the way down (where a bicep or a calf actually is), tapering to the joint below.
        /// <paramref name="flatten"/> squashes the depth axis — limbs are ovals in section, not tubes.
        /// </summary>
        public static Mesh LimbDown(float length, float rTop, float rBelly, float rBottom,
                                    float flatten = 0.88f, int rings = 9, int segments = 10)
        {
            var list = new List<Ring>(rings);
            for (int i = 0; i < rings; i++)
            {
                float t = i / (float)(rings - 1);          // 0 at the pivot, 1 at the far end
                // Three-point radius profile through top -> belly -> bottom.
                float r = t < 0.34f
                    ? Mathf.Lerp(rTop, rBelly, Mathf.SmoothStep(0f, 1f, t / 0.34f))
                    : Mathf.Lerp(rBelly, rBottom, Mathf.SmoothStep(0f, 1f, (t - 0.34f) / 0.66f));
                // Round both ends off so a limb doesn't terminate in a flat disc where it meets the
                // next one. A joint that shows its cap is the classic parts-bin look.
                r *= EndRound(t, 0.10f);
                list.Add(new Ring(-t * length, r, r * flatten));
            }
            // Lathe's contract is BOTTOM-UP, and this profile was written pivot-first, which runs
            // downward. Handing it over in that order winds every side face inward and the limb
            // renders as nothing at all — the mesh is there, you simply cannot see it from outside.
            list.Reverse();
            return Lathe(list, segments);
        }

        /// <summary>Same profile, built UPWARD from the pivot — necks, and the Yeti's shoulder humps.</summary>
        public static Mesh LimbUp(float length, float rBottom, float rBelly, float rTop,
                                  float flatten = 0.88f, int rings = 9, int segments = 10)
        {
            var list = new List<Ring>(rings);
            for (int i = 0; i < rings; i++)
            {
                float t = i / (float)(rings - 1);
                float r = t < 0.34f
                    ? Mathf.Lerp(rBottom, rBelly, Mathf.SmoothStep(0f, 1f, t / 0.34f))
                    : Mathf.Lerp(rBelly, rTop, Mathf.SmoothStep(0f, 1f, (t - 0.34f) / 0.66f));
                r *= EndRound(t, 0.10f);
                list.Add(new Ring(t * length, r, r * flatten));
            }
            return Lathe(list, segments);
        }

        /// <summary>
        /// Shoulder-to-hip mass, built upward from a pivot at the waist. The four radii are the
        /// silhouette: hips, waist, chest and shoulder width, in metres.
        /// <paramref name="lean"/> pushes the upper rings forward, which is the whole difference
        /// between a person standing and a hominid hunching.
        /// </summary>
        public static Mesh Torso(float height, float rHip, float rWaist, float rChest, float rShoulder,
                                 float depthRatio, float lean, int rings = 11, int segments = 14)
        {
            var list = new List<Ring>(rings);
            for (int i = 0; i < rings; i++)
            {
                float t = i / (float)(rings - 1);
                float w;
                if (t < 0.22f) w = Mathf.Lerp(rHip, rWaist, Mathf.SmoothStep(0f, 1f, t / 0.22f));
                else if (t < 0.68f) w = Mathf.Lerp(rWaist, rChest, Mathf.SmoothStep(0f, 1f, (t - 0.22f) / 0.46f));
                else w = Mathf.Lerp(rChest, rShoulder, Mathf.SmoothStep(0f, 1f, (t - 0.68f) / 0.32f));
                // The very top closes over into the shoulder yoke rather than ending on a flat lid.
                w *= Mathf.Lerp(1f, 0.72f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.90f, 1f, t)));
                float bottomRound = Mathf.Lerp(0.80f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.12f, t)));
                w *= bottomRound;
                // The lean is a curve, not a shear: the hips stay put and the shoulders travel.
                float z = lean * t * t;
                list.Add(new Ring(t * height, w, w * depthRatio, z));
            }
            return Lathe(list, segments);
        }

        /// <summary>
        /// A head, built upward from the neck joint. Not a sphere: a skull is taller than it is wide,
        /// deeper than it is tall, and has a brow that overhangs the eyes. <paramref name="muzzle"/>
        /// pushes the lower-front of the mass forward into a snout, which is most of what separates
        /// the Yeti's head from a searcher's hood at silhouette range.
        /// </summary>
        public static Mesh Head(float height, float rWidth, float depthRatio, float muzzle, float brow,
                                int rings = 12, int segments = 14)
        {
            var list = new List<Ring>(rings);
            for (int i = 0; i < rings; i++)
            {
                float t = i / (float)(rings - 1);
                // An ovoid that is widest just above the middle (the cranium) and narrows to the jaw.
                float w = rWidth * Mathf.Sin(Mathf.Lerp(0.30f, 2.86f, t));
                // Brow ridge: a bump in depth around eye height, which is where a shadow line forms.
                float b = brow * Mathf.Exp(-Mathf.Pow((t - 0.46f) / 0.11f, 2f));
                float d = w * depthRatio + b;
                // Muzzle: the lower-front mass carried forward, fading out by the brow.
                float z = muzzle * Mathf.Clamp01(1f - t / 0.55f);
                list.Add(new Ring(t * height, w, d, z));
            }
            return Lathe(list, segments);
        }

        /// <summary>
        /// A ragged fringe of fur or fabric — a downward-facing skirt whose hem is chewed up by the
        /// hash, so no two strands end level.
        ///
        /// This is the single cheapest thing in the file and it does the most work, because it is the
        /// only part that attacks the OUTLINE. A body made of smooth lathed masses still reads as
        /// machined at 40 m; a broken edge around the shoulders and forearms reads as an animal. Same
        /// argument as the conifer's jag, for the same reason.
        /// </summary>
        public static Mesh Ruff(float rTop, float rBottom, float drop, int segments, int variant,
                                float jag = 0.30f, float flatten = 1f)
        {
            // A fringe is a thin skirt, so you see the INSIDE of it whenever the hem swings out or the
            // camera drops below it. That means two sides — and the back side needs its OWN copy of
            // the vertices, not just a second set of triangles over the same ones. Reversing the
            // winding on shared vertices makes RecalculateNormals average each vertex's front and
            // back contributions, which cancel to roughly zero and shade the whole ruff black. So the
            // ring is emitted twice: front half at [0, 2*cols), back half at [2*cols, 4*cols).
            int cols = segments + 1;
            int back = cols * 2;
            var verts = new Vector3[cols * 4];
            var uvs = new Vector2[cols * 4];
            for (int s = 0; s < cols; s++)
            {
                int sw = s % segments;
                float a = s / (float)segments * Mathf.PI * 2f - Mathf.PI * 0.5f;
                float cs = Mathf.Cos(a), sn = Mathf.Sin(a); // (cos -> x, sin -> z), as in Lathe
                // Two independent hashes: one widens the strand, one lengthens it. Perturbing only the
                // radius gives a scalloped but perfectly level hem, which still reads as a machined
                // edge — it is the varying LENGTH that reads as hanging fur.
                float wobble = 1f + (MeshUtil.Hash01(variant * 4813 + sw * 37) - 0.5f) * 2f * jag;
                float hang = drop * (0.45f + MeshUtil.Hash01(variant * 9241 + sw * 53) * 0.85f);

                var top = new Vector3(cs * rTop, 0f, sn * rTop * flatten);
                var hem = new Vector3(cs * rBottom * wobble, -hang, sn * rBottom * wobble * flatten);
                var uvTop = new Vector2(s / (float)segments, 0f);
                var uvHem = new Vector2(s / (float)segments, -hang);

                verts[s] = top;               uvs[s] = uvTop;
                verts[cols + s] = hem;        uvs[cols + s] = uvHem;
                verts[back + s] = top;        uvs[back + s] = uvTop;
                verts[back + cols + s] = hem; uvs[back + cols + s] = uvHem;
            }

            var tris = new List<int>(segments * 12);
            for (int s = 0; s < segments; s++)
            {
                // Lathe's winding, with the HEM as the lower ring and the collar as the upper one.
                int lo = cols + s, loN = cols + s + 1, up = s, upN = s + 1;
                tris.Add(lo); tris.Add(up); tris.Add(upN);
                tris.Add(lo); tris.Add(upN); tris.Add(loN);
                // ...and the same quad reversed, on the duplicate vertices.
                tris.Add(back + lo); tris.Add(back + upN); tris.Add(back + up);
                tris.Add(back + lo); tris.Add(back + loN); tris.Add(back + upN);
            }

            var mesh = new Mesh { name = "Ruff" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// A rounded slab — boots, mittens, the pack, the Yeti's feet. Built as a lathe with a
        /// rectangular-ish section so it is one code path with everything else (and therefore gets the
        /// UVs, tangents and welded seam for free), rather than a scaled built-in cube.
        /// </summary>
        public static Mesh Blob(Vector3 size, float taper = 0.85f, int rings = 7, int segments = 10)
        {
            var list = new List<Ring>(rings);
            for (int i = 0; i < rings; i++)
            {
                float t = i / (float)(rings - 1);
                float r = EndRound(t, 0.22f) * Mathf.Lerp(1f, taper, t);
                list.Add(new Ring((t - 0.5f) * size.y, r * size.x * 0.5f, r * size.z * 0.5f));
            }
            return Lathe(list, segments);
        }

        /// <summary>
        /// Shoulder-of-a-capsule falloff: 1 across the middle, easing to ~0 within
        /// <paramref name="cap"/> of either end. A quarter-circle, so the end reads as a dome rather
        /// than as a chamfer.
        /// </summary>
        private static float EndRound(float t, float cap)
        {
            float e = Mathf.Min(t, 1f - t);
            if (e >= cap) return 1f;
            float k = e / cap;
            return Mathf.Sqrt(Mathf.Max(0f, 1f - (1f - k) * (1f - k)));
        }
    }
}
