using System.Collections.Generic;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// Procedural curved 3D spatial UI geometry &amp; brushed-metal card textures:
    /// - Parabolic Y-arch + cylindrical Z-wrap toward the viewer (`EvaluateArc`)
    /// - Rounded-rectangle cards with steel rims, graphite fill, subtle bevel (web METAL palette)
    /// - Curved 3D backing plates with metal perimeter rails (no neon cyan)
    /// </summary>
    public static class XrCurvedWidgets
    {
        static readonly Dictionary<string, Texture2D> CardTexCache = new Dictionary<string, Texture2D>();
        static readonly Dictionary<string, Material> CardMatCache = new Dictionary<string, Material>();

        /// <summary>Cool steel rim (#9aa3ad) — shared with web dock chrome.</summary>
        public static readonly Color MetalRim = new Color(0.604f, 0.639f, 0.678f, 0.96f);
        /// <summary>Dark graphite plate fill.</summary>
        public static readonly Color MetalFill = new Color(0.10f, 0.12f, 0.15f, 0.96f);
        /// <summary>Amber active accent (#d4a574) — hamburger / selected only.</summary>
        public static readonly Color MetalAccent = new Color(0.831f, 0.647f, 0.455f, 0.95f);

        /// <summary>
        /// Evaluates the curved dock/panel surface at horizontal offset <paramref name="x"/> (in meters):
        /// - Center (<c>x = 0</c>) bows slightly UP in Y (`+archHeight`) and sits at <c>z = 0</c>.
        /// - Outer wings curve gently DOWN in Y and wrap FORWARD toward the viewer (<c>-Z</c> in XrUiFacing)
        ///   with matching inward yaw (`localRot`) so every button faces the viewer in 3D!
        /// </summary>
        public static void EvaluateArc(
            float x,
            float halfSpan,
            float archHeight,
            float depthWrap,
            out Vector3 localOffset,
            out Quaternion localRot)
        {
            float nx = halfSpan > 1e-4f ? Mathf.Clamp(x / halfSpan, -1.25f, 1.25f) : 0f;
            float nx2 = nx * nx;

            // Center is arched up (+archHeight * 0.5), wings curve down (-archHeight * 0.5)
            float dy = archHeight * (0.45f - nx2);
            // Outer wings wrap toward viewer (-Z in XrUiFacing convention)
            float dz = -depthWrap * nx2;

            localOffset = new Vector3(x, dy, dz);

            // Derivatives for smooth tangent orientation:
            // d(dy)/dx = -2 * archHeight * nx / halfSpan -> Z-roll so buttons follow the arch!
            // d(dz)/dx = -2 * depthWrap * nx / halfSpan  -> Y-yaw so outer wings angle toward viewer!
            float rollDeg = Mathf.Atan(-2f * archHeight * nx / Mathf.Max(halfSpan, 0.2f)) * Mathf.Rad2Deg * 0.65f;
            float yawDeg = -Mathf.Atan(2f * depthWrap * nx / Mathf.Max(halfSpan, 0.2f)) * Mathf.Rad2Deg * 0.90f;
            localRot = Quaternion.Euler(0f, yawDeg, rollDeg);
        }

        /// <summary>
        /// Builds a 3D curved backing plate with a dark bevelled back-layer, graphite face,
        /// and brushed-steel top &amp; bottom border rails following the parabolic arch.
        /// </summary>
        public static Transform CreateCurvedPanelPlate(
            string name,
            Transform parent,
            float width,
            float height,
            Color fillColor,
            Color rimColor,
            float archHeight = 0.018f,
            float depthWrap = 0.045f,
            int renderQueue = 2992)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);

            // 1. 3D Shadow/Bevel Depth Plate slightly behind (+Z) and slightly larger
            var depthGo = new GameObject("DepthBevel3D");
            depthGo.transform.SetParent(root, false);
            depthGo.transform.localPosition = new Vector3(0f, -0.003f, 0.010f);
            var depthMf = depthGo.AddComponent<MeshFilter>();
            depthMf.sharedMesh = BuildCurvedStripMesh(width + 0.016f, height + 0.016f, archHeight, depthWrap, 28);
            var depthMr = depthGo.AddComponent<MeshRenderer>();
            depthMr.sharedMaterial = GetRoundedPanelMaterial(
                new Color(0.01f, 0.03f, 0.06f, 0.94f),
                new Color(rimColor.r * 0.55f, rimColor.g * 0.55f, rimColor.b * 0.55f, 0.70f),
                renderQueue - 2);
            depthMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            depthMr.receiveShadows = false;

            // 2. Main curved metal face with steel perimeter border
            var faceGo = new GameObject("CurvedFace");
            faceGo.transform.SetParent(root, false);
            faceGo.transform.localPosition = new Vector3(0f, 0f, 0.004f);
            var faceMf = faceGo.AddComponent<MeshFilter>();
            faceMf.sharedMesh = BuildCurvedStripMesh(width, height, archHeight, depthWrap, 28);
            var faceMr = faceGo.AddComponent<MeshRenderer>();
            faceMr.sharedMaterial = GetRoundedPanelMaterial(fillColor, rimColor, renderQueue);
            faceMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            faceMr.receiveShadows = false;

            return root;
        }

        /// <summary>
        /// Creates a rounded-rectangle button card quad with a 3D depth backing plate and
        /// crisp border outline (matching the Stitch dock cards).
        /// </summary>
        public static Transform CreateRoundedButtonCard(
            string name,
            Transform parent,
            Vector2 size,
            Color fillColor,
            Color borderColor,
            int renderQueue = 2998)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);

            // 3D recessed shadow/bevel layer behind the button (+0.004m Z)
            var bevel = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bevel.name = "CardBevel3D";
            Object.Destroy(bevel.GetComponent<Collider>());
            bevel.transform.SetParent(root, false);
            bevel.transform.localPosition = new Vector3(0f, -0.002f, 0.004f);
            bevel.transform.localScale = new Vector3(size.x + 0.005f, size.y + 0.005f, 1f);
            var br = bevel.GetComponent<Renderer>();
            br.sharedMaterial = GetRoundedCardMaterial(
                new Color(0.01f, 0.03f, 0.06f, 0.90f),
                new Color(borderColor.r * 0.5f, borderColor.g * 0.5f, borderColor.b * 0.5f, 0.55f),
                renderQueue - 1);
            br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            br.receiveShadows = false;

            // Main rounded card quad
            var card = GameObject.CreatePrimitive(PrimitiveType.Quad);
            card.name = "Bg";
            Object.Destroy(card.GetComponent<Collider>());
            card.transform.SetParent(root, false);
            card.transform.localPosition = Vector3.zero;
            card.transform.localScale = new Vector3(size.x, size.y, 1f);
            var cr = card.GetComponent<Renderer>();
            cr.sharedMaterial = GetRoundedCardMaterial(fillColor, borderColor, renderQueue);
            cr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cr.receiveShadows = false;

            return root;
        }

        static Mesh BuildCurvedStripMesh(float width, float height, float archHeight, float depthWrap, int segs)
        {
            var verts = new Vector3[(segs + 1) * 2];
            var uvs = new Vector2[(segs + 1) * 2];
            var tris = new int[segs * 6];
            float halfW = width * 0.5f;
            float halfH = height * 0.5f;

            for (int i = 0; i <= segs; i++)
            {
                float u = i / (float)segs;
                float x = Mathf.Lerp(-halfW, halfW, u);
                EvaluateArc(x, Mathf.Max(halfW, 0.25f), archHeight, depthWrap, out var offset, out _);

                verts[i * 2] = new Vector3(offset.x, offset.y - halfH, offset.z);
                verts[i * 2 + 1] = new Vector3(offset.x, offset.y + halfH, offset.z);
                uvs[i * 2] = new Vector2(u, 0f);
                uvs[i * 2 + 1] = new Vector2(u, 1f);
            }

            int t = 0;
            for (int i = 0; i < segs; i++)
            {
                int i0 = i * 2;
                int i1 = i0 + 1;
                int i2 = i0 + 2;
                int i3 = i0 + 3;
                // Readable face on -Z (XrUiFacing convention)
                tris[t++] = i0; tris[t++] = i1; tris[t++] = i3;
                tris[t++] = i0; tris[t++] = i3; tris[t++] = i2;
            }

            var mesh = new Mesh { name = "CurvedUiStrip" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Material GetRoundedCardMaterial(Color fill, Color border, int queue)
        {
            string key = $"card:{ColorUtility.ToHtmlStringRGBA(fill)}:{ColorUtility.ToHtmlStringRGBA(border)}:{queue}";
            if (CardMatCache.TryGetValue(key, out var mat) && mat != null)
                return mat;

            var tex = GetOrBuildRoundedRectTex(
                key, 128, 128, radiusFrac: 0.16f, borderFrac: 0.045f, fill, border);
            var sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Transparent");
            mat = new Material(sh);
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            mat.renderQueue = queue;
            CardMatCache[key] = mat;
            return mat;
        }

        static Material GetRoundedPanelMaterial(Color fill, Color border, int queue)
        {
            string key = $"panel:{ColorUtility.ToHtmlStringRGBA(fill)}:{ColorUtility.ToHtmlStringRGBA(border)}:{queue}";
            if (CardMatCache.TryGetValue(key, out var mat) && mat != null)
                return mat;

            var tex = GetOrBuildRoundedRectTex(
                key, 256, 128, radiusFrac: 0.14f, borderFrac: 0.032f, fill, border);
            var sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Transparent");
            mat = new Material(sh);
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            mat.renderQueue = queue;
            CardMatCache[key] = mat;
            return mat;
        }

        static Texture2D GetOrBuildRoundedRectTex(
            string key, int w, int h, float radiusFrac, float borderFrac, Color fill, Color border)
        {
            if (CardTexCache.TryGetValue(key, out var cached) && cached != null)
                return cached;

            var px = new Color32[w * h];
            float aspect = w / (float)h;
            float rx = radiusFrac;
            float ry = radiusFrac * aspect;
            float bx = borderFrac;
            float by = borderFrac * aspect;

            // Graphite vertical gradient + brushed streaks; steel rim with top highlight bevel
            Color topFill = Color.Lerp(fill, new Color(0.22f, 0.25f, 0.30f, fill.a), 0.22f);
            Color botFill = Color.Lerp(fill, new Color(0.03f, 0.035f, 0.045f, fill.a), 0.35f);
            Color rimHi = Color.Lerp(border, new Color(0.84f, 0.87f, 0.90f, border.a), 0.55f);
            Color rimLo = Color.Lerp(border, new Color(0.20f, 0.22f, 0.25f, border.a), 0.45f);

            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                Color rowFill = Color.Lerp(botFill, topFill, v);
                // subtle brushed metal streak
                float brush = 0.04f * Mathf.Sin(v * 140f) * Mathf.Sin(v * 37f + 1.7f);
                rowFill.r = Mathf.Clamp01(rowFill.r + brush);
                rowFill.g = Mathf.Clamp01(rowFill.g + brush);
                rowFill.b = Mathf.Clamp01(rowFill.b + brush);
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    float dx = Mathf.Max(0f, Mathf.Abs(u - 0.5f) - (0.5f - rx)) / Mathf.Max(rx, 1e-4f);
                    float dy = Mathf.Max(0f, Mathf.Abs(v - 0.5f) - (0.5f - ry)) / Mathf.Max(ry, 1e-4f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    if (d > 1.0f)
                    {
                        px[y * w + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    float idx = Mathf.Max(0f, Mathf.Abs(u - 0.5f) - (0.5f - rx - bx)) / Mathf.Max(rx, 1e-4f);
                    float idy = Mathf.Max(0f, Mathf.Abs(v - 0.5f) - (0.5f - ry - by)) / Mathf.Max(ry, 1e-4f);
                    float id = Mathf.Sqrt(idx * idx + idy * idy);
                    bool edge = d >= 0.78f || id >= 0.95f ||
                                u < bx || u > 1f - bx ||
                                v < by || v > 1f - by;

                    float edgeAa = Mathf.Clamp01((1.0f - d) * 14f);
                    Color c;
                    if (edge)
                    {
                        // Top of plate gets highlight, bottom gets shade (metal bevel)
                        c = Color.Lerp(rimLo, rimHi, Mathf.Clamp01(v * 1.15f));
                        c = Color.Lerp(c, border, 0.35f);
                    }
                    else
                    {
                        c = rowFill;
                    }
                    c.a *= edgeAa;
                    px[y * w + x] = c;
                }
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(px);
            tex.Apply(true, false);
            CardTexCache[key] = tex;
            return tex;
        }
    }
}
