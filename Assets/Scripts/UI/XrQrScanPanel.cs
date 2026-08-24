using System;
using System.Collections.Generic;
using TakXr.Xr;
using UnityEngine;
using ZXing;
using ZXing.Common;

namespace TakXr.UI
{
    /// <summary>
    /// In-headset QR scanner: WebCamTexture + ZXing. Falls back to a message
    /// telling the user to type the host if the camera is unavailable.
    /// </summary>
    [DefaultExecutionOrder(70)]
    public class XrQrScanPanel : MonoBehaviour
    {
        const float PanelW = 0.92f;
        const float PanelH = 0.78f;

        Transform _cam;
        Action<string> _onDone;
        Transform _root;
        TextMesh _hint;
        Renderer _previewRend;
        WebCamTexture _webcam;
        BarcodeReaderGeneric _reader;
        float _nextDecode;
        bool _busy;
        readonly List<(Transform root, Action onClick)> _hits = new List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        float _nextClick;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrQrScanPanel Create()
        {
            var go = new GameObject("XrQrScanPanel");
            return go.AddComponent<XrQrScanPanel>();
        }

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.012f), new Vector2(PanelW, PanelH),
                new Color(0.04f, 0.08f, 0.12f, 0.97f), 3010);

            Text("Title", _root, new Vector3(0f, PanelH / 2f - 0.05f, -0.01f),
                "SCAN TAK QR", 0.011f, new Color(0.85f, 0.95f, 1f, 1f));

            var preview = GameObject.CreatePrimitive(PrimitiveType.Quad);
            preview.name = "Preview";
            preview.transform.SetParent(_root, false);
            preview.transform.localPosition = new Vector3(0f, 0.04f, -0.006f);
            preview.transform.localScale = new Vector3(0.72f, 0.42f, 1f);
            UnityEngine.Object.Destroy(preview.GetComponent<Collider>());
            _previewRend = preview.GetComponent<Renderer>();
            var sh = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            _previewRend.sharedMaterial = new Material(sh);
            _previewRend.sharedMaterial.renderQueue = 3015;

            _hint = Text("Hint", _root, new Vector3(0f, -0.26f, -0.01f),
                "Point camera at an infraTAK / ATAK QR", 0.0058f,
                new Color(0.75f, 0.88f, 1f, 0.95f));

            AddBtn("Cancel", new Vector3(0f, -PanelH / 2f + 0.07f, -0.01f),
                new Vector2(0.28f, 0.05f), "Cancel", () => Finish(null));

            _reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                    TryHarder = true,
                    TryInverted = true,
                }
            };
        }

        public void Open(Transform cam, Action<string> onDone)
        {
            _cam = cam;
            _onDone = onDone;
            _busy = false;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            if (_hint != null) _hint.text = "Opening camera…";
            StartCamera();
        }

        public void Hide()
        {
            StopCamera();
            _root.gameObject.SetActive(false);
        }

        void PlaceFacing()
        {
            if (_cam == null) return;
            var camPos = _cam.position;
            var flat = _cam.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            _root.position = camPos + flat * 1.55f + Vector3.up * 0.02f;
            _root.rotation = XrUiFacing.RotationFacingUser(_root.position, camPos);
        }

        void StartCamera()
        {
            StopCamera();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QrScan] permission: " + ex.Message);
            }
#endif
            try
            {
                var devices = WebCamTexture.devices;
                if (devices == null || devices.Length == 0)
                {
                    FailCamera("No camera — type host instead");
                    return;
                }
                int idx = 0;
                for (int i = 0; i < devices.Length; i++)
                {
                    if (!devices[i].isFrontFacing) { idx = i; break; }
                }
                _webcam = new WebCamTexture(devices[idx].name, 640, 480, 15);
                _webcam.Play();
                if (_previewRend != null && _previewRend.sharedMaterial != null)
                    _previewRend.sharedMaterial.mainTexture = _webcam;
                if (_hint != null) _hint.text = "Point camera at an infraTAK / ATAK QR";
            }
            catch (Exception ex)
            {
                FailCamera("Camera unavailable — type host (" + Trunc(ex.Message, 40) + ")");
            }
        }

        void FailCamera(string msg)
        {
            if (_hint != null) _hint.text = msg;
        }

        void StopCamera()
        {
            if (_webcam == null) return;
            try { _webcam.Stop(); } catch { /* ignore */ }
            _webcam = null;
            if (_previewRend != null && _previewRend.sharedMaterial != null)
                _previewRend.sharedMaterial.mainTexture = null;
        }

        void Finish(string raw)
        {
            if (_busy) return;
            _busy = true;
            StopCamera();
            Hide();
            _onDone?.Invoke(raw);
        }

        void Update()
        {
            if (!IsVisible) return;
            PlaceFacing();

            if (_webcam != null && _webcam.isPlaying && _webcam.didUpdateThisFrame
                && Time.unscaledTime >= _nextDecode)
            {
                _nextDecode = Time.unscaledTime + 0.18f;
                TryDecode();
            }

            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;
                if (!rising || Time.unscaledTime < _nextClick) continue;
                var hits = Physics.RaycastAll(origin, fwd, 8f, ~0, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var hit in hits)
                {
                    foreach (var (root, onClick) in _hits)
                    {
                        if (root == null) continue;
                        if (hit.transform == root || hit.transform.IsChildOf(root))
                        {
                            onClick?.Invoke();
                            _nextClick = Time.unscaledTime + 0.3f;
                            return;
                        }
                    }
                }
            }
        }

        void TryDecode()
        {
            if (_webcam == null || _reader == null) return;
            int w = _webcam.width;
            int h = _webcam.height;
            if (w < 16 || h < 16) return;
            Color32[] px;
            try { px = _webcam.GetPixels32(); }
            catch { return; }
            if (px == null || px.Length < w * h) return;

            var rgb = new byte[w * h * 3];
            // WebCamTexture is bottom-up; flip Y for ZXing.
            int i = 0;
            for (int y = h - 1; y >= 0; y--)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    var c = px[row + x];
                    rgb[i++] = c.r;
                    rgb[i++] = c.g;
                    rgb[i++] = c.b;
                }
            }

            try
            {
                var source = new RGBLuminanceSource(rgb, w, h, RGBLuminanceSource.BitmapFormat.RGB24);
                var result = _reader.Decode(source);
                if (result != null && !string.IsNullOrEmpty(result.Text))
                    Finish(result.Text);
            }
            catch
            {
                /* keep scanning */
            }
        }

        void OnDestroy() => StopCamera();

        void AddBtn(string name, Vector3 pos, Vector2 size, string label, Action onClick)
        {
            var root = new GameObject("Ui_" + name).transform;
            root.SetParent(_root, false);
            root.localPosition = pos;
            Quad("Bg", root, Vector3.zero, size, new Color(0.12f, 0.22f, 0.33f, 0.97f), 3020);
            Text("Label", root, new Vector3(0f, 0f, -0.006f), label, 0.007f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.03f);
            col.isTrigger = true;
            _hits.Add((root, onClick));
        }

        static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        static void Quad(string name, Transform parent, Vector3 pos, Vector2 size, Color color, int queue)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            r.sharedMaterial.renderQueue = queue;
        }

        static TextMesh Text(string name, Transform parent, Vector3 pos, string msg, float size, Color color)
        {
            return XrText.Make(name, parent, pos, msg, size, color, TextAnchor.MiddleCenter, TextAlignment.Center);
        }
    }
}
