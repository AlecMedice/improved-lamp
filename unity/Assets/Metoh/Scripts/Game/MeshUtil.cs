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
        /// (UNITY_NOTES [rng-lockstep]) and drawing one extra number here would offset every tree after it.
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
        /// <param name="xScale">Squash factors baked into the MESH rather than applied by the
        /// transform. This is not a convenience: a non-uniform transform scale shears tangent space,
        /// so a normal map on the result is being read through a skew, and it stretches UV tiling
        /// anisotropically so one surface shows its grain at different densities depending on which
        /// way it faces. Building the proportions in keeps the UVs (which are in metres here) honest
        /// and the tangents square. The cave mounds carried a 13 : 7.2 : 11 transform squash until
        /// this existed.</param>
        public static Mesh Rock(float radius, int rings, int segments, int variant,
                                float xScale = 1f, float zScale = 1f, float yScale = 1f)
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
                    verts[i] = new Vector3(sp * Mathf.Cos(theta) * rr * xScale,
                                           cp * rr * yScale,
                                           sp * Mathf.Sin(theta) * rr * zScale);
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
        /// (UNITY_NOTES [legibility]) reappearing on a creature instead of on a tree.
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
        ///
        /// **THIS IS WHAT THE FIRST VERSION GOT WRONG, and it is worth understanding before touching
        /// it.** That version spent a fixed 18% OF THE LENGTH at each end tapering the radius to
        /// exactly zero — a spindle, not a capsule. Points have no volume to overlap with, so every
        /// joint in every body pinched to a single vertex: a knee bent 72 degrees showed daylight
        /// through the leg, and the owner's report was "they're literally like stick people". The
        /// doc comment above was already describing the intent correctly; only the code disagreed.
        ///
        /// So the caps are hemispheres sized from the RADIUS and added BEYOND the span, not carved out
        /// of it. Two consequences, both wanted:
        ///
        /// - The mesh runs from <c>-radiusStart*capScale</c> to <c>length + radiusEnd*capScale</c>, so
        ///   a segment bulges PAST its own joint at both ends. That overlap is exactly what makes an
        ///   elbow read as continuous while it bends.
        /// - <paramref name="length"/> stays honest as the joint-to-joint distance, so the skeleton
        ///   numbers in Avatar.cs still mean what they say and no anchor moves.
        ///
        /// <paramref name="capScale"/> of 0 restores the old taper-to-a-point, which is not a
        /// compatibility shim — it is what an ICICLE wants, and the cave spikes ask for it by name.
        ///
        /// NOTE ON ORIENTATION, because it caused a second bug. This builds along +Y, but limbs hang
        /// DOWN from their joint, so Avatar.PartDown flips the mesh 180 degrees about X. After that
        /// flip <paramref name="radiusStart"/> lands at the PROXIMAL joint (shoulder, hip) and
        /// <paramref name="radiusEnd"/> at the DISTAL one (elbow, knee). The parameters used to be
        /// called radiusBottom/radiusTop, which described the mesh before the flip and therefore meant
        /// the opposite of what every caller assumed: all eight limb segments in the game were built
        /// thin at the shoulder and fat at the elbow. Start = at the joint, End = away from it.
        /// </summary>
        public static Mesh Limb(float radiusStart, float radiusEnd, float length,
                                int rings, int segments, int variant, float jag = 0.04f,
                                float capScale = 1f)
        {
            rings = Mathf.Max(rings, 5);

            // Pointed: the original profile, kept for spikes. Taper happens INSIDE the length.
            if (capScale <= 0f)
            {
                var pointed = new Vector2[rings];
                const float cap = 0.18f; // fraction of the length each tapered end occupies
                for (int r = 0; r < rings; r++)
                {
                    float t = r / (float)(rings - 1);
                    float rad = Mathf.Lerp(radiusStart, radiusEnd, t);
                    if (t < cap) rad *= Mathf.Sin(t / cap * Mathf.PI * 0.5f);
                    else if (t > 1f - cap) rad *= Mathf.Sin((1f - t) / cap * Mathf.PI * 0.5f);
                    pointed[r] = new Vector2(t * length, rad);
                }
                return Lathe(pointed, segments, variant, jag);
            }

            // Rounded. Three rings per cap sample the quarter-circle at 0/30/60 degrees; the core's own
            // end ring is the 90 the quarter closes on, so the cap meets the shaft tangentially with no
            // extra ring and no crease.
            const int capRings = 3;
            float capS = radiusStart * capScale, capE = radiusEnd * capScale;
            var profile = new Vector2[capRings + rings + capRings];
            int k = 0;

            for (int r = 0; r < capRings; r++)
            {
                float ang = r / (float)capRings * Mathf.PI * 0.5f;
                profile[k++] = new Vector2(-capS * Mathf.Cos(ang), radiusStart * Mathf.Sin(ang));
            }
            for (int r = 0; r < rings; r++)
            {
                float t = r / (float)(rings - 1);
                profile[k++] = new Vector2(t * length, Mathf.Lerp(radiusStart, radiusEnd, t));
            }
            for (int r = capRings - 1; r >= 0; r--)
            {
                float ang = r / (float)capRings * Mathf.PI * 0.5f;
                profile[k++] = new Vector2(length + capE * Mathf.Cos(ang), radiusEnd * Mathf.Sin(ang));
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
        /// A ring, centred on the origin with its hole along +Y — belts, cuffs, collars, a hood ruff.
        ///
        /// WHY THIS IS WORTH A PRIMITIVE. A body built only from ellipsoids and tapered limbs has no
        /// way to say "this is a person wearing clothes" rather than "this is a person-shaped solid".
        /// Clothing announces itself at the places it STOPS — the hem, the belt, where a sleeve ends
        /// at a glove, where a trouser leg ends at a boot. Each of those is a ring, and each one is a
        /// hard horizontal break in a silhouette that would otherwise be one continuous taper. At the
        /// range these are read at, those breaks do more than any amount of surface detail: they are
        /// the difference between a mannequin and a figure in a parka.
        ///
        /// Wound to match <see cref="Lathe"/> exactly, which is the only reason the normals come out
        /// facing outward. The cross-section is traversed COUNTERCLOCKWISE in the (radius, y) half
        /// plane — outer equator, over the top, down the inside, under the bottom — which is the same
        /// direction <see cref="Blob"/>'s profile runs, and consistent winding is what that buys.
        ///
        /// <paramref name="xScale"/>/<paramref name="zScale"/> squash the ring off-circular. A hood
        /// ruff is wider than it is tall, and a belt is wider than it is deep, for the same reason a
        /// torso is: bodies are flat front-to-back and a circular ring around one reads as a hoop.
        /// </summary>
        public static Mesh Torus(float majorRadius, float minorRadius, int majorSegs, int minorSegs,
                                 int variant, float jag = 0f, float xScale = 1f, float zScale = 1f)
        {
            majorSegs = Mathf.Max(majorSegs, 5);
            minorSegs = Mathf.Max(minorSegs, 4);
            int cols = majorSegs + 1, rows = minorSegs + 1;

            var verts = new Vector3[rows * cols];
            var uvs = new Vector2[rows * cols];
            for (int r = 0; r < rows; r++)
            {
                // Wrapped so the last row lands on the first row's exact angle — a ring closes in BOTH
                // directions, unlike a lathe, which only has the one seam column.
                int rw = r % minorSegs;
                float b = rw / (float)minorSegs * Mathf.PI * 2f;
                float ringRad = majorRadius + Mathf.Cos(b) * minorRadius;
                float y = Mathf.Sin(b) * minorRadius;
                for (int s = 0; s < cols; s++)
                {
                    int sw = s % majorSegs;
                    float a = sw / (float)majorSegs * Mathf.PI * 2f;
                    float k = jag > 0f
                        ? 1f + (Hash01(variant * 7919 + rw * 131 + sw * 37) - 0.5f) * 2f * jag
                        : 1f;
                    float rr = ringRad * k;

                    int i = r * cols + s;
                    verts[i] = new Vector3(Mathf.Cos(a) * rr * xScale, y * k, Mathf.Sin(a) * rr * zScale);
                    uvs[i] = new Vector2(s / (float)majorSegs, r / (float)minorSegs);
                }
            }

            var tris = new System.Collections.Generic.List<int>(rows * majorSegs * 6);
            for (int r = 0; r < rows - 1; r++)
            {
                for (int s = 0; s < majorSegs; s++)
                {
                    int a = r * cols + s, b2 = a + 1;
                    int c = (r + 1) * cols + s, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b2);
                }
            }

            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();

            // Seal BOTH seams, for the reason spelled out on Lathe: RecalculateNormals averages by
            // vertex index, so co-located duplicates each see half the surrounding faces and light
            // differently. A ring has two of them — one down the major seam, one around the minor.
            var norms = mesh.normals;
            for (int r = 0; r < rows; r++)
            {
                int a = r * cols, b = r * cols + majorSegs;
                Vector3 avg = (norms[a] + norms[b]).normalized;
                norms[a] = avg; norms[b] = avg;
            }
            for (int s = 0; s < cols; s++)
            {
                int a = s, b = (rows - 1) * cols + s;
                Vector3 avg = (norms[a] + norms[b]).normalized;
                norms[a] = avg; norms[b] = avg;
            }
            mesh.normals = norms;

            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
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
        /// precisely why it is safe to call from inside the forest loop (UNITY_NOTES [rng-lockstep]).
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
        /// <summary>
        /// A cave throat: a tapering tube of INWARD-facing faces with a closed back — the shape a hole
        /// actually is.
        ///
        /// WHY THIS TYPE HAD TO EXIST. Every previous cave mouth was a convex mass (a scaled sphere,
        /// then an irregular Rock) painted near-black. Convex geometry bulges toward the viewer and can
        /// only read as a dark ROCK; concavity is not a texture problem or a lighting problem, it is a
        /// topology problem, and the only fix is faces that point back at you from the far side.
        ///
        /// Windings are reversed against every other generator here, which is the whole trick: you are
        /// meant to be standing inside the bore looking out through its walls. It also means the throat
        /// is invisible from behind, so it can be pushed into a hillside without capping it.
        ///
        /// It narrows and drops as it recedes (<paramref name="depth"/> along -Z after the caller's
        /// 180° yaw), because a passage that keeps its bore reads as a pipe. The jag is hashed from
        /// <paramref name="variant"/> so each crevasse gets its own irregular bore.
        /// </summary>
        public static Mesh Throat(float mouthRadiusX, float mouthRadiusY, float depth,
                                  int segments, int rings, int variant)
        {
            segments = Mathf.Max(segments, 6);
            rings = Mathf.Max(rings, 3);
            int cols = segments + 1;

            var verts = new Vector3[rings * cols];
            var uvs = new Vector2[rings * cols];
            for (int r = 0; r < rings; r++)
            {
                float t = r / (float)(rings - 1);
                // Cubic taper: wide at the lip, closing fast. A linear taper reads as a funnel.
                float shrink = Mathf.Lerp(1f, 0.06f, t * t * t);
                float z = t * depth;
                float sag = t * t * 0.9f; // the floor rises / the roof drops as it goes back
                for (int s = 0; s < cols; s++)
                {
                    int sw = s % segments;
                    float a = sw / (float)segments * Mathf.PI * 2f;
                    float k = 1f + (Hash01(variant * 7717 + r * 131 + sw * 29) - 0.5f) * 0.34f;
                    float x = Mathf.Cos(a) * mouthRadiusX * shrink * k;
                    float y = Mathf.Sin(a) * mouthRadiusY * shrink * k - sag;
                    verts[r * cols + s] = new Vector3(x, y, z);
                    uvs[r * cols + s] = new Vector2(s / (float)segments, z);
                }
            }

            var tris = new System.Collections.Generic.List<int>(rings * segments * 6);
            for (int r = 0; r < rings - 1; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * cols + s, b = a + 1;
                    int c = (r + 1) * cols + s, d = c + 1;
                    // REVERSED against Lathe's winding — these faces must be visible from inside the
                    // bore. Wind them the usual way and the throat vanishes entirely, leaving a hole
                    // you can see the hillside through.
                    tris.Add(a); tris.Add(d); tris.Add(c);
                    tris.Add(a); tris.Add(b); tris.Add(d);
                }
            }

            var m = new Mesh { name = "Throat" };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }

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

        /// <summary>
        /// A box whose UVs are in METRES, so a normal map keeps the same texel density on every face
        /// no matter how the box is proportioned.
        ///
        /// THE BUG THIS FIXES was everywhere. Unity's primitive cube gives each face 0..1 UVs, so a
        /// material tiled at, say, 1.4 repeats puts 1.4 repeats across a face regardless of its real
        /// size. Scale that cube to the basecamp hut's 6.6 x 2.5 x 2.3 and the timber grain is
        /// stretched about 3x along the long walls compared with the ends — same plank texture,
        /// visibly different plank width depending on which side you are standing at. Every AddBox
        /// object had it: hut, sill, roof slabs, ridge beam, crates, tower platform and rails.
        ///
        /// Taking the size here rather than scaling a shared cube is what makes the fix possible: UVs
        /// have to be built from the real dimensions, and a transform scale applied afterwards cannot
        /// know them. Each face is unwrapped against the two world axes it spans, which also means
        /// adjacent faces agree at the corners.
        /// </summary>
        public static Mesh MetricBox(Vector3 size)
        {
            Vector3 h = size * 0.5f;
            var verts = new System.Collections.Generic.List<Vector3>(24);
            var uvs = new System.Collections.Generic.List<Vector2>(24);
            var norms = new System.Collections.Generic.List<Vector3>(24);
            var tris = new System.Collections.Generic.List<int>(36);

            void Face(Vector3 n, Vector3 u, Vector3 v, float uLen, float vLen)
            {
                int b = verts.Count;
                Vector3 c = Vector3.Scale(n, h);      // face centre
                Vector3 hu = u * (uLen * 0.5f), hv = v * (vLen * 0.5f);
                verts.Add(c - hu - hv); verts.Add(c + hu - hv);
                verts.Add(c + hu + hv); verts.Add(c - hu + hv);
                // UV in metres — this is the entire point of the type.
                uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(uLen, 0f));
                uvs.Add(new Vector2(uLen, vLen)); uvs.Add(new Vector2(0f, vLen));
                for (int i = 0; i < 4; i++) norms.Add(n);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }

            Face(Vector3.up,      Vector3.right,   Vector3.forward, size.x, size.z);
            Face(Vector3.down,    Vector3.right,   Vector3.back,    size.x, size.z);
            Face(Vector3.forward, Vector3.left,    Vector3.up,      size.x, size.y);
            Face(Vector3.back,    Vector3.right,   Vector3.up,      size.x, size.y);
            Face(Vector3.right,   Vector3.forward, Vector3.up,      size.z, size.y);
            Face(Vector3.left,    Vector3.back,    Vector3.up,      size.z, size.y);

            var m = new Mesh { name = "MetricBox" };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetNormals(norms);
            m.SetTriangles(tris, 0);
            m.RecalculateTangents(); // normal-mapped geometry needs tangents or it renders flat
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// A <see cref="Surface"/> that also sways in the wind — the forest's material.
        ///
        /// Falls back to a plain Lit surface if the shader is missing. That matters more than it
        /// looks: Shader.Find returns null only for a MISSING file, while a shader that fails to
        /// COMPILE returns a valid object that renders MAGENTA (the trap UNITY_NOTES flags for
        /// Metoh/Snowpack, and the reason the terrain shader went a whole pass without ever parsing).
        /// A silently rigid forest is a far better failure than a magenta one.
        /// </summary>
        public static Material Sway(Color color, float smoothness, Texture2D normal, float normalScale,
                                    float tiling, float strength)
        {
            var shader = Shader.Find("Metoh/TreeSway");
            if (shader == null)
            {
                BootReport.MissingShader("Metoh/TreeSway", "the forest is rigid — no wind sway at all");
                return Surface(color, smoothness, normal, normalScale, tiling);
            }

            var m = new Material(shader);
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.SetFloat("_BumpScale", normalScale);
            }
            m.SetFloat("_WindStrength", strength);
            return m;
        }

        /// <summary>
        /// Destroy a generated object from either play mode or the editor. Unity throws on
        /// <c>Destroy</c> outside play mode, and the headless scene rebuild ([workflow]) runs there.
        /// </summary>
        public static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        /// <summary>
        /// Accumulate several generated meshes and weld them into ONE.
        ///
        /// WHY THIS EXISTS. A hand with fingers, a boot with a sole and a toe box, a head of hair —
        /// each is five to a dozen small lathes, and giving every piece its own GameObject would take a
        /// searcher past a hundred renderers. **Renderers are the expensive unit here, not triangles:**
        /// every one is a culling entry and a candidate draw call, and [perf] is written about a machine
        /// with integrated graphics. Welding them means a hand with four fingers and a thumb costs
        /// exactly what the single ellipsoid it replaces did.
        ///
        /// Parts are posed in the finished part's local space, combined once at build time, and the
        /// sources destroyed here — they are native objects the GC never collects, and they must never
        /// reach a caller's tracking list, because nothing else would ever release them ([materials]'s
        /// leak rule).
        ///
        /// Everything <see cref="Lathe"/> produces carries position, normal, UV and tangent, which is
        /// what lets CombineMeshes weld them without dropping an attribute. That matters more than it
        /// looks: a combined mesh missing tangents renders FLAT under the normal maps [materials] is
        /// built on, and it does it silently, with no error anywhere.
        /// </summary>
        public sealed class MeshGroup
        {
            private readonly System.Collections.Generic.List<CombineInstance> _parts =
                new System.Collections.Generic.List<CombineInstance>();

            public MeshGroup Add(Mesh m, Vector3 pos) { return Add(m, pos, Vector3.zero, Vector3.one); }

            public MeshGroup Add(Mesh m, Vector3 pos, Vector3 euler) { return Add(m, pos, euler, Vector3.one); }

            public MeshGroup Add(Mesh m, Vector3 pos, Vector3 euler, Vector3 scale)
            {
                if (m == null) return this;
                _parts.Add(new CombineInstance
                {
                    mesh = m,
                    transform = Matrix4x4.TRS(pos, Quaternion.Euler(euler), scale),
                });
                return this;
            }

            /// <summary>Weld and hand back the result. The group is spent afterwards.</summary>
            public Mesh Build()
            {
                var mesh = new Mesh();
                // 16-bit indices are ample for a hand, but the failure mode when a part group does run
                // past 65k verts is a silently truncated mesh rather than an error, so don't gamble.
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.CombineMeshes(_parts.ToArray(), true, true);
                for (int i = 0; i < _parts.Count; i++) Kill(_parts[i].mesh);
                _parts.Clear();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        /// <summary>
        /// Build a URP particle material that is actually transparent.
        ///
        /// **READ THIS BEFORE BUILDING ANY TRANSPARENT MATERIAL FROM CODE.** URP does NOT derive its
        /// blend state from the shader alone. `new Material(shader)` starts from the shader's DEFAULT
        /// property values, which are opaque — `_SrcBlend` One, `_DstBlend` Zero, `_ZWrite` on — and
        /// the `_Surface`/`_Blend` floats that look like they select transparency are only inputs to
        /// the material EDITOR's validation step. Nothing applies them at runtime. So a material set
        /// up with `_Surface = 1` and nothing else renders fully opaque, with depth writes on, and a
        /// soft-dot particle texture becomes a hard SQUARE of flat colour.
        ///
        /// That is not hypothetical: it shipped in the campfire. The owner's report was *"the fire
        /// smoke is boxes of black"* — which is exactly this, the smoke's dark start colour drawn as
        /// opaque quads. `Weather` had already hit the same bug on snowflakes, diagnosed it correctly,
        /// and written a private fix; the fire never got it. **One bug, two call sites, one of them
        /// fixed — so the fix now lives in one place that both use.** Anything else transparent built
        /// at runtime should come through here too.
        ///
        /// <paramref name="additive"/> selects the blend: additive for things that EMIT (flame,
        /// sparks, the torch's motes) and alpha for things that OCCLUDE (smoke, snow). Getting that
        /// backwards is the other classic fire bug — additive smoke glows instead of blocking, which
        /// stops it reading as matter.
        /// </summary>
        public static Material ParticleMaterial(Texture2D tex, bool additive)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                      ?? Shader.Find("Universal Render Pipeline/Unlit")
                      ?? Shader.Find("Sprites/Default");
            var m = new Material(shader);
            if (tex != null)
            {
                m.SetTexture("_BaseMap", tex);
                m.mainTexture = tex;
            }
            MakeTransparent(m, additive);
            return m;
        }

        /// <summary>
        /// Put an existing URP material into transparent mode. Every one of these has to agree — the
        /// _Surface/_Blend floats, the two blend factors, ZWrite, the render queue, the RenderType tag
        /// AND the _SURFACE_TYPE_TRANSPARENT keyword. Setting a subset is the usual way a
        /// runtime-built transparent material comes out opaque. See <see cref="ParticleMaterial"/>.
        /// </summary>
        public static void MakeTransparent(Material m, bool additive)
        {
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", additive ? 1f : 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)(additive
                ? UnityEngine.Rendering.BlendMode.One
                : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>
        /// A flag: a thin sheet in the XY plane, hoist edge on the Y axis at x=0, flying out to +X.
        ///
        /// **The UVs are the contract with Metoh/Flag** and the mesh is useless without them: u runs 0
        /// at the lashed edge to 1 at the free edge, and the shader scales every displacement by u so
        /// the hoist stays welded to its pole. v runs top to bottom and is currently unused, but it is
        /// there so a future shader can hang the corners.
        ///
        /// Segmented along its length, because a ripple is a wave and a wave needs vertices to exist
        /// at all — the single quad this replaces could only ever tilt. Eight columns is enough for
        /// two visible crests without being able to alias into one.
        ///
        /// Unit-sized (1 x 1) on purpose: both call sites scale it, and the shader works off UVs, so a
        /// 13 cm prayer flag and a 55 cm marker flag share one mesh and one set of instructions.
        /// </summary>
        public static Mesh FlagSheet(int cols = 8, int rows = 2)
        {
            cols = Mathf.Max(cols, 2);
            rows = Mathf.Max(rows, 1);
            var verts = new Vector3[(cols + 1) * (rows + 1)];
            var uvs = new Vector2[verts.Length];
            var norms = new Vector3[verts.Length];
            for (int r = 0; r <= rows; r++)
            {
                for (int c = 0; c <= cols; c++)
                {
                    int i = r * (cols + 1) + c;
                    float u = c / (float)cols, v = r / (float)rows;
                    verts[i] = new Vector3(u, 0.5f - v, 0f);
                    uvs[i] = new Vector2(u, v);
                    norms[i] = Vector3.back;
                }
            }
            var tris = new int[cols * rows * 6];
            int t = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int i0 = r * (cols + 1) + c, i1 = i0 + 1;
                    int i2 = i0 + cols + 1, i3 = i2 + 1;
                    tris[t++] = i0; tris[t++] = i2; tris[t++] = i1;
                    tris[t++] = i1; tris[t++] = i2; tris[t++] = i3;
                }
            }
            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = norms;   // set by hand: a flat sheet's normals are known, and RecalculateNormals
            mesh.triangles = tris;  // on a zero-thickness sheet is a coin flip on sign
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            // Bounds have to survive the shader pushing vertices off the sheet, or a rippling flag gets
            // frustum-culled early and pops at the screen edge. Grown generously; it costs nothing.
            var b = mesh.bounds;
            b.Expand(new Vector3(0.4f, 0.4f, 0.6f));
            mesh.bounds = b;
            return mesh;
        }

        /// <summary>
        /// Soft particles plus a near-camera fade, on a URP particle material.
        ///
        /// A billboard is a flat quad, so where it passes through the ground, a trunk or a wall the
        /// depth test slices it and it terminates in a hard straight line across its own face. On
        /// smoke, whose puffs are metres across and sit directly on top of a fire that is sitting on
        /// the ground, that line runs through nearly every particle — so the column reads as a stack
        /// of intersecting cards rather than as smoke. Fading against scene depth is what removes it.
        ///
        /// The camera fade does the same job at the other end: a particle the near plane clips through
        /// flashes as a half-quad, which is most of what "walking through smoke looks broken" is.
        ///
        /// <paramref name="softFar"/> is the fade distance in metres and wants to scale with the
        /// particle: 0.75 for a snowflake, but a two-metre smoke puff needs metres or the fade is
        /// narrower than the intersection it is hiding.
        /// </summary>
        public static void SetSoftParticles(Material m, float softFar = 0.75f,
                                            float camNear = 0.3f, float camFar = 0.9f)
        {
            const float softNear = 0f;
            m.SetFloat("_SoftParticlesEnabled", 1f);
            m.SetFloat("_SoftParticlesNearFadeDistance", softNear);
            m.SetFloat("_SoftParticlesFarFadeDistance", softFar);
            m.SetVector("_SoftParticleFadeParams",
                new Vector4(softNear, 1f / Mathf.Max(0.0001f, softFar - softNear), 0f, 0f));
            m.EnableKeyword("_SOFTPARTICLES_ON");

            m.SetFloat("_CameraFadingEnabled", 1f);
            m.SetFloat("_CameraNearFadeDistance", camNear);
            m.SetFloat("_CameraFarFadeDistance", camFar);
            m.SetVector("_CameraFadeParams",
                new Vector4(camNear, 1f / Mathf.Max(0.0001f, camFar - camNear), 0f, 0f));
            m.EnableKeyword("_FADING_ON");
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
