// Tiny procedural meshes for the low-poly forest (no asset files). Smooth-shaded per the
// project aesthetic: shared vertices + RecalculateNormals, never flat/voxel.
using UnityEngine;

namespace Metoh.Game
{
    public static class MeshUtil
    {
        /// <summary>Tapered cylinder along +Y, base at y=0. Used for trunks, logs, posts.</summary>
        public static Mesh TaperedCylinder(float bottomRadius, float topRadius, float height, int segments)
        {
            var mesh = new Mesh();
            int ring = segments;
            var verts = new Vector3[ring * 2 + 2];
            // Cylindrical UVs. Every mesh here carries UVs and tangents now, because without them a
            // normal map cannot bind at all — and flat-shaded URP/Lit with no normal detail is the
            // single biggest reason the world reads as polygons instead of as material. V is scaled by
            // height so a tall trunk doesn't stretch its grain.
            var uvs = new Vector2[ring * 2 + 2];
            for (int i = 0; i < ring; i++)
            {
                float a = i / (float)ring * Mathf.PI * 2f;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                verts[i] = new Vector3(c * bottomRadius, 0f, s * bottomRadius);
                verts[ring + i] = new Vector3(c * topRadius, height, s * topRadius);
                float u = i / (float)ring;
                uvs[i] = new Vector2(u, 0f);
                uvs[ring + i] = new Vector2(u, height);
            }
            verts[ring * 2] = new Vector3(0f, 0f, 0f);          // bottom centre
            verts[ring * 2 + 1] = new Vector3(0f, height, 0f);  // top centre
            uvs[ring * 2] = new Vector2(0.5f, 0f);
            uvs[ring * 2 + 1] = new Vector2(0.5f, height);

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
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents(); // must follow UVs — URP/Lit needs tangents to apply a normal map
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Cone along +Y, base at y=0, apex at height. Canopy layers, ember piles.</summary>
        public static Mesh Cone(float radius, float height, int segments)
        {
            return TaperedCylinder(radius, 0.02f, height, segments);
        }

        /// <summary>
        /// A conifer crown as ONE lathed mesh with a tiered, jagged, drooping profile.
        ///
        /// WHY THIS REPLACED THREE STACKED CONES. The realism pass argued that the low-poly geometry
        /// was not the problem and materials were, and that was right about surfaces and wrong about
        /// SILHOUETTE. Three smooth cones stacked on a stick is a shape no tree has ever had, and at
        /// night — fogged, backlit, seen at 60 m — the silhouette is very nearly the only information
        /// reaching the player. A material cannot fix an outline. That specific shape, a stack of
        /// perfect cones, is also about as legible a period marker as exists in 3D: it is what trees
        /// looked like when a cone was all the triangles you could afford.
        ///
        /// Three things make this read as a fir, and none of them cost much:
        /// - **Tiers.** Real conifers grow in whorls, so the outline steps outward at each tier
        ///   instead of running smoothly to the tip. This is the single biggest cue.
        /// - **Jag.** Every vertex radius is nudged by a deterministic hash, so no two boughs end at
        ///   the same distance and the outline breaks up. A perfectly circular tree reads as a
        ///   revolved shape, which is exactly what it is.
        /// - **Droop.** Bough tips hang, proportional to how far they reach.
        ///
        /// <paramref name="variant"/> picks a deterministic shape from the hash — build a handful and
        /// deal them out so a stand of trees isn't one tree stamped 2,400 times. It must NOT come from
        /// an RNG stream: the forest's stream is in lockstep with the collider builder's
        /// (UNITY_PORT_NOTES §3c) and drawing one extra number here would offset every tree after it.
        /// </summary>
        public static Mesh Conifer(float height, float baseRadius, int rings, int segments, int tiers, int variant)
        {
            rings = Mathf.Max(rings, 3);
            segments = Mathf.Max(segments, 4);

            var verts = new Vector3[rings * segments + 1];
            var uvs = new Vector2[rings * segments + 1];

            for (int r = 0; r < rings; r++)
            {
                // t runs base(0) -> tip(1). The last ring stops short of the apex, which is its own
                // single vertex, so the tip comes to an actual point rather than a tiny flat disc.
                float t = r / (float)rings;

                // Envelope: opens quickly off the trunk, then tapers. Starting at zero closes the
                // bottom of the crown onto the trunk, so there is no hole to cap and nothing to see
                // up into from below.
                float open = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.14f));
                float env = open * Mathf.Pow(1f - t, 0.85f);

                // Whorls: within each tier the crown is widest at the bottom and narrows upward, so
                // the profile steps rather than running as one straight taper.
                float w = Mathf.Repeat(t * tiers, 1f);
                float tier = Mathf.Lerp(1f, 0.66f, w);

                float ringR = baseRadius * env * tier;

                for (int s = 0; s < segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;

                    // Per-vertex jag. Hashed from (variant, ring, segment) so it is stable for a given
                    // variant and completely independent of any RNG stream.
                    float jag = Hash01(variant * 9176 + r * 131 + s * 17) * 0.34f + 0.80f;
                    float rr = ringR * jag;

                    // Bough droop, strongest where the branch reaches furthest.
                    float reach = baseRadius > 1e-4f ? rr / baseRadius : 0f;
                    float droop = -reach * reach * height * 0.055f;

                    int i = r * segments + s;
                    verts[i] = new Vector3(Mathf.Cos(a) * rr, t * height + droop, Mathf.Sin(a) * rr);
                    // V in world-ish metres so needle grain doesn't stretch on a tall tree, matching
                    // TaperedCylinder's convention.
                    uvs[i] = new Vector2(s / (float)segments, t * height);
                }
            }

            int tip = rings * segments;
            verts[tip] = new Vector3(0f, height, 0f);
            uvs[tip] = new Vector2(0.5f, height);

            var tris = new System.Collections.Generic.List<int>(rings * segments * 6);
            for (int r = 0; r < rings - 1; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int s2 = (s + 1) % segments;
                    int a = r * segments + s, b = r * segments + s2;
                    int c = (r + 1) * segments + s, d = (r + 1) * segments + s2;
                    // Wound so the faces point outward — get this backwards and the tree is
                    // backface-culled into an invisible hole in the forest.
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                }
            }
            for (int s = 0; s < segments; s++) // apex fan
            {
                int s2 = (s + 1) % segments;
                tris.Add((rings - 1) * segments + s); tris.Add(tip); tris.Add((rings - 1) * segments + s2);
            }

            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// An irregular boulder — a lathed sphere pushed around by hashed noise.
        ///
        /// Every rock in the game was <c>PrimitiveType.Sphere</c> scaled flat. A sphere is the one
        /// shape the eye identifies instantly and never mistakes for stone, so the cave mouths (which
        /// the whole fast-travel network depends on being recognisable) read as a heap of grey
        /// beachballs. Deforming the radius costs nothing at these counts and there are only a few
        /// dozen of them in the world.
        /// </summary>
        public static Mesh Rock(float radius, int rings, int segments, int variant)
        {
            rings = Mathf.Max(rings, 3);
            segments = Mathf.Max(segments, 4);

            var verts = new Vector3[(rings + 1) * (segments + 1)];
            var uvs = new Vector2[(rings + 1) * (segments + 1)];

            for (int r = 0; r <= rings; r++)
            {
                float phi = r / (float)rings * Mathf.PI;      // 0..pi, pole to pole
                float sp = Mathf.Sin(phi), cp = Mathf.Cos(phi);
                for (int s = 0; s <= segments; s++)
                {
                    float theta = s / (float)segments * Mathf.PI * 2f;

                    // Two scales of lumps: broad facets, then a finer grit. Hashed on the WRAPPED
                    // segment index so the seam at theta=2pi matches the one at 0 — otherwise every
                    // rock has a visible crack down one side.
                    int sw = s % segments;
                    float broad = Hash01(variant * 7717 + (r / 2) * 97 + (sw / 2) * 13);
                    float fine = Hash01(variant * 3391 + r * 53 + sw * 7);
                    float rr = radius * (0.78f + broad * 0.30f + fine * 0.12f);

                    int i = r * (segments + 1) + s;
                    verts[i] = new Vector3(sp * Mathf.Cos(theta) * rr, cp * rr, sp * Mathf.Sin(theta) * rr);
                    uvs[i] = new Vector2(s / (float)segments * radius * 2f, r / (float)rings * radius * 2f);
                }
            }

            var tris = new System.Collections.Generic.List<int>(rings * segments * 6);
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * (segments + 1) + s, b = a + 1;
                    int c = (r + 1) * (segments + 1) + s, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Deterministic 0..1 hash. Not an RNG — it takes no state and advances nothing, which is
        /// precisely why it is safe to call from inside the forest loop (UNITY_PORT_NOTES §3c).
        /// </summary>
        private static float Hash01(int n)
        {
            uint h = (uint)n * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xffffff) / (float)0xffffff;
        }

        /// <summary>Flat ellipse disc in the XZ plane (fan). Used for the lake surface.</summary>
        public static Mesh EllipseDisc(float rx, float rz, int segments)
        {
            var mesh = new Mesh();
            var verts = new Vector3[segments + 1];
            var uvs = new Vector2[segments + 1];
            verts[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                verts[i + 1] = new Vector3(Mathf.Cos(a) * rx, 0f, Mathf.Sin(a) * rz);
                uvs[i + 1] = new Vector2(Mathf.Cos(a) * 0.5f + 0.5f, Mathf.Sin(a) * 0.5f + 0.5f);
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
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Unit cube centred on the origin. The one deliberately FLAT-shaded mesh here: it exists for
        /// the prayer-flag squares, which are 12 cm across and would turn to mush with the smooth
        /// normals the rest of the style uses.
        ///
        /// Borrowed from Unity's built-in primitive rather than hand-rolled — the temporary
        /// GameObject is destroyed immediately, but <c>sharedMesh</c> points at a built-in asset that
        /// outlives it, so the cached mesh stays valid. Cached because the undergrowth pass asks for
        /// it once per world rebuild.
        /// </summary>
        private static Mesh _unitCube;
        public static Mesh UnitCube()
        {
            if (_unitCube == null)
            {
                var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _unitCube = probe.GetComponent<MeshFilter>().sharedMesh;
                Object.Destroy(probe);
            }
            return _unitCube;
        }

        /// <summary>URP Lit material with a flat base colour. Kept for props that genuinely want no
        /// surface detail; anything the player gets close to should use <see cref="Surface"/>.</summary>
        public static Material Lit(Color color, float smoothness = 0.05f)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(shader);
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        /// <summary>
        /// A material with real surface response: normal detail, tuned smoothness, and optional
        /// second-layer detail for close-up.
        ///
        /// This is the difference between "a white triangle" and "snow". A flat-shaded face takes one
        /// shade of light across its whole area, which is why untextured geometry reads as plastic no
        /// matter how the colour is picked. A normal map gives every texel its own normal, so a single
        /// face scatters the moon and the flashlight into structure — and for snow specifically, that
        /// scattering IS the material: the glitter is a microfacet effect, not a colour.
        ///
        /// <paramref name="tiling"/> is in WORLD metres per repeat where the mesh's UVs are world-ish
        /// (terrain, trails), and in mesh-UV units elsewhere. Keep it small enough to show grain and
        /// large enough that the tile pattern doesn't read at a distance.
        /// </summary>
        public static Material Surface(
            Color color,
            float smoothness,
            Texture2D normal = null,
            float normalScale = 1f,
            float tiling = 1f,
            Texture2D detailNormal = null,
            float detailTiling = 8f,
            float metallic = 0f,
            Color? emission = null,
            float emissionIntensity = 1f)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            // URP/Lit drives EVERY main-texture UV from _BaseMap_ST — base, normal and metallic all
            // share it. Setting a scale on _BumpMap looks like it should work and does nothing at
            // all, which is a quietly expensive way to spend an afternoon.
            m.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));

            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.SetFloat("_BumpScale", normalScale);
                // URP compiles the normal path only when the keyword is set. Binding the texture
                // without this is a silent no-op — the map is attached and never sampled.
                m.EnableKeyword("_NORMALMAP");
            }

            if (detailNormal != null)
            {
                m.SetTexture("_DetailNormalMap", detailNormal);
                m.SetFloat("_DetailNormalMapScale", 1f);
                // ...and the DETAIL UV comes from _DetailAlbedoMap_ST, for both detail maps — so the
                // tiling has to be set there even though we never assign a detail albedo. (Leaving
                // that texture unassigned is safe: URP defaults it to linearGrey and the "x2" in
                // _DETAIL_MULX2 takes 0.5 back to 1.0, so the base colour passes through untouched.)
                m.SetTextureScale("_DetailAlbedoMap", new Vector2(detailTiling, detailTiling));
                m.EnableKeyword("_DETAIL_MULX2");
            }

            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                m.SetColor("_EmissionColor", emission.Value * emissionIntensity);
            }
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
