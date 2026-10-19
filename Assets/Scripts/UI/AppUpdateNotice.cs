using System;
using TakXr.Core;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// World-space notice shown only when a newer GitHub Release APK is available.
    /// </summary>
    public class AppUpdateNotice : MonoBehaviour
    {
        const float PanelW = 0.72f;
        const float PanelH = 0.28f;

        Transform _cam;
        Action<string> _flash;
        Transform _root;
        TextMesh _body;
        string _dismissedVersion;
        readonly System.Collections.Generic.List<(Transform root, Action onClick)> _hits =
            new System.Collections.Generic.List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        float _nextClickTime;
        bool _hooked;

        public static AppUpdateNotice Create()
        {
            var go = new GameObject("AppUpdateNotice");
            return go.AddComponent<AppUpdateNotice>();
        }

        public void Configure(Transform cam, Action<string> flash)
        {
            _cam = cam;
            _flash = flash;
        }

        void Awake() => Build();

        void OnEnable() => TryHook();

        void Start()
        {
            TryHook();
            Refresh();
        }

        void OnDisable()
        {
            if (_hooked && AppUpdateChecker.Instance != null)
                AppUpdateChecker.Instance.Changed -= OnChanged;
            _hooked = false;
        }

        void TryHook()
        {
            if (_hooked || AppUpdateChecker.Instance == null) return;
            AppUpdateChecker.Instance.Changed += OnChanged;
            _hooked = true;
        }

        void OnChanged() => Refresh();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.05f, 0.12f, 0.18f, 0.96f), 3100);
            Text("Title", _root, new Vector3(0f, 0.08f, -0.01f),
                "UPDATE AVAILABLE", 0.01f, new Color(0.85f, 0.95f, 1f, 1f));
            _body = Text("Body", _root, new Vector3(0f, 0.02f, -0.01f),
                "", 0.007f, new Color(0.9f, 0.95f, 1f, 0.95f));
            AddButton("Install", new Vector3(-0.14f, -0.07f, -0.01f), new Vector2(0.22f, 0.05f),
                "Install", true, () =>
                {
                    AppUpdateChecker.Instance?.BeginInstall();
                    _flash?.Invoke("Downloading update…");
                });
            AddButton("Later", new Vector3(0.14f, -0.07f, -0.01f), new Vector2(0.22f, 0.05f),
                "Later", false, () =>
                {
                    _dismissedVersion = AppUpdateChecker.Instance != null
                        ? AppUpdateChecker.Instance.LatestVersion
                        : "";
                    Refresh();
                });
        }

        void Refresh()
        {
            var checker = AppUpdateChecker.Instance;
            var show = checker != null && checker.UpdateAvailable
                && checker.LatestVersion != _dismissedVersion;
            if (_root != null) _root.gameObject.SetActive(show);
            if (!show) return;
            if (_body != null)
                _body.text = "v" + checker.LatestVersion + "  ·  " + checker.StatusMessage;
            PlaceFacing();
        }

        void PlaceFacing()
        {
            if (_cam == null || _root == null) return;
            var camPos = _cam.position;
            var flat = _cam.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            _root.position = camPos + flat * 1.35f + Vector3.down * 0.15f;
            _root.rotation = XrUiFacing.RotationFacingUser(_root.position, camPos);
        }

        void Update()
        {
            if (_root == null || !_root.gameObject.activeSelf) return;
            var checker = AppUpdateChecker.Instance;
            if (checker != null && checker.Busy && _body != null)
                _body.text = checker.StatusMessage;

            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;
                if (!rising || Time.unscaledTime < _nextClickTime) continue;

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
                            _nextClickTime = Time.unscaledTime + 0.35f;
                            return;
                        }
                    }
                }
            }
        }

        void AddButton(string name, Vector3 pos, Vector2 size, string label, bool highlighted, Action onClick)
        {
            var root = new GameObject("Ui_" + name).transform;
            root.SetParent(_root, false);
            root.localPosition = pos;
            Quad("Bg", root, Vector3.zero, size,
                highlighted ? new Color(0.16f, 0.42f, 0.62f, 0.97f) : new Color(0.12f, 0.22f, 0.33f, 0.97f), 3101);
            Text("Label", root, new Vector3(0f, 0f, -0.006f), label, 0.0065f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.03f);
            col.isTrigger = true;
            _hits.Add((root, onClick));
        }

        static void Quad(string name, Transform parent, Vector3 pos, Vector2 size, Color color, int queue)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            r.sharedMaterial.renderQueue = queue;
        }

        static TextMesh Text(string name, Transform parent, Vector3 pos, string msg, float size, Color color) =>
            XrText.Make(name, parent, pos, msg, size, color);
    }
}
