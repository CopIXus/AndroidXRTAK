using UnityEngine;

#if UNITY_TEXTMESHPRO || TMP_PRESENT
using TMPro;
#endif

namespace TakXr.UI
{
    /// <summary>
    /// Shared XR text factory. Prefers TextMeshPro SDF when a font asset is
    /// available (smooth at any distance); otherwise creates a high-density
    /// TextMesh with bilinear atlas filtering.
    ///
    /// World line height convention: callers pass "charSize64" — the characterSize
    /// that legacy fontSize-64 TextMesh call sites used. TMP conversion uses
    /// fontSize ≈ charSize64 * 640 so world size stays comparable.
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
        /// Create crisp world-space text. Returns a TextMesh for API compatibility
        /// with existing chrome (status, compass, panels). When TMP is available the
        /// TextMesh is still created as a lightweight driver; prefer SetText helpers.
        /// </summary>
        public static TextMesh Make(string name, Transform parent, Vector3 localPos, string msg,
            float charSize64, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center)
        {
#if UNITY_TEXTMESHPRO || TMP_PRESENT
            if (TryMakeTmp(name, parent, localPos, msg, charSize64, color, anchor, alignment, out var tmDriver))
                return tmDriver;
#endif
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            tm.text = msg;
            tm.fontSize = CrispFontSize;
            tm.characterSize = charSize64 * LegacyFontSize / CrispFontSize;
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
            return tm;
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

#if UNITY_TEXTMESHPRO || TMP_PRESENT
        static bool TryMakeTmp(string name, Transform parent, Vector3 localPos, string msg,
            float charSize64, Color color, TextAnchor anchor, TextAlignment alignment,
            out TextMesh driver)
        {
            driver = null;
            if (_tmpFont == null)
            {
                _tmpFont = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF")
                           ?? Resources.Load<TMP_FontAsset>("LiberationSans SDF");
                if (_tmpFont == null && TMP_Settings.instance != null)
                    _tmpFont = TMP_Settings.defaultFontAsset;
            }
            if (_tmpFont == null) return false;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = _tmpFont;
            tmp.text = msg ?? "";
            tmp.color = color;
            tmp.fontSize = Mathf.Max(8f, charSize64 * 640f);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.alignment = ToTmpAlign(anchor, alignment);
            tmp.rectTransform.sizeDelta = new Vector2(2f, 0.5f);
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            // Invisible TextMesh driver so call sites that assign .text keep working
            // via XrTmpBridge.
            var bridgeGo = new GameObject("TmBridge");
            bridgeGo.transform.SetParent(go.transform, false);
            driver = bridgeGo.AddComponent<TextMesh>();
            driver.text = msg ?? "";
            driver.characterSize = 0.0001f;
            driver.fontSize = 8;
            driver.color = new Color(0, 0, 0, 0);
            var bridge = bridgeGo.AddComponent<XrTmpBridge>();
            bridge.Bind(tmp, driver);
            return true;
        }

        static TextAlignmentOptions ToTmpAlign(TextAnchor anchor, TextAlignment align)
        {
            bool left = anchor == TextAnchor.UpperLeft || anchor == TextAnchor.MiddleLeft
                        || anchor == TextAnchor.LowerLeft || align == TextAlignment.Left;
            bool right = anchor == TextAnchor.UpperRight || anchor == TextAnchor.MiddleRight
                         || anchor == TextAnchor.LowerRight || align == TextAlignment.Right;
            if (left) return TextAlignmentOptions.MidlineLeft;
            if (right) return TextAlignmentOptions.MidlineRight;
            return TextAlignmentOptions.Center;
        }
#endif
    }

#if UNITY_TEXTMESHPRO || TMP_PRESENT
    /// <summary>Keeps a TextMeshPro label in sync with a TextMesh .text assignment.</summary>
    public sealed class XrTmpBridge : MonoBehaviour
    {
        TextMeshPro _tmp;
        TextMesh _driver;
        string _last;

        public void Bind(TextMeshPro tmp, TextMesh driver)
        {
            _tmp = tmp;
            _driver = driver;
            _last = driver != null ? driver.text : null;
        }

        void LateUpdate()
        {
            if (_tmp == null || _driver == null) return;
            if (_driver.text == _last) return;
            _last = _driver.text;
            _tmp.text = _last ?? "";
            _tmp.color = _driver.color;
        }
    }
#endif
}
