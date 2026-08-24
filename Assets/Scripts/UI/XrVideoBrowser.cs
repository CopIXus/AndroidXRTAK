using System;
using System.Collections.Generic;
using TakXr.Cot;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK Video tool: list CoTs that carry a video URL and open XrVideoPanel.
    /// </summary>
    public class XrVideoBrowser : MonoBehaviour
    {
        const float PanelW = 0.88f;
        const float PanelH = 0.92f;
        const int PageSize = 6;

        CotFeedClient _feed;
        XrVideoPanel _video;
        Transform _cam;
        Action<string> _flash;

        Transform _root;
        Transform _listRoot;
        TextMesh _title;
        readonly List<(Transform root, Action onClick)> _hits = new List<(Transform, Action)>();
        readonly List<(string uid, string name, string url)> _entries = new List<(string, string, string)>();
        readonly bool[] _wasGrabbing = new bool[2];
        float _nextClick;
        int _page;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrVideoBrowser Create()
        {
            var go = new GameObject("XrVideoBrowser");
            return go.AddComponent<XrVideoBrowser>();
        }

        public void Configure(CotFeedClient feed, XrVideoPanel video, Transform cam, Action<string> flash)
        {
            _feed = feed;
            _video = video;
            _cam = cam;
            _flash = flash;
        }

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);
            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.04f, 0.05f, 0.08f, 0.96f), 2995);
            _title = Text("Title", _root, new Vector3(0f, PanelH / 2f - 0.05f, -0.01f),
                "VIDEO", 0.008f, new Color(0.85f, 0.95f, 1f, 1f));
            _listRoot = new GameObject("List").transform;
            _listRoot.SetParent(_root, false);
            AddChromeBtn("Prev", new Vector3(-0.28f, -PanelH / 2f + 0.06f, -0.01f), () =>
            {
                if (_page > 0) { _page--; RebuildList(); }
            });
            AddChromeBtn("Next", new Vector3(0.0f, -PanelH / 2f + 0.06f, -0.01f), () =>
            {
                int maxPage = Mathf.Max(0, (_entries.Count - 1) / PageSize);
                if (_page < maxPage) { _page++; RebuildList(); }
            });
            AddChromeBtn("Close", new Vector3(0.28f, -PanelH / 2f + 0.06f, -0.01f), Hide);
        }

        public void Open(Transform cam)
        {
            if (cam != null) _cam = cam;
            RefreshEntries();
            _page = 0;
            RebuildList();
            _root.gameObject.SetActive(true);
            PlaceFacing();
            _flash?.Invoke(_entries.Count == 0 ? "No video streams" : $"{_entries.Count} video streams");
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        void RefreshEntries()
        {
            _entries.Clear();
            if (_feed == null) return;
            foreach (var kv in _feed.Cots)
            {
                var cot = kv.Value;
                string url = cot?.detail?.video?.url;
                if (string.IsNullOrEmpty(url) && !CotClassifier.IsVideoCot(cot)) continue;
                if (string.IsNullOrEmpty(url)) continue;
                string name = cot.Callsign;
                if (string.IsNullOrEmpty(name)) name = cot.uid;
                _entries.Add((cot.uid, name, url));
            }
            _entries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        void RebuildList()
        {
            // Clear prior row hit targets (keep chrome Prev/Next/Close at end of list build).
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                var t = _hits[i].root;
                if (t != null && t.name.StartsWith("Row_", StringComparison.Ordinal))
                {
                    Destroy(t.gameObject);
                    _hits.RemoveAt(i);
                }
            }
            if (_listRoot != null)
            {
                for (int i = _listRoot.childCount - 1; i >= 0; i--)
                    Destroy(_listRoot.GetChild(i).gameObject);
            }

            if (_title != null)
            {
                int pages = Mathf.Max(1, (_entries.Count + PageSize - 1) / PageSize);
                _title.text = $"VIDEO  ({_entries.Count})  p{_page + 1}/{pages}";
            }

            int start = _page * PageSize;
            float y = PanelH / 2f - 0.14f;
            for (int i = 0; i < PageSize; i++)
            {
                int idx = start + i;
                if (idx >= _entries.Count) break;
                var e = _entries[idx];
                var row = new GameObject("Row_" + e.uid).transform;
                row.SetParent(_listRoot, false);
                row.localPosition = new Vector3(0f, y, 0f);
                y -= 0.11f;
                Quad("Bg", row, new Vector3(0f, 0f, 0.004f), new Vector2(PanelW - 0.08f, 0.095f),
                    new Color(0.10f, 0.13f, 0.18f, 0.95f), 3000);
                var label = Text("L", row, new Vector3(-0.36f, 0.012f, -0.004f), e.name,
                    0.0042f, Color.white);
                label.anchor = TextAnchor.MiddleLeft;
                label.alignment = TextAlignment.Left;
                var urlTm = Text("U", row, new Vector3(-0.36f, -0.028f, -0.004f),
                    Trunc(e.url, 42), 0.0028f, new Color(0.65f, 0.8f, 0.95f, 0.8f));
                urlTm.anchor = TextAnchor.MiddleLeft;
                urlTm.alignment = TextAlignment.Left;
                var col = row.gameObject.AddComponent<BoxCollider>();
                col.size = new Vector3(PanelW - 0.06f, 0.1f, 0.08f);
                string uid = e.uid;
                _hits.Add((row, () => Play(uid)));
            }
        }

        void Play(string uid)
        {
            if (_feed == null || !_feed.Cots.TryGetValue(uid, out var cot)) return;
            _video?.Show(cot, _cam);
            _flash?.Invoke("Playing " + (cot.Callsign ?? uid));
            Hide();
        }

        static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");

        void PlaceFacing()
        {
            if (_cam == null || _root == null) return;
            var pos = _cam.position + _cam.TransformDirection(new Vector3(0f, -0.05f, 1.7f));
            _root.position = pos;
            _root.rotation = XrUiFacing.RotationFacingUser(pos, _cam.position);
        }

        void LateUpdate()
        {
            if (!IsVisible) return;
            PlaceFacing();
            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;
                if (!aimOk || !rising || Time.unscaledTime < _nextClick) continue;
                var hits = Physics.RaycastAll(origin, fwd, 6f, ~0, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var hit in hits)
                {
                    foreach (var t in _hits)
                    {
                        if (t.root == null) continue;
                        if (hit.transform == t.root || hit.transform.IsChildOf(t.root))
                        {
                            t.onClick?.Invoke();
                            _nextClick = Time.unscaledTime + 0.35f;
                            return;
                        }
                    }
                }
            }
        }

        void AddChromeBtn(string label, Vector3 localPos, Action onClick)
        {
            var root = new GameObject("Btn_" + label).transform;
            root.SetParent(_root, false);
            root.localPosition = localPos;
            Quad("Bg", root, new Vector3(0f, 0f, 0.004f), new Vector2(0.18f, 0.06f),
                new Color(0.12f, 0.16f, 0.22f, 0.95f), 3000);
            Text("L", root, new Vector3(0f, 0f, -0.004f), label, 0.0038f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.19f, 0.07f, 0.08f);
            _hits.Add((root, onClick));
        }

        static GameObject Quad(string name, Transform parent, Vector3 localPos, Vector2 size, Color color, int rq)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", color);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            r.sharedMaterial.renderQueue = rq;
            return go;
        }

        static TextMesh Text(string name, Transform parent, Vector3 localPos, string msg, float cs, Color c) =>
            XrText.Make(name, parent, localPos, msg, cs, c);
    }
}
