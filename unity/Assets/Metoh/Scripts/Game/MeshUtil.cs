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
        /// (UNITY_PORT_NOTES [rng-lockstep]) and drawing one extra number here would offset every tree after it.
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
        /// A surface of revolution built from an explicit PROFILE — the general form of the shape
        /// <see cref="Conifer"/> and <see cref="Rock"/> are each a special case of, and the thing every
        /// body part in <see cref="Avatar"/> is made from.
        ///
        /// <paramref name="profile"/> runs bottom to top, one entry per ring, as (y, radius). A ring of
        /// radius 0 collapses to a pole, which is how limbs get rounded ends and heads close over the
        /// top; anything ending at a real radius gets a fan cap so there is never a hole to see into.
        ///
        /// <paramref name="xScale"/>/<paramref name="zScale"/> squash the revolution off-circular, and
        /// that is what makes this usable for bodies at all: a torso is far wider than it is deep, and a
        /// circular one reads as a barrel — which is precisely the "stack of primitives" problem
        /// (UNITY_PORT_NOTES [legibility]) reappearing on a creature instead of on a tree.
        ///
        /// THE SEAM IS SEALED EXPLICITLY. The wrap column duplicates column 0's position so the two can
        /// carry different U — without that the texture runs backwards across one strip. But
        /// RecalculateNormals averages by vertex INDEX, not by position, so those two co-located
        /// vertices each end up with only half the surrounding faces and light differently: a bright
        /// hairline seam straight down the body. Averaging the pair afterwards costs nothing and is why
        /// this is a shared builder rather than another copy of the loop in Rock.
        /// </summary>
        public static Mesh Lathe(Vector2[] profile, int segments, int variant,
                                 float jag = 0f, float xScale = 1f, float zScale = 1f)
        {
            segments = Mathf.Max(segments, 4);
            int rings = profile.Length;
            int cols = segments + 1;

            var verts = new Vector3[rings * cols];
            var uvs = new Vector2[rings * cols];

            for (int r = 0; r < rings; r++)
            {
                float y = profile[r].x, rad = profile[r].y;
                for (int s = 0; s < cols; s++)
                {
                    // Wrapped segment index: the seam column must land on column 0's exact angle, and
                    // must hash to the same jag, or the two halves of the seam pull apart.
                    int sw = s % segments;
                    float a = sw / (float)segments * Mathf.PI * 2f;
                    float k = jag > 0f
                        ? 1f + (Hash01(variant * 6151 + r * 179 + sw * 23) - 0.5f) * 2f * jag
                        : 1f;
                    float rr = rad * k;

                    int i = r * cols + s;
                    verts[i] = new Vector3(Mathf.Cos(a) * rr * xScale, y, Mathf.Sin(a) * rr * zScale);
                    // V in metres, matching TaperedCylinder — fur grain must not stretch on a long limb.
                    uvs[i] = new Vector2(s / (float)segments, y);
                }
            }

            var tris = new System.Collections.Generic.List<int>(rings * segments * 6);
            for (int r = 0; r < rings - 1; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * cols + s, b = a + 1;
                    int c = (r + 1) * cols + s, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                }
            }

            // Fan caps for ends that stop at a real radius. A pole (radius 0) already closes itself.
            AddCap(tris, profile[0].y, 0, cols, segments, false);
            AddCap(tris, profile[rings - 1].y, rings - 1, cols, segments, true);

            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();

            // Seal the seam (see the summary): give both copies of each seam vertex the same normal.
            var norms = mesh.normals;
            for (int r = 0; r < rings; r++)
            {
                int a = r * cols, b = r * cols + segments;
                Vector3 avg = (norms[a] + norms[b]).normalized;
                norms[a] = avg;
                norms[b] = avg;
            }
            mesh.normals = norms;

            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Fan-close one end of a lathe. No-op at a pole, where the ring has already collapsed to a
        /// point and a cap would be a disc of degenerate triangles.
        /// </summary>
        private static void AddCap(System.Collections.Generic.List<int> tris,
                                   float endRadius, int ring, int cols, int segments, bool top)
        {
            if (endRadius <= 0.001f) return;
            // Fan around the ring's own vertices rather than adding a centre vertex: it keeps the arrays
            // the caller allocated valid, and at these radii the missing centre never shows.
            int b = ring * cols;
            for (int s = 1; s < segments - 1; s++)
            {
                if (top) { tris.Add(b); tris.Add(b + s); tris.Add(b + s + 1); }
                else { tris.Add(b); tris.Add(b + s + 1); tris.Add(b + s); }
            }
        }

        /// <summary>
        /// A limb segment: a tapered cylinder with rounded ends, along +Y from y=0.
        ///
        /// Rounded ends are the whole point. Flat-capped cylinders butted together at a joint show the
        /// join as a hard disc edge that swings independently of the limb — which reads as a doll made
        /// of parts. Round caps overlap into each other through the full range of motion instead, so an
        /// elbow stays a continuous mass without any skinning.
        /// </summary>
        public static Mesh Limb(float radiusBottom, float radiusTop, float length,
                                int rings, int segments, int variant, float jag = 0.04f)
        {
            rings = Mathf.Max(rings, 5);
            var profile = new Vector2[rings];
            const float cap = 0.18f; // fraction of the length each rounded end occupies
            for (int r = 0; r < rings; r++)
            {
                float t = r / (float)(rings - 1);
                float rad = Mathf.Lerp(radiusBottom, radiusTop, t);
                // Quarter-sine shoulders at both ends: full radius through the middle, closing to a
                // point at the tips.
                if (t < cap) rad *= Mathf.Sin(t / cap * Mathf.PI * 0.5f);
                else if (t > 1f - cap) rad *= Mathf.Sin((1f - t) / cap * Mathf.PI * 0.5f);
                profile[r] = new Vector2(t * length, rad);
            }
            return Lathe(profile, segments, variant, jag);
        }

        /// <summary>
        /// A lumpy ellipsoid centred on the origin — torsos, skulls, hands, feet, shoulder mass.
        ///
        /// <paramref name="lumpiness"/> is what separates it from a scaled sphere, and a sphere is the
        /// one shape the eye names instantly ([legibility], on the boulders). On a body the tell is worse than on
        /// a rock, because anatomy is asymmetric everywhere and a perfect ellipsoid is the single
        /// loudest signal that what you are looking at is a primitive with a texture on it.
        /// </summary>
        public static Mesh Blob(float radiusX, float radiusY, float radiusZ,
                                int rings, int segments, int variant, float lumpiness = 0.12f)
        {
            rings = Mathf.Max(rings, 5);
            var profile = new Vector2[rings];
            for (int r = 0; r < rings; r++)
            {
                float phi = r / (float)(rings - 1) * Mathf.PI; // pole to pole
                profile[r] = new Vector2(-Mathf.Cos(phi) * radiusY, Mathf.Sin(phi) * radiusX);
            }
            return Lathe(profile, segments, variant, lumpiness, 1f, radiusZ / Mathf.Max(radiusX, 1e-4f));
        }

        /// <summary>
        /// An A-frame ridge tent: two sagging fabric slopes over a ridge line, closed at both ends.
        ///
        /// The camp tents were <c>Cone(radius, height, 4)</c> — a four-sided pyramid. A pyramid has a
        /// point where a tent has a RIDGE, and that difference is most of why the camp read as a set of
        /// placeholder shapes rather than as somewhere people are living: camp is the silhouette every
        /// searcher navigates home by and the one place the player stands still and looks around.
        ///
        /// The sag is the detail that does the work. Fabric under its own weight pulls inward between
        /// the poles, so the ridge dips and the slopes hollow; taut flat panels read as sheet metal.
        /// It is a sine in both axes — strongest mid-panel and mid-length, zero at every pole and
        /// every ground peg, which is exactly where a real tent is pulled tight.
        /// </summary>
        public static Mesh RidgeTent(float halfWidth, float height, float halfLength,
                                     int lengthSegs = 4, int slopeSegs = 6, float sag = 0.12f)
        {
            lengthSegs = Mathf.Max(lengthSegs, 2);
            slopeSegs = Mathf.Max(slopeSegs, 4);
            int cols = slopeSegs + 1, rows = lengthSegs + 1;

            var verts = new Vector3[rows * cols];
            var uvs = new Vector2[rows * cols];
            for (int r = 0; r < rows; r++)
            {
                float tz = r / (float)lengthSegs;              // 0..1 along the ridge
                float z = Mathf.Lerp(-halfLength, halfLength, tz);
                float lengthSag = Mathf.Sin(tz * Mathf.PI);    // zero at both end poles
                for (int s = 0; s < cols; s++)
                {
                    float u = s / (float)slopeSegs;            // 0 = left peg, 0.5 = ridge, 1 = right peg
                    float x, y;
                    if (u <= 0.5f) { float t = u * 2f; x = Mathf.Lerp(-halfWidth, 0f, t); y = Mathf.Lerp(0f, height, t); }
                    else { float t = (u - 0.5f) * 2f; x = Mathf.Lerp(0f, halfWidth, t); y = Mathf.Lerp(height, 0f, t); }

                    y -= sag * height * Mathf.Sin(u * Mathf.PI) * lengthSag;

                    int i = r * cols + s;
                    verts[i] = new Vector3(x, y, z);
                    uvs[i] = new Vector2(u * (halfWidth * 2f), z); // metres, so the weave doesn't stretch
                }
            }

            var tris = new System.Collections.Generic.List<int>(lengthSegs * slopeSegs * 6 + slopeSegs * 6);
            for (int r = 0; r < lengthSegs; r++)
            {
                for (int s = 0; s < slopeSegs; s++)
                {
                    int a = r * cols + s, b = a + 1, c = (r + 1) * cols + s, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            // End walls: fan each end profile down to a point on the ground under the ridge. Closed on
            // purpose — an open-ended tent shows its own inside surface backwards, and a lit camp is
            // exactly where somebody will walk round the back and look.
            var extra = new System.Collections.Generic.List<Vector3>();
            var extraUv = new System.Collections.Generic.List<Vector2>();
            for (int end = 0; end < 2; end++)
            {
                int baseRow = end == 0 ? 0 : lengthSegs;
                float z = end == 0 ? -halfLength : halfLength;
                int centre = verts.Length + extra.Count;
                extra.Add(new Vector3(0f, 0f, z));
                extraUv.Add(new Vector2(halfWidth, z));
                for (int s = 0; s < slopeSegs; s++)
                {
                    int a = baseRow * cols + s, b = a + 1;
                    if (end == 0) { tris.Add(centre); tris.Add(a); tris.Add(b); }
                    else { tris.Add(centre); tris.Add(b); tris.Add(a); }
                }
            }

            var allVerts = new Vector3[verts.Length + extra.Count];
            var allUvs = new Vector2[uvs.Length + extraUv.Count];
            verts.CopyTo(allVerts, 0);
            uvs.CopyTo(allUvs, 0);
            for (int i = 0; i < extra.Count; i++) { allVerts[verts.Length + i] = extra[i]; allUvs[uvs.Length + i] = extraUv[i]; }

            var mesh = new Mesh();
            mesh.vertices = allVerts;
            mesh.uv = allUvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Deterministic 0..1 hash. Not an RNG — it takes no state and advances nothing, which is
        /// precisely why it is safe to call from inside the forest loop (UNITY_PORT_NOTES [rng-lockstep]).
        ///
        /// PUBLIC because it is the project's canonical index hash: every builder that wants
        /// per-instance variation must go through something like this rather than reach for a
        /// System.Random, and having one shared implementation is what keeps that rule easy to follow.
        /// </summary>
        public static float Hash01(int n)
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
