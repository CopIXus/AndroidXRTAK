using UnityEngine;

#if UNITY_TEXTMESHPRO || TMP_PRESENT
using TMPro;
#endif

namespace TakXr.UI
{
    /// <summary>
    /// Shared XR text factory. Prefers TextMeshPro SDF when a font asset is
    /// available (smooth at any distance); otherwise creates a high-density
    /// TextMesh with trilinear atlas filtering and an automatic widthfitter
    /// (<see cref="XrTextAutoFit"/>) so no header, button label, or list row
    /// ever bleeds outside its column or collides with neighboring UI items.
    ///
    /// World line height convention: callers pass "charSize64" — the characterSize
    /// that legacy fontSize-64 TextMesh call sites used.
    /// </summary>
    public static class XrText
    {
        /// <summary>
        /// Raster density for TextMesh glyphs. Higher = cleaner edges under MSAA
        /// (bitmap fonts stay soft if the atlas is undersampled).
        /// </summary>
        public const int CrispFontSize = 384;
        const float LegacyFontSize = 64f;
        const string PrefetchGlyphs =
            " ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
            ".,:;!?@#$%&*()+-=_/\\|[]{}<>\"'`~°·•—–…✓×←→↑↓";

        static Font _font;
        static bool _glyphsWarmed;
#if UNITY_TEXTMESHPRO || TMP_PRESENT
        static TMP_FontAsset _tmpFont;
#endif

        public static Font SharedFont
        {
            get
            {
                if (_font == null)
                {
                    _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                            ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
                    WarmGlyphs(_font);
                }
                ForceCrispFiltering(_font);
                return _font;
            }
        }

        public static void ForceBilinear(Font font) => ForceCrispFiltering(font);

        /// <summary>
        /// Keep the dynamic font atlas on high-quality filtering so menu and CoT
        /// glyphs stay sharp after atlas rebuilds (Unity often resets to Point).
        /// </summary>
        public static void ForceCrispFiltering(Font font)
        {
            if (font == null) return;
            var mat = font.material;
            if (mat == null) return;
            var tex = mat.mainTexture;
            if (tex == null) return;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            tex.mipMapBias = -0.5f; // slightly sharper mip selection for thin strokes
            if (mat.HasProperty("_MainTex"))
                mat.mainTexture = tex;
        }

        static void WarmGlyphs(Font font)
        {
            if (font == null || _glyphsWarmed) return;
            _glyphsWarmed = true;
            try
            {
                font.RequestCharactersInTexture(PrefetchGlyphs, CrispFontSize, FontStyle.Normal);
                ForceCrispFiltering(font);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[XrText] glyph warm: " + ex.Message);
            }
        }

        /// <summary>
        /// Create crisp world-space text with optional automatic width clamping.
        /// When <paramref name="maxWidthM"/> &lt;= 0, an automatic safe width is inferred
        /// from the parent button/row background quad if one exists.
        /// </summary>
        public static TextMesh Make(string name, Transform parent, Vector3 localPos, string msg,
            float charSize64, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center,
            float maxWidthM = 0f,
            float maxHeightM = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            tm.text = msg ?? "";
            tm.fontSize = CrispFontSize;
            float baseCharSize = charSize64 * LegacyFontSize / CrispFontSize;
            tm.characterSize = baseCharSize;
            tm.anchor = anchor;
            tm.alignment = alignment;
            tm.color = color;
            tm.font = SharedFont;
            ForceCrispFiltering(SharedFont);
            // Ask the dynamic atlas to rasterize this string at crisp size before first draw.
            if (!string.IsNullOrEmpty(msg) && tm.font != null)
            {
                try { tm.font.RequestCharactersInTexture(msg, CrispFontSize, FontStyle.Normal); }
                catch { /* ignore */ }
                ForceCrispFiltering(tm.font);
            }
            var r = go.GetComponent<MeshRenderer>();
            if (r != null)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                // Slight push so transparent glyph edges composite cleanly under MSAA.
                if (r.sharedMaterial != null)
                    r.material.renderQueue = 3002;
            }

            if (maxWidthM <= 0f && parent != null)
            {
                InferBoundsFromParent(parent, localPos, anchor, out maxWidthM, out maxHeightM);
            }

            var fitter = go.AddComponent<XrTextAutoFit>();
            fitter.Configure(tm, r, baseCharSize, maxWidthM, maxHeightM);
            return tm;
        }

        static void InferBoundsFromParent(Transform parent, Vector3 localPos, TextAnchor anchor,
            out float maxW, out float maxH)
        {
            maxW = 0f;
            maxH = 0f;

            // 1. Check if parent is a button or chip with a "Bg" or "CellBg" or "ChipBg" or "BadgeBg" child
            string[] buttonBgNames = { "Bg", "CellBg", "ChipBg", "BadgeBg", "Box" };
            string pName = parent.name ?? "";
            bool isButtonLike = pName.StartsWith("Btn_", System.StringComparison.Ordinal) ||
                                pName.StartsWith("Ui_", System.StringComparison.Ordinal) ||
                                pName.StartsWith("PillBtn_", System.StringComparison.Ordinal) ||
                                pName.StartsWith("Dock_", System.StringComparison.Ordinal) ||
                                pName.StartsWith("Tile_", System.StringComparison.Ordinal) ||
                                pName.StartsWith("Opt_", System.StringComparison.Ordinal) ||
                                pName == "Chip" || pName == "Badge" || pName == "Close" ||
                                pName == "DockCollapseStrip";

            if (isButtonLike)
            {
                for (int i = 0; i < buttonBgNames.Length; i++)
                {
                    var bg = parent.Find(buttonBgNames[i]);
                    if (bg != null)
                    {
                        maxW = Mathf.Max(0.04f, bg.localScale.x * 0.90f);
                        maxH = Mathf.Max(0.025f, bg.localScale.y * 0.86f);
                        return;
                    }
                }
            }

            // 2. Check if this is a Panel Title or Header or Row text inside a panel
            Transform panelBg = parent.Find("Bg") ?? parent.Find("PanelBg") ?? parent.Find("PillBg");
            if (panelBg == null && parent.parent != null)
                panelBg = parent.parent.Find("Bg") ?? parent.parent.Find("PanelBg");

            if (panelBg != null)
            {
                float panelW = panelBg.localScale.x;
                bool isLeftAligned = anchor == TextAnchor.MiddleLeft ||
                                     anchor == TextAnchor.UpperLeft ||
                                     anchor == TextAnchor.LowerLeft;
                if (isLeftAligned)
                {
                    // Left-aligned header/row text starts at localPos.x and must stop before right-side buttons
                    float rightMargin = 0.16f;
                    float avail = (panelW * 0.5f - rightMargin) - localPos.x;
                    if (avail > 0.12f)
                        maxW = avail;
                }
                else
                {
                    maxW = panelW * 0.86f;
                }
            }
        }

        /// <summary>
        /// Create crisp world-space text with an automatic 3D drop shadow so text
        /// reads cleanly over bright terrain without needing a dark background box.
        /// </summary>
        public static TextMesh MakeWithShadow(
            string name,
            Transform parent,
            Vector3 localPos,
            string msg,
            float charSize64,
            Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center,
            float maxWidthM = 0f,
            float maxHeightM = 0f)
        {
            var tm = Make(name, parent, localPos, msg, charSize64, color, anchor, alignment, maxWidthM, maxHeightM);
            float worldH = tm.characterSize * CrispFontSize * 0.1f;

            // Soft halo (wide, faint) + hard drop shadow — bitmap TextMesh reads much
            // cleaner over passthrough / terrain when edges have contrast under MSAA.
            var haloGo = new GameObject(name + "_Halo");
            haloGo.transform.SetParent(tm.transform, false);
            haloGo.transform.localPosition = new Vector3(0f, 0f, 0.0035f);
            var htm = haloGo.AddComponent<TextMesh>();
            htm.text = tm.text;
            htm.fontSize = tm.fontSize;
            htm.characterSize = tm.characterSize * 1.04f;
            htm.anchor = tm.anchor;
            htm.alignment = tm.alignment;
            htm.color = new Color(0f, 0f, 0f, 0.55f);
            htm.font = SharedFont;
            ForceCrispFiltering(SharedFont);
            var hr = haloGo.GetComponent<MeshRenderer>();
            if (hr != null)
            {
                hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                hr.receiveShadows = false;
                if (hr.sharedMaterial != null)
                    hr.material.renderQueue = 2998;
            }

            var shadowGo = new GameObject(name + "_Shadow");
            shadowGo.transform.SetParent(tm.transform, false);
            shadowGo.transform.localPosition = new Vector3(worldH * 0.07f, -worldH * 0.08f, 0.002f);

            var stm = shadowGo.AddComponent<TextMesh>();
            stm.text = tm.text;
            stm.fontSize = tm.fontSize;
            stm.characterSize = tm.characterSize;
            stm.anchor = tm.anchor;
            stm.alignment = tm.alignment;
            stm.color = new Color(0f, 0f, 0f, 0.96f);
            stm.font = SharedFont;
            ForceCrispFiltering(SharedFont);

            var sr = shadowGo.GetComponent<MeshRenderer>();
            if (sr != null)
            {
                sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sr.receiveShadows = false;
                if (sr.sharedMaterial != null)
                    sr.material.renderQueue = 2999;
            }
            var mr = tm.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
                mr.material.renderQueue = 3002;

            var fitter = tm.GetComponent<XrTextAutoFit>();
            if (fitter != null)
                fitter.BindShadow(stm, htm);
            return tm;
        }

        public static void SetMaxBounds(TextMesh tm, float maxWidthM, float maxHeightM = 0f)
        {
            if (tm == null) return;
            var fitter = tm.GetComponent<XrTextAutoFit>();
            if (fitter == null)
            {
                fitter = tm.gameObject.AddComponent<XrTextAutoFit>();
                fitter.Configure(tm, tm.GetComponent<MeshRenderer>(), tm.characterSize, maxWidthM, maxHeightM);
            }
            else
            {
                fitter.SetLimits(maxWidthM, maxHeightM);
            }
        }

        public static void Sharpen(TextMesh tm)
        {
            if (tm == null) return;
            if (tm.fontSize < CrispFontSize)
            {
                float worldFactor = tm.characterSize * Mathf.Max(tm.fontSize, 1);
                tm.fontSize = CrispFontSize;
                tm.characterSize = worldFactor / CrispFontSize;
            }
            if (tm.font == null) tm.font = SharedFont;
            if (!string.IsNullOrEmpty(tm.text) && tm.font != null)
            {
                try { tm.font.RequestCharactersInTexture(tm.text, CrispFontSize, FontStyle.Normal); }
                catch { /* ignore */ }
            }
            ForceCrispFiltering(tm.font);
        }
    }

    /// <summary>
    /// Automatically shrinks <see cref="TextMesh.characterSize"/> whenever the rendered text
    /// exceeds its allotted width or height budget, preventing menu headers, buttons, and rows
    /// from bleeding into adjacent controls.
    /// </summary>
    public sealed class XrTextAutoFit : MonoBehaviour
    {
        TextMesh _tm;
        TextMesh _shadowTm;
        TextMesh _haloTm;
        MeshRenderer _rend;
        float _baseCharSize;
        float _maxWidthM;
        float _maxHeightM;
        string _lastText;

        public void Configure(TextMesh tm, MeshRenderer rend, float baseCharSize, float maxWidthM, float maxHeightM)
        {
            _tm = tm;
            _rend = rend;
            _baseCharSize = baseCharSize;
            _maxWidthM = maxWidthM;
            _maxHeightM = maxHeightM;
            _lastText = null;
            FitNow();
        }

        public void BindShadow(TextMesh shadow, TextMesh halo = null)
        {
            _shadowTm = shadow;
            _haloTm = halo;
            SyncShadow();
        }

        public void SetLimits(float maxWidthM, float maxHeightM = 0f)
        {
            _maxWidthM = maxWidthM;
            _maxHeightM = maxHeightM;
            _lastText = null;
            FitNow();
        }

        void LateUpdate()
        {
            if (_tm == null) return;
            if (_tm.text != _lastText)
                FitNow();
            else if (_shadowTm != null && _shadowTm.text != _tm.text)
                SyncShadow();
        }

        public void FitNow()
        {
            if (_tm == null) return;
            _lastText = _tm.text;
            XrText.ForceCrispFiltering(_tm.font);
            if (!string.IsNullOrEmpty(_lastText) && _tm.font != null)
            {
                try { _tm.font.RequestCharactersInTexture(_lastText, XrText.CrispFontSize, FontStyle.Normal); }
                catch { /* ignore */ }
                XrText.ForceCrispFiltering(_tm.font);
            }

            if (_maxWidthM <= 0f && _maxHeightM <= 0f)
            {
                SyncShadow();
                return;
            }

            _tm.characterSize = _baseCharSize;
            if (string.IsNullOrEmpty(_lastText))
            {
                SyncShadow();
                return;
            }
            if (_rend == null) _rend = _tm.GetComponent<MeshRenderer>();
            if (_rend == null)
            {
                SyncShadow();
                return;
            }

            // Measure local bounds with current characterSize
            var b = _rend.localBounds;
            float w = b.size.x;
            float h = b.size.y;
            if (w <= 1e-5f && h <= 1e-5f)
            {
                SyncShadow();
                return;
            }

            float scale = 1f;
            if (_maxWidthM > 0f && w > _maxWidthM)
                scale = Mathf.Min(scale, _maxWidthM / w);
            if (_maxHeightM > 0f && h > _maxHeightM)
                scale = Mathf.Min(scale, _maxHeightM / h);

            if (scale < 0.995f)
            {
                _tm.characterSize = _baseCharSize * Mathf.Max(scale, 0.42f);
            }
            SyncShadow();
        }

        void SyncShadow()
        {
            if (_tm == null) return;
            float worldH = _tm.characterSize * _tm.fontSize * 0.1f;
            if (_shadowTm != null)
            {
                _shadowTm.text = _tm.text;
                _shadowTm.fontSize = _tm.fontSize;
                _shadowTm.characterSize = _tm.characterSize;
                _shadowTm.transform.localPosition = new Vector3(worldH * 0.065f, -worldH * 0.075f, 0.002f);
            }
            if (_haloTm != null)
            {
                _haloTm.text = _tm.text;
                _haloTm.fontSize = _tm.fontSize;
                _haloTm.characterSize = _tm.characterSize * 1.04f;
                _haloTm.transform.localPosition = new Vector3(0f, 0f, 0.0035f);
            }
        }
    }
}
