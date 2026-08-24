using UnityEngine;
using UnityEngine.UI;

namespace TakXr.UI
{
    /// <summary>
    /// Always-on Screen Space Overlay HUD so phones/XR show something even if XR/Cesium fail.
    /// OnGUI is unreliable under OpenXR stereo; use uGUI instead.
    /// </summary>
    public class BootHud : MonoBehaviour
    {
        Text _status;
        Text _detail;
        Image _panel;

        public static BootHud Create(Transform parent = null)
        {
            var go = new GameObject("BootHud");
            if (parent != null) go.transform.SetParent(parent, false);
            DontDestroyOnLoad(go);
            return go.AddComponent<BootHud>();
        }

        void Awake()
        {
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            canvasGo.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(canvasGo.transform, false);
            _panel = panelGo.AddComponent<Image>();
            _panel.color = new Color(0.05f, 0.12f, 0.18f, 0.72f);
            var prt = panelGo.GetComponent<RectTransform>();
            // Compact top-left — leave most of the viewport for the 3D map.
            prt.anchorMin = new Vector2(0.02f, 0.72f);
            prt.anchorMax = new Vector2(0.42f, 0.98f);
            prt.offsetMin = Vector2.zero;
            prt.offsetMax = Vector2.zero;

            _status = MakeLabel(panelGo.transform, "Status", 28, FontStyle.Bold,
                new Vector2(0.04f, 0.55f), new Vector2(0.96f, 0.95f));
            _detail = MakeLabel(panelGo.transform, "Detail", 20, FontStyle.Normal,
                new Vector2(0.04f, 0.05f), new Vector2(0.96f, 0.55f));

            SetStatus("TAKXR", "Starting…");
        }

        static Text MakeLabel(Transform parent, string name, int size, FontStyle style, Vector2 amin, Vector2 amax)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                     ?? Resources.GetBuiltinResource<Font>("Arial.ttf")
                     ?? Font.CreateDynamicFontFromOSFont("sans-serif", size);
            t.fontSize = size;
            t.fontStyle = style;
            t.color = Color.white;
            t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = t.rectTransform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return t;
        }

        public void SetStatus(string title, string detail)
        {
            if (_status != null) _status.text = title ?? "";
            if (_detail != null) _detail.text = detail ?? "";
        }

        public void SetPanelVisible(bool visible)
        {
            if (_panel != null) _panel.gameObject.SetActive(visible);
        }
    }
}
