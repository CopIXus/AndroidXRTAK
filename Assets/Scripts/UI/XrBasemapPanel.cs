using System;
using System.Collections.Generic;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK Map Manager–style basemap picker. Lists imagery options and switches
    /// DemTerrainMap via SetImageryMode (rebuilds tiles).
    /// </summary>
    public class XrBasemapPanel : MonoBehaviour
    {
        const float PanelW = 0.78f;
        const float RowH = 0.078f;

        DemTerrainMap _terrain;
        Transform _cam;
        Transform _root;
        TextMesh _title;
        TextMesh _statusLine;
        Transform _rowsRoot;
        readonly List<(Transform root, Action onClick)> _hitTargets = new List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        float _nextClickTime;

        static readonly (DemTerrainMap.ImageryMode mode, string label)[] Options =
        {
            (DemTerrainMap.ImageryMode.GoogleHybrid, "Google Hybrid"),
            (DemTerrainMap.ImageryMode.GoogleSatellite, "Google Satellite"),
            (DemTerrainMap.ImageryMode.GoogleRoads, "Google Roads"),
            (DemTerrainMap.ImageryMode.EsriWorldImagery, "ESRI World Imagery"),
        };

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrBasemapPanel Create()
        {
            var go = new GameObject("XrBasemapPanel");
            return go.AddComponent<XrBasemapPanel>();
        }

        public void Configure(DemTerrainMap terrain, Transform cam)
        {
            _terrain = terrain;
            _cam = cam;
        }

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, 0.52f),
                new Color(0.05f, 0.10f, 0.16f, 0.96f), 2995);

            _title = Text("Title", _root, new Vector3(0f, 0.21f, -0.01f), "MAPS", 0.011f,
                new Color(0.85f, 0.95f, 1f, 1f));

            _statusLine = Text("StatusLine", _root, new Vector3(0f, -0.21f, -0.01f), "", 0.006f,
                new Color(0.7f, 0.85f, 1f, 0.9f));

            _rowsRoot = new GameObject("Rows").transform;
            _rowsRoot.SetParent(_root, false);
        }

        public void Open(Transform cam)
        {
            if (cam != null) _cam = cam;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildRows();
        }

        public void Hide() => _root.gameObject.SetActive(false);

        void PlaceFacing()
        {
            if (_cam == null) return;
            var camPos = _cam.position;
            var flat = _cam.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            _root.position = camPos + flat * 1.7f + Vector3.up * 0.05f;
            _root.rotation = XrUiFacing.RotationFacingUser(_root.position, camPos);
        }

        void RebuildRows()
        {
            foreach (Transform child in _rowsRoot) Destroy(child.gameObject);
            _hitTargets.Clear();

            AddButton("Close", new Vector3(PanelW / 2f - 0.05f, 0.21f, -0.01f),
                new Vector2(0.07f, 0.05f), "X", false, Hide);

            var current = _terrain != null
                ? _terrain.CurrentImageryMode
                : DemTerrainMap.ImageryMode.GoogleHybrid;

            float y = 0.12f;
            foreach (var (mode, label) in Options)
            {
                bool on = mode == current;
                var captured = mode;
                AddRow(label, on ? "ACTIVE" : "SELECT", on, y, () => Select(captured));
                y -= RowH;
            }

            SetStatus(LabelFor(current));
        }

        void Select(DemTerrainMap.ImageryMode mode)
        {
            if (_terrain == null) return;
            _terrain.SetImageryMode(mode);
            SetStatus("Switched · " + LabelFor(mode));
            RebuildRows();
        }

        static string LabelFor(DemTerrainMap.ImageryMode mode)
        {
            foreach (var (m, label) in Options)
                if (m == mode) return label;
            return mode.ToString();
        }

        void SetStatus(string msg)
        {
            if (_statusLine != null) _statusLine.text = msg ?? "";
        }

        void AddRow(string label, string stateLabel, bool on, float y, Action onClick)
        {
            var row = new GameObject("Row").transform;
            row.SetParent(_rowsRoot, false);
            row.localPosition = new Vector3(0f, y, 0f);

            Quad("Bg", row, Vector3.zero, new Vector2(PanelW - 0.06f, RowH - 0.01f),
                new Color(0.10f, 0.18f, 0.27f, 0.95f), 3000);

            var nameTm = Text("Name", row, new Vector3(-PanelW / 2f + 0.06f, 0f, -0.008f),
                label, 0.0085f, Color.white);
            nameTm.anchor = TextAnchor.MiddleLeft;
            nameTm.alignment = TextAlignment.Left;

            var chip = new GameObject("Chip").transform;
            chip.SetParent(row, false);
            chip.localPosition = new Vector3(PanelW / 2f - 0.12f, 0f, -0.006f);
            Quad("ChipBg", chip, Vector3.zero, new Vector2(0.15f, 0.042f),
                on ? new Color(0.13f, 0.45f, 0.25f, 0.97f) : new Color(0.25f, 0.30f, 0.38f, 0.97f), 3005);
            Text("ChipLabel", chip, new Vector3(0f, 0f, -0.004f), stateLabel, 0.006f, Color.white);

            var col = row.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(PanelW - 0.04f, RowH, 0.03f);
            col.isTrigger = true;
            _hitTargets.Add((row, onClick));
        }

        void AddButton(string name, Vector3 pos, Vector2 size, string label, bool highlighted, Action onClick)
        {
            var root = new GameObject("Ui_" + name).transform;
            root.SetParent(_rowsRoot, false);
            root.localPosition = pos;
            Quad("Bg", root, Vector3.zero, size,
                highlighted ? new Color(0.16f, 0.42f, 0.62f, 0.97f) : new Color(0.12f, 0.22f, 0.33f, 0.97f), 3000);
            Text("Label", root, new Vector3(0f, 0f, -0.006f), label, 0.0075f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.03f);
            col.isTrigger = true;
            _hitTargets.Add((root, onClick));
        }

        void Update()
        {
            if (!IsVisible) return;
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
                    foreach (var (root, onClick) in _hitTargets)
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
            return XrText.Make(name, parent, pos, msg, size, color);
        }
    }
}
