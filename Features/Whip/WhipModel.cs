using System;

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The whip built in code: a short leather handle and a tapered lash, one material, no texture.
    ///
    /// Model space: the grip (where the fist closes) is the origin, the handle points along +Z, +Y is up. The lash
    /// starts at the handle tip. In hand <see cref="WhipLash"/> moves the lash every frame; the icon uses a lash
    /// laid out in a curve on the ground plane.
    /// </summary>
    internal static class WhipModel
    {
        internal const string LashName = "AWEWhipLash";
        internal const float HandleTipZ = 0.11f;
        internal const float LashLength = 0.6f;
        internal const int LashPoints = 14;
        internal const int LashSides = 6;
        private const float LashBaseRadius = 0.0065f;
        private const float LashTipRadius = 0.0014f;

        // Dark leather. One colour for handle and lash, so nothing reads as bands or stripes.
        private static readonly Color Leather = new Color(0.25f, 0.14f, 0.075f);
        private static Material _material;

        /// <summary>The whip with a hanging lash (<paramref name="restingCurve"/> false) or a lash lying in a curve, for the icon.</summary>
        internal static GameObject Build(string name, bool restingCurve)
        {
            var root = new GameObject(name);
            Material mat = SharedMaterial();

            // Handle: a short pommel, then the grip tapering towards the tip.
            Vector3[] hp = { new Vector3(0f, 0f, -0.034f), new Vector3(0f, 0f, -0.022f), new Vector3(0f, 0f, -0.021f), new Vector3(0f, 0f, HandleTipZ) };
            float[] hr = { 0.0145f, 0.0145f, 0.0112f, 0.0085f };
            var handle = new Mesh { name = "AWEWhipHandle" };
            WriteTube(handle, hp, hr, 10, true, true);
            AddPart(root.transform, "AWEWhipHandle", handle, mat);

            var lash = new Mesh { name = LashName };
            lash.MarkDynamic();
            WriteTube(lash, restingCurve ? RestingCurve() : Hanging(), LashRadii(), LashSides, false, true);
            AddPart(root.transform, LashName, lash, mat);
            return root;
        }

        internal static float[] LashRadii()
        {
            var r = new float[LashPoints];
            for (int i = 0; i < LashPoints; i++)
            {
                float u = (float)i / (LashPoints - 1);
                r[i] = Mathf.Lerp(LashBaseRadius, LashTipRadius, Mathf.Pow(u, 0.8f));
            }
            return r;
        }

        /// <summary>The lash at rest in hand: from the handle tip forward and down. <see cref="WhipLash"/> takes over from here.</summary>
        internal static Vector3[] Hanging()
        {
            var p = new Vector3[LashPoints];
            float seg = LashLength / (LashPoints - 1);
            Vector3 dir = new Vector3(0f, -0.8f, 0.6f).normalized;
            for (int i = 0; i < LashPoints; i++) p[i] = new Vector3(0f, 0f, HandleTipZ) + dir * (seg * i);
            return p;
        }

        /// <summary>For the icon: the lash lies flat and curls round to one side, so handle and lash both read from above.</summary>
        private static Vector3[] RestingCurve()
        {
            var p = new Vector3[LashPoints];
            float seg = LashLength / (LashPoints - 1);
            p[0] = new Vector3(0f, 0f, HandleTipZ);
            for (int i = 1; i < LashPoints; i++)
            {
                float u = (float)i / (LashPoints - 1);
                float heading = 2.9f * Mathf.Pow(u, 1.25f);   // radians, turning tighter towards the tip
                p[i] = p[i - 1] + new Vector3(Mathf.Sin(heading), 0f, Mathf.Cos(heading)) * seg;
            }
            return p;
        }

        /// <summary>
        /// Writes a tube along <paramref name="pts"/> into the mesh: one ring of <paramref name="sides"/> vertices per point,
        /// rings oriented by parallel transport so the tube does not twist, normals pointing out of the ring. With
        /// <paramref name="caps"/> both ends are closed. <paramref name="setTriangles"/> only on the first write; later
        /// writes keep the vertex count and only move the vertices.
        /// </summary>
        internal static void WriteTube(Mesh mesh, Vector3[] pts, float[] radii, int sides, bool caps, bool setTriangles)
        {
            int n = pts.Length;
            int ringVerts = n * sides;
            int total = ringVerts + (caps ? 2 * (sides + 1) : 0);
            var v = new Vector3[total];
            var nm = new Vector3[total];

            Vector3 t0 = (pts[1] - pts[0]).normalized;
            Vector3 normal = Vector3.Cross(t0, Mathf.Abs(t0.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            for (int i = 0; i < n; i++)
            {
                Vector3 t = i == 0 ? pts[1] - pts[0] : i == n - 1 ? pts[n - 1] - pts[n - 2] : pts[i + 1] - pts[i - 1];
                t = t.sqrMagnitude > 1e-12f ? t.normalized : t0;
                normal = (normal - Vector3.Dot(normal, t) * t);
                normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.Cross(t, Vector3.right).normalized;
                Vector3 bin = Vector3.Cross(t, normal);
                for (int k = 0; k < sides; k++)
                {
                    float a = k * (Mathf.PI * 2f / sides);
                    Vector3 d = Mathf.Cos(a) * normal + Mathf.Sin(a) * bin;
                    v[i * sides + k] = pts[i] + d * radii[i];
                    nm[i * sides + k] = d;
                }
            }
            if (caps)
            {
                for (int end = 0; end < 2; end++)
                {
                    int ring = end == 0 ? 0 : n - 1;
                    Vector3 outward = end == 0 ? -(pts[1] - pts[0]).normalized : (pts[n - 1] - pts[n - 2]).normalized;
                    int start = ringVerts + end * (sides + 1);
                    for (int k = 0; k < sides; k++) { v[start + k] = v[ring * sides + k]; nm[start + k] = outward; }
                    v[start + sides] = pts[ring];
                    nm[start + sides] = outward;
                }
            }
            mesh.vertices = v;
            mesh.normals = nm;

            if (setTriangles)
            {
                int tris = (n - 1) * sides * 6 + (caps ? 2 * sides * 3 : 0);
                var idx = new int[tris];
                int w = 0;
                for (int i = 0; i < n - 1; i++)
                    for (int k = 0; k < sides; k++)
                    {
                        int a = i * sides + k, b = i * sides + (k + 1) % sides, c = (i + 1) * sides + k, d = (i + 1) * sides + (k + 1) % sides;
                        idx[w++] = a; idx[w++] = c; idx[w++] = b;
                        idx[w++] = b; idx[w++] = c; idx[w++] = d;
                    }
                if (caps)
                {
                    for (int end = 0; end < 2; end++)
                    {
                        int start = ringVerts + end * (sides + 1);
                        int centre = start + sides;
                        for (int k = 0; k < sides; k++)
                        {
                            int a = start + k, b = start + (k + 1) % sides;
                            if (end == 0) { idx[w++] = centre; idx[w++] = b; idx[w++] = a; }
                            else { idx[w++] = centre; idx[w++] = a; idx[w++] = b; }
                        }
                    }
                }
                mesh.triangles = idx;
            }
            mesh.RecalculateBounds();
        }

        private static void AddPart(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>
        /// The game's lit shader in one flat leather colour, drawn from both sides so a ring winding the wrong way never
        /// shows a hole. Falls back to Standard and then to an unlit sprite shader, so the whip stays visible.
        /// </summary>
        private static Material SharedMaterial()
        {
            if (_material != null) return _material;
            Shader shader = null;
            try { shader = Shader.Find("Universal Render Pipeline/Lit"); } catch (Exception e) { Core.Caught("whip: find the URP lit shader", e); }
            if (shader == null)
            {
                Core.Log?.Warning("[Whip] URP lit shader not found, the whip uses Standard.");
                try { shader = Shader.Find("Standard"); } catch (Exception e) { Core.Caught("whip: find the standard shader", e); }
            }
            var mat = shader != null ? new Material(shader) : new Material(Shader.Find("Sprites/Default"));
            mat.name = "AWEWhipLeather";
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            try
            {
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Leather);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", Leather);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.28f);
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
                if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
                mat.doubleSidedGI = true;
            }
            catch (Exception e) { Core.Caught("whip: set up the leather material", e); }
            _material = mat;
            return mat;
        }
    }
}
