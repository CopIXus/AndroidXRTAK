using System.Collections.Generic;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK toolbar icons. Prefers the REAL ATAK-CIV drawables vendored under
    /// Assets/Resources/AtakIcons (downloaded from the archived
    /// deptofdefense/AndroidTacticalAssaultKit-CIV repo, drawable-xxhdpi — warm
    /// off-white glyphs on transparency, tintable). Falls back to the procedural
    /// web-parity glyphs when a name has no vendored file or the load fails.
    /// </summary>
    public static class AtakToolbarIcons
    {
        static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        // Our icon name -> vendored ATAK-CIV drawable base name in Resources/AtakIcons.
        // "size" has no ATAK equivalent and stays procedural.
        // hamburger uses nav_menu_closed (three bars). nav_menu_opened is the X ATAK
        // shows when the overflow menu is already open — do not use it as the default.
        static readonly Dictionary<string, string> AtakFiles = new Dictionary<string, string>
        {
            { "route", "nav_routes" },
            { "channels", "nav_channels" },
            { "layers", "nav_overlay_manager" },
            { "map", "nav_maps" },
            { "point", "nav_point_dropper" },
            { "pointadd", "nav_point_dropper" },
            { "close", "nav_redx" },
            { "hamburger", "nav_menu_closed" },
            { "menuopen", "nav_menu_opened" },
            { "draw", "nav_draw" },
            { "polygon", "nav_draw_shape" },
            { "circle", "nav_draw_circle" },
            { "package", "nav_data_package" },
            { "datasync", "nav_package" },
            { "locate", "nav_update_location" },
            { "north", "true_north" },
            { "orientation", "nav_orientation" },
            { "follow", "nav_firstperson" },
            { "firstperson", "nav_firstperson" },
            { "settings", "nav_settings" },
            { "server", "nav_server" },
            { "delete", "nav_delete" },
            { "clear", "nav_delete" },
            { "details", "nav_info" },
            { "video", "nav_video" },
            { "rb", "nav_rab" },
            { "range", "nav_rab" },
            { "zoom", "nav_zoom" },
            { "more", "nav_more" },
            // Full ATAK Tools grid
            { "alert", "nav_alert" },
            { "brightness", "nav_brightness" },
            { "casevac", "nav_casevac" },
            { "chat", "nav_chatnext" },
            { "contacts", "nav_contacts" },
            { "pointer", "nav_fire_tools" },
            { "spi", "nav_spi" },
            { "elevation", "nav_elevation" },
            { "gallery", "nav_gallery" },
            { "geofence", "nav_geofence" },
            { "goto", "nav_goto" },
            { "import", "nav_import" },
            { "lasso", "nav_lasso" },
            { "plugins", "nav_plugins" },
            { "quicknav", "nav_quick_nav" },
            { "quickpic", "nav_quick_pic" },
            { "radio", "nav_radio" },
            { "resection", "nav_resection" },
            { "rubbersheet", "nav_rubber_sheet" },
            { "tracks", "nav_track_history" },
            { "quit", "nav_power" },
            { "pageup", "nav_more" },
            { "pagedown", "nav_more" },
        };

        public static Texture2D Get(string name)
        {
            if (Cache.TryGetValue(name, out var tex) && tex != null) return tex;
            tex = LoadAtakDrawable(name) ?? Render(name);
            Cache[name] = tex;
            return tex;
        }

        static Texture2D LoadAtakDrawable(string name)
        {
            if (!AtakFiles.TryGetValue(name, out var file)) return null;
            var tex = Resources.Load<Texture2D>("AtakIcons/" + file);
            if (tex == null) return null;
            // Sampler settings are writable even on non-readable imported textures.
            // Default import generates mipmaps; trilinear + aniso keeps the glyphs
            // clean at both toolbar minification and radial-menu magnification.
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            return tex;
        }

        static Texture2D Render(string name)
        {
            // Web-parity: bright white glyphs on dark button plates, thick strokes.
            // 192px + mipmaps so the strip stays crisp when minified at ~2.3 m.
            const int s = 192;
            var px = new Color32[s * s];
            Clear(px, s);
            var ink = new Color32(255, 255, 255, 255);
            var accent = new Color32(160, 220, 255, 255);

            switch (name)
            {
                case "globe":
                    Circle(px, s, 0.5f, 0.5f, 0.38f, ink, false);
                    Ellipse(px, s, 0.5f, 0.5f, 0.16f, 0.38f, ink);
                    HLine(px, s, 0.14f, 0.86f, 0.5f, ink);
                    break;
                case "locate":
                    Circle(px, s, 0.5f, 0.5f, 0.28f, ink, false);
                    Circle(px, s, 0.5f, 0.5f, 0.08f, ink, true);
                    VLine(px, s, 0.5f, 0.06f, 0.22f, ink);
                    VLine(px, s, 0.5f, 0.78f, 0.94f, ink);
                    HLine(px, s, 0.06f, 0.22f, 0.5f, ink);
                    HLine(px, s, 0.78f, 0.94f, 0.5f, ink);
                    break;
                case "fit":
                    Bracket(px, s, ink);
                    Circle(px, s, 0.5f, 0.5f, 0.08f, ink, true);
                    break;
                case "north":
                    Circle(px, s, 0.5f, 0.5f, 0.36f, ink, false);
                    // N arrow up
                    VLine(px, s, 0.5f, 0.12f, 0.42f, accent);
                    FillTri(px, s, 0.5f, 0.12f, 0.38f, 0.28f, 0.62f, 0.28f, accent);
                    break;
                case "server":
                    Rect(px, s, 0.18f, 0.18f, 0.82f, 0.42f, ink, false);
                    Rect(px, s, 0.18f, 0.55f, 0.82f, 0.82f, ink, false);
                    Circle(px, s, 0.32f, 0.30f, 0.05f, ink, true);
                    Circle(px, s, 0.32f, 0.68f, 0.05f, ink, true);
                    break;
                case "xr":
                    Rect(px, s, 0.12f, 0.30f, 0.88f, 0.70f, ink, false);
                    Circle(px, s, 0.34f, 0.5f, 0.08f, ink, true);
                    Circle(px, s, 0.66f, 0.5f, 0.08f, ink, true);
                    break;
                case "point":
                    // Pin
                    Circle(px, s, 0.5f, 0.38f, 0.18f, ink, true);
                    FillTri(px, s, 0.5f, 0.88f, 0.32f, 0.48f, 0.68f, 0.48f, ink);
                    Circle(px, s, 0.5f, 0.38f, 0.07f, new Color32(16, 22, 30, 255), true);
                    break;
                case "route":
                    // ATAK node-link route: three ringed nodes joined by diagonals.
                    Circle(px, s, 0.20f, 0.78f, 0.11f, ink, false);
                    Circle(px, s, 0.50f, 0.48f, 0.11f, ink, false);
                    Circle(px, s, 0.80f, 0.18f, 0.11f, ink, false);
                    Circle(px, s, 0.20f, 0.78f, 0.035f, ink, true);
                    Circle(px, s, 0.50f, 0.48f, 0.035f, ink, true);
                    Circle(px, s, 0.80f, 0.18f, 0.035f, ink, true);
                    Line(px, s, 0.278f, 0.702f, 0.422f, 0.558f, ink);
                    Line(px, s, 0.578f, 0.402f, 0.722f, 0.258f, ink);
                    break;
                case "polygon":
                    // Pentagon-ish
                    Line(px, s, 0.5f, 0.14f, 0.84f, 0.38f, ink);
                    Line(px, s, 0.84f, 0.38f, 0.72f, 0.82f, ink);
                    Line(px, s, 0.72f, 0.82f, 0.28f, 0.82f, ink);
                    Line(px, s, 0.28f, 0.82f, 0.16f, 0.38f, ink);
                    Line(px, s, 0.16f, 0.38f, 0.5f, 0.14f, ink);
                    break;
                case "circle":
                    Circle(px, s, 0.5f, 0.5f, 0.34f, ink, false);
                    Circle(px, s, 0.5f, 0.5f, 0.06f, ink, true);
                    HLine(px, s, 0.5f, 0.84f, 0.5f, ink);
                    break;
                case "channels":
                    // Stacked layers
                    FillTri(px, s, 0.5f, 0.18f, 0.18f, 0.40f, 0.82f, 0.40f, ink);
                    Line(px, s, 0.18f, 0.55f, 0.5f, 0.72f, ink);
                    Line(px, s, 0.5f, 0.72f, 0.82f, 0.55f, ink);
                    Line(px, s, 0.18f, 0.72f, 0.5f, 0.88f, ink);
                    Line(px, s, 0.5f, 0.88f, 0.82f, 0.72f, ink);
                    break;
                case "package":
                    Rect(px, s, 0.22f, 0.28f, 0.78f, 0.78f, ink, false);
                    Line(px, s, 0.22f, 0.28f, 0.5f, 0.14f, ink);
                    Line(px, s, 0.78f, 0.28f, 0.5f, 0.14f, ink);
                    Line(px, s, 0.5f, 0.14f, 0.5f, 0.50f, ink);
                    break;
                case "datasync":
                    // Sync arrows (approx arcs)
                    Circle(px, s, 0.5f, 0.5f, 0.30f, ink, false);
                    FillTri(px, s, 0.22f, 0.22f, 0.22f, 0.40f, 0.40f, 0.30f, ink);
                    FillTri(px, s, 0.78f, 0.78f, 0.78f, 0.60f, 0.60f, 0.70f, ink);
                    break;
                case "views":
                    Rect(px, s, 0.14f, 0.22f, 0.86f, 0.70f, ink, false);
                    FillTri(px, s, 0.40f, 0.35f, 0.40f, 0.58f, 0.62f, 0.46f, ink);
                    HLine(px, s, 0.35f, 0.65f, 0.82f, ink);
                    break;
                case "settings":
                    Circle(px, s, 0.5f, 0.5f, 0.14f, ink, false);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI * 2f / 8f;
                        float x0 = 0.5f + Mathf.Cos(a) * 0.22f;
                        float y0 = 0.5f + Mathf.Sin(a) * 0.22f;
                        float x1 = 0.5f + Mathf.Cos(a) * 0.36f;
                        float y1 = 0.5f + Mathf.Sin(a) * 0.36f;
                        Line(px, s, x0, y0, x1, y1, ink);
                    }
                    break;
                case "follow":
                    Circle(px, s, 0.5f, 0.5f, 0.30f, ink, false);
                    Circle(px, s, 0.5f, 0.5f, 0.08f, ink, true);
                    VLine(px, s, 0.5f, 0.08f, 0.22f, ink);
                    VLine(px, s, 0.5f, 0.78f, 0.92f, ink);
                    HLine(px, s, 0.08f, 0.22f, 0.5f, ink);
                    HLine(px, s, 0.78f, 0.92f, 0.5f, ink);
                    break;
                case "size":
                    // Two squares different sizes
                    Rect(px, s, 0.18f, 0.45f, 0.45f, 0.78f, ink, false);
                    Rect(px, s, 0.42f, 0.18f, 0.82f, 0.58f, ink, false);
                    break;
                case "hamburger":
                    // ATAK weight: bold filled bars rather than thin strokes.
                    Rect(px, s, 0.16f, 0.215f, 0.84f, 0.305f, ink, true);
                    Rect(px, s, 0.16f, 0.455f, 0.84f, 0.545f, ink, true);
                    Rect(px, s, 0.16f, 0.695f, 0.84f, 0.785f, ink, true);
                    break;
                case "close":
                    Line(px, s, 0.20f, 0.20f, 0.80f, 0.80f, ink);
                    Line(px, s, 0.20f, 0.80f, 0.80f, 0.20f, ink);
                    break;
                case "map":
                    // Tri-fold map like the ATAK top bar.
                    Line(px, s, 0.12f, 0.22f, 0.12f, 0.78f, ink);
                    Line(px, s, 0.88f, 0.22f, 0.88f, 0.78f, ink);
                    Line(px, s, 0.12f, 0.22f, 0.38f, 0.14f, ink);
                    Line(px, s, 0.38f, 0.14f, 0.62f, 0.22f, ink);
                    Line(px, s, 0.62f, 0.22f, 0.88f, 0.14f, ink);
                    Line(px, s, 0.12f, 0.78f, 0.38f, 0.86f, ink);
                    Line(px, s, 0.38f, 0.86f, 0.62f, 0.78f, ink);
                    Line(px, s, 0.62f, 0.78f, 0.88f, 0.86f, ink);
                    Line(px, s, 0.38f, 0.14f, 0.38f, 0.86f, ink);
                    Line(px, s, 0.62f, 0.22f, 0.62f, 0.78f, ink);
                    break;
                case "pointadd":
                    // ATAK point-drop: solid pin with dark eye + bold plus at lower right.
                    Circle(px, s, 0.40f, 0.34f, 0.18f, ink, true);
                    FillTri(px, s, 0.40f, 0.86f, 0.255f, 0.44f, 0.545f, 0.44f, ink);
                    Circle(px, s, 0.40f, 0.34f, 0.065f, new Color32(16, 22, 30, 255), true);
                    HLine(px, s, 0.64f, 0.92f, 0.70f, ink);
                    VLine(px, s, 0.78f, 0.56f, 0.84f, ink);
                    break;
                case "details":
                    // "i" in a circle.
                    Circle(px, s, 0.5f, 0.5f, 0.36f, ink, false);
                    Circle(px, s, 0.5f, 0.31f, 0.055f, ink, true);
                    VLine(px, s, 0.5f, 0.43f, 0.68f, ink);
                    break;
                case "video":
                    // Camcorder: body + lens trapezoid (as tri) pointing right.
                    Line(px, s, 0.10f, 0.32f, 0.60f, 0.32f, ink);
                    Line(px, s, 0.10f, 0.68f, 0.60f, 0.68f, ink);
                    Line(px, s, 0.10f, 0.32f, 0.10f, 0.68f, ink);
                    Line(px, s, 0.60f, 0.32f, 0.60f, 0.68f, ink);
                    FillTri(px, s, 0.64f, 0.50f, 0.90f, 0.32f, 0.90f, 0.68f, ink);
                    Circle(px, s, 0.32f, 0.50f, 0.07f, ink, false);
                    break;
                case "rb":
                    // Range & bearing: double-headed diagonal ruler with ticks.
                    Line(px, s, 0.24f, 0.76f, 0.76f, 0.24f, ink);
                    FillTri(px, s, 0.86f, 0.14f, 0.80f, 0.33f, 0.67f, 0.20f, ink);
                    FillTri(px, s, 0.14f, 0.86f, 0.20f, 0.67f, 0.33f, 0.80f, ink);
                    Line(px, s, 0.36f, 0.56f, 0.44f, 0.64f, ink);
                    Line(px, s, 0.56f, 0.36f, 0.64f, 0.44f, ink);
                    break;
                case "delete":
                    // Trash can: lid + handle + tapered body + ribs.
                    HLine(px, s, 0.22f, 0.78f, 0.26f, ink);
                    Line(px, s, 0.42f, 0.26f, 0.44f, 0.16f, ink);
                    Line(px, s, 0.58f, 0.26f, 0.56f, 0.16f, ink);
                    HLine(px, s, 0.44f, 0.56f, 0.16f, ink);
                    Line(px, s, 0.28f, 0.34f, 0.34f, 0.86f, ink);
                    Line(px, s, 0.72f, 0.34f, 0.66f, 0.86f, ink);
                    HLine(px, s, 0.34f, 0.66f, 0.86f, ink);
                    HLine(px, s, 0.28f, 0.72f, 0.34f, ink);
                    VLine(px, s, 0.44f, 0.46f, 0.74f, ink);
                    VLine(px, s, 0.56f, 0.46f, 0.74f, ink);
                    break;
                default:
                    Circle(px, s, 0.5f, 0.5f, 0.2f, ink, true);
                    break;
            }

            Outline(px, s);

            var t = new Texture2D(s, s, TextureFormat.RGBA32, true);
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 4;
            t.SetPixels32(px);
            t.Apply(true, false);
            return t;
        }

        /// <summary>
        /// Dark halo around every glyph stroke so icons stay readable when floating
        /// directly over bright map imagery (ATAK-style, no button plates).
        /// </summary>
        static void Outline(Color32[] px, int s)
        {
            const int R = 3;
            var halo = new Color32(0, 0, 0, 190);
            var src = (Color32[])px.Clone();
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                if (src[y * s + x].a != 0) continue;
                bool near = false;
                for (int dy = -R; dy <= R && !near; dy++)
                for (int dx = -R; dx <= R && !near; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= s || ny >= s) continue;
                    if (src[ny * s + nx].a > 128) near = true;
                }
                if (near) px[y * s + x] = halo;
            }
        }

        static void Clear(Color32[] px, int s)
        {
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
        }

        static void Set(Color32[] px, int s, int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= s || y >= s) return;
            px[y * s + x] = c;
        }

        static void Circle(Color32[] px, int s, float cx, float cy, float r, Color32 c, bool fill)
        {
            int x0 = Mathf.FloorToInt((cx - r) * s) - 1;
            int x1 = Mathf.CeilToInt((cx + r) * s) + 1;
            int y0 = Mathf.FloorToInt((cy - r) * s) - 1;
            int y1 = Mathf.CeilToInt((cy + r) * s) + 1;
            float r2 = r * r;
            float rin = (r - 0.06f) * (r - 0.06f);
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float u = (x + 0.5f) / s - cx;
                float v = (y + 0.5f) / s - cy;
                float d = u * u + v * v;
                if (fill) { if (d <= r2) Set(px, s, x, y, c); }
                else if (d <= r2 && d >= rin) Set(px, s, x, y, c);
            }
        }

        static void Ellipse(Color32[] px, int s, float cx, float cy, float rx, float ry, Color32 c)
        {
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = ((x + 0.5f) / s - cx) / rx;
                float v = ((y + 0.5f) / s - cy) / ry;
                float d = u * u + v * v;
                if (d <= 1f && d >= 0.85f) Set(px, s, x, y, c);
            }
        }

        static void Rect(Color32[] px, int s, float x0, float y0, float x1, float y1, Color32 c, bool fill)
        {
            int ix0 = Mathf.FloorToInt(x0 * s);
            int iy0 = Mathf.FloorToInt(y0 * s);
            int ix1 = Mathf.CeilToInt(x1 * s);
            int iy1 = Mathf.CeilToInt(y1 * s);
            for (int y = iy0; y < iy1; y++)
            for (int x = ix0; x < ix1; x++)
            {
                bool edge = x == ix0 || y == iy0 || x == ix1 - 1 || y == iy1 - 1;
                if (fill || edge) Set(px, s, x, y, c);
            }
        }

        // Stroke half-width in pixels (192px canvas → ~7px strokes survive minification).
        const int Stroke = 3;

        static void HLine(Color32[] px, int s, float x0, float x1, float y, Color32 c)
        {
            int iy = Mathf.RoundToInt(y * s);
            int a = Mathf.FloorToInt(Mathf.Min(x0, x1) * s);
            int b = Mathf.CeilToInt(Mathf.Max(x0, x1) * s);
            for (int x = a; x <= b; x++)
            for (int dy = -Stroke; dy <= Stroke; dy++)
                Set(px, s, x, iy + dy, c);
        }

        static void VLine(Color32[] px, int s, float x, float y0, float y1, Color32 c)
        {
            int ix = Mathf.RoundToInt(x * s);
            int a = Mathf.FloorToInt(Mathf.Min(y0, y1) * s);
            int b = Mathf.CeilToInt(Mathf.Max(y0, y1) * s);
            for (int y = a; y <= b; y++)
            for (int dx = -Stroke; dx <= Stroke; dx++)
                Set(px, s, ix + dx, y, c);
        }

        static void Line(Color32[] px, int s, float x0, float y0, float x1, float y1, Color32 c)
        {
            int n = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) * s) + 2;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t) * s);
                int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t) * s);
                for (int dx = -Stroke; dx <= Stroke; dx++)
                for (int dy = -Stroke; dy <= Stroke; dy++)
                    Set(px, s, x + dx, y + dy, c);
            }
        }

        static void FillTri(Color32[] px, int s, float x0, float y0, float x1, float y1, float x2, float y2, Color32 c)
        {
            int minX = Mathf.FloorToInt(Mathf.Min(x0, Mathf.Min(x1, x2)) * s);
            int maxX = Mathf.CeilToInt(Mathf.Max(x0, Mathf.Max(x1, x2)) * s);
            int minY = Mathf.FloorToInt(Mathf.Min(y0, Mathf.Min(y1, y2)) * s);
            int maxY = Mathf.CeilToInt(Mathf.Max(y0, Mathf.Max(y1, y2)) * s);
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float u = (x + 0.5f) / s;
                float v = (y + 0.5f) / s;
                if (InsideTri(u, v, x0, y0, x1, y1, x2, y2)) Set(px, s, x, y, c);
            }
        }

        static bool InsideTri(float px, float py, float x0, float y0, float x1, float y1, float x2, float y2)
        {
            float d1 = Sign(px, py, x0, y0, x1, y1);
            float d2 = Sign(px, py, x1, y1, x2, y2);
            float d3 = Sign(px, py, x2, y2, x0, y0);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        static float Sign(float px, float py, float x0, float y0, float x1, float y1) =>
            (px - x1) * (y0 - y1) - (x0 - x1) * (py - y1);

        static void Bracket(Color32[] px, int s, Color32 c)
        {
            // Four corner brackets
            HLine(px, s, 0.14f, 0.32f, 0.14f, c); VLine(px, s, 0.14f, 0.14f, 0.32f, c);
            HLine(px, s, 0.68f, 0.86f, 0.14f, c); VLine(px, s, 0.86f, 0.14f, 0.32f, c);
            HLine(px, s, 0.14f, 0.32f, 0.86f, c); VLine(px, s, 0.14f, 0.68f, 0.86f, c);
            HLine(px, s, 0.68f, 0.86f, 0.86f, c); VLine(px, s, 0.86f, 0.68f, 0.86f, c);
        }
    }
}
