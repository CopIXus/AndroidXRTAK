using UnityEngine;

#if UNITY_TEXTMESHPRO || TMP_PRESENT
using TMPro;
#endif

namespace TakXr.UI
{
    /// <summary>
    /// Shared XR text factory. Prefers TextMeshPro SDF when a font asset is
    /// available (smooth at any distance); otherwise creates a high-density
    /// TextMesh with bilinear atlas filtering and an automatic widthfitter
    /// (<see cref="XrTextAutoFit"/>) so no header, button label, or list row
    /// ever bleeds outside its column or collides with neighboring UI items.
    ///
    /// World line height convention: callers pass "charSize64" — the characterSize
    /// that legacy fontSize-64 TextMesh call sites used.
    /// </summary>
    public static class XrText
    {
        public const int CrispFontSize = 256;
        const float LegacyFontSize = 64f;

        static Font _font;
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
                }
                ForceBilinear(_font);
                return _font;
            }
        }

        public static void ForceBilinear(Font font)
        {
            if (font != null && font.material != null
                && font.material.mainTexture != null)
                font.material.mainTexture.filterMode = FilterMode.Bilinear;
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
            ForceBilinear(SharedFont);
            var r = go.GetComponent<MeshRenderer>();
            if (r != null)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
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
            if (tm == null || tm.fontSize >= CrispFontSize) return;
            float worldFactor = tm.characterSize * Mathf.Max(tm.fontSize, 1);
            tm.fontSize = CrispFontSize;
            tm.characterSize = worldFactor / CrispFontSize;
            if (tm.font == null) tm.font = SharedFont;
            ForceBilinear(tm.font);
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
        }

        public void FitNow()
        {
            if (_tm == null) return;
            _lastText = _tm.text;
            if (_maxWidthM <= 0f && _maxHeightM <= 0f) return;

            _tm.characterSize = _baseCharSize;
            if (string.IsNullOrEmpty(_lastText)) return;
            if (_rend == null) _rend = _tm.GetComponent<MeshRenderer>();
            if (_rend == null) return;

            // Measure local bounds with current characterSize
            var b = _rend.localBounds;
            float w = b.size.x;
            float h = b.size.y;
            if (w <= 1e-5f && h <= 1e-5f) return;

            float scale = 1f;
            if (_maxWidthM > 0f && w > _maxWidthM)
                scale = Mathf.Min(scale, _maxWidthM / w);
            if (_maxHeightM > 0f && h > _maxHeightM)
                scale = Mathf.Min(scale, _maxHeightM / h);

            if (scale < 0.995f)
            {
                _tm.characterSize = _baseCharSize * Mathf.Max(scale, 0.42f);
            }
        }
    }
}
