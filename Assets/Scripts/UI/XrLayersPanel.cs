using System;
using System.Collections.Generic;
using TakXr.Cot;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// In-headset Channels / Data Packages / Data Sync (missions) panel — the
    /// standalone replacement for the web toolbar panels, backed by TakLayersService
    /// (Marti API direct to the TAK server). One list with tabs; rows toggle
    /// channels, import/remove packages, subscribe/unsubscribe missions.
    /// </summary>
    public class XrLayersPanel : MonoBehaviour
    {
        public enum Tab { Channels, Packages, Missions }

        const int RowsPerPage = 8;
        const float RowH = 0.062f;
        const float PanelW = 0.85f;

        TakLayersService _layers;
        Transform _cam;

        Transform _root;
        TextMesh _title;
        TextMesh _statusLine;
        Transform _rowsRoot;
        readonly List<(Transform root, Action onClick)> _hitTargets = new List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        float _nextClickTime;

        Tab _tab = Tab.Channels;
        int _page;
        bool _busy;

        List<ChannelRow> _channels = new List<ChannelRow>();
        List<PackageRow> _packages = new List<PackageRow>();
        List<MissionRow> _missions = new List<MissionRow>();

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrLayersPanel Create()
        {
            var go = new GameObject("XrLayersPanel");
            return go.AddComponent<XrLayersPanel>();
        }

        public void Configure(TakLayersService layers, Transform cam)
        {
            _layers = layers;
            _cam = cam;
        }

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, 0.72f),
                new Color(0.05f, 0.10f, 0.16f, 0.96f), 2995);

            _title = Text("Title", _root, new Vector3(0f, 0.315f, -0.01f), "CHANNELS", 0.011f,
                new Color(0.85f, 0.95f, 1f, 1f));

            _statusLine = Text("StatusLine", _root, new Vector3(0f, -0.315f, -0.01f), "", 0.006f,
                new Color(0.7f, 0.85f, 1f, 0.9f));

            _rowsRoot = new GameObject("Rows").transform;
            _rowsRoot.SetParent(_root, false);
        }

        public void Open(Tab tab, Transform cam)
        {
            _tab = tab;
            _page = 0;
            if (cam != null) _cam = cam;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            _ = RefreshData();
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

        async System.Threading.Tasks.Task RefreshData()
        {
            if (_layers == null) return;
            _busy = true;
            SetStatus(_layers.MartiReady ? "Loading…" : "Waiting for TAK cert…");
            RebuildRows();
            try
            {
                switch (_tab)
                {
                    case Tab.Channels: _channels = await _layers.ListChannels(); break;
                    case Tab.Packages: _packages = await _layers.ListPackages(); break;
                    case Tab.Missions: _missions = await _layers.ListMissions(); break;
                }
                SetStatus(_layers.LastError != null ? "Error: " + Trunc(_layers.LastError, 60) : "");
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + Trunc(ex.Message, 60));
            }
            _busy = false;
            if (IsVisible) RebuildRows();
        }

        void SetStatus(string msg)
        {
            if (_statusLine != null) _statusLine.text = msg ?? "";
        }

        int RowCount => _tab switch
        {
            Tab.Channels => _channels.Count,
            Tab.Packages => _packages.Count,
            _ => _missions.Count,
        };

        void RebuildRows()
        {
            foreach (Transform child in _rowsRoot) Destroy(child.gameObject);
            _hitTargets.Clear();

            _title.text = _tab switch
            {
                Tab.Channels => "CHANNELS",
                Tab.Packages => "DATA PACKAGES",
                _ => "DATA SYNC · MISSIONS",
            };

            // Tab strip.
            AddButton("TabCh", new Vector3(-0.28f, 0.255f, -0.01f), new Vector2(0.24f, 0.05f),
                "Channels", _tab == Tab.Channels, () => { _tab = Tab.Channels; _page = 0; _ = RefreshData(); });
            AddButton("TabPk", new Vector3(0f, 0.255f, -0.01f), new Vector2(0.24f, 0.05f),
                "Packages", _tab == Tab.Packages, () => { _tab = Tab.Packages; _page = 0; _ = RefreshData(); });
            AddButton("TabMi", new Vector3(0.28f, 0.255f, -0.01f), new Vector2(0.24f, 0.05f),
                "Missions", _tab == Tab.Missions, () => { _tab = Tab.Missions; _page = 0; _ = RefreshData(); });

            // Close.
            AddButton("Close", new Vector3(0.385f, 0.315f, -0.01f), new Vector2(0.07f, 0.05f),
                "X", false, Hide);

            int total = RowCount;
            int maxPage = Mathf.Max(0, (total - 1) / RowsPerPage);
            _page = Mathf.Clamp(_page, 0, maxPage);
            int start = _page * RowsPerPage;
            float y = 0.19f;

            for (int i = start; i < Mathf.Min(start + RowsPerPage, total); i++)
            {
                int idx = i;
                string label, state;
                bool on;
                Action act;
                switch (_tab)
                {
                    case Tab.Channels:
                    {
                        var c = _channels[idx];
                        label = c.name;
                        on = c.active;
                        state = c.active ? "ON" : "OFF";
                        act = () => ToggleChannel(c);
                        break;
                    }
                    case Tab.Packages:
                    {
                        var p = _packages[idx];
                        label = p.name;
                        on = p.imported;
                        state = p.imported ? "REMOVE" : "IMPORT";
                        act = () => TogglePackage(p);
                        break;
                    }
                    default:
                    {
                        var m = _missions[idx];
                        label = m.name;
                        on = m.subscribed;
                        state = m.subscribed ? "LEAVE" : "JOIN";
                        act = () => ToggleMission(m);
                        break;
                    }
                }
                AddRow(label, state, on, y, act);
                y -= RowH;
            }

            if (total == 0 && !_busy)
                Text("Empty", _rowsRoot, new Vector3(0f, 0.05f, -0.01f),
                    _layers != null && _layers.MartiReady ? "(none found)" : "TAK cert not ready",
                    0.008f, new Color(0.7f, 0.8f, 0.9f, 0.8f));

            // Pager.
            if (maxPage > 0)
            {
                AddButton("Prev", new Vector3(-0.28f, -0.255f, -0.01f), new Vector2(0.16f, 0.05f),
                    "< Prev", false, () => { _page = Mathf.Max(0, _page - 1); RebuildRows(); });
                Text("Page", _rowsRoot, new Vector3(0f, -0.255f, -0.01f),
                    $"{_page + 1}/{maxPage + 1}", 0.008f, Color.white);
                AddButton("Next", new Vector3(0.28f, -0.255f, -0.01f), new Vector2(0.16f, 0.05f),
                    "Next >", false, () => { _page = Mathf.Min(maxPage, _page + 1); RebuildRows(); });
            }
        }

        async void ToggleChannel(ChannelRow c)
        {
            if (_busy || _layers == null) return;
            _busy = true;
            SetStatus((c.active ? "Deactivating " : "Activating ") + c.name + "…");
            try
            {
                await _layers.SetChannelActive(c.name, !c.active);
                _channels = await _layers.ListChannels();
                SetStatus("");
            }
            catch (Exception ex) { SetStatus("Error: " + Trunc(ex.Message, 60)); }
            _busy = false;
            if (IsVisible) RebuildRows();
        }

        async void TogglePackage(PackageRow p)
        {
            if (_busy || _layers == null) return;
            _busy = true;
            try
            {
                if (p.imported)
                {
                    SetStatus("Removing " + p.name + "…");
                    await _layers.RemovePackage(p.hash);
                    SetStatus("Removed " + p.name);
                }
                else
                {
                    SetStatus("Importing " + p.name + "…");
                    int n = await _layers.ImportPackage(p.hash);
                    SetStatus($"Imported {p.name}: {n} CoTs");
                }
                _packages = await _layers.ListPackages();
            }
            catch (Exception ex) { SetStatus("Error: " + Trunc(ex.Message, 60)); }
            _busy = false;
            if (IsVisible) RebuildRows();
        }

        async void ToggleMission(MissionRow m)
        {
            if (_busy || _layers == null) return;
            _busy = true;
            try
            {
                if (m.subscribed)
                {
                    SetStatus("Leaving " + m.name + "…");
                    await _layers.UnsubscribeMission(m.name);
                    SetStatus("Left " + m.name);
                }
                else
                {
                    SetStatus("Joining " + m.name + "…");
                    int n = await _layers.SubscribeMission(m.name);
                    SetStatus($"Joined {m.name}: {n} CoTs");
                }
                _missions = await _layers.ListMissions();
            }
            catch (Exception ex) { SetStatus("Error: " + Trunc(ex.Message, 60)); }
            _busy = false;
            if (IsVisible) RebuildRows();
        }

        void AddRow(string label, string stateLabel, bool on, float y, Action onClick)
        {
            var row = new GameObject("Row").transform;
            row.SetParent(_rowsRoot, false);
            row.localPosition = new Vector3(0f, y, 0f);

            Quad("Bg", row, Vector3.zero, new Vector2(PanelW - 0.06f, RowH - 0.008f),
                new Color(0.10f, 0.18f, 0.27f, 0.95f), 3000);

            var nameTm = Text("Name", row, new Vector3(-PanelW / 2f + 0.06f, 0f, -0.008f),
                Trunc(label, 34), 0.0085f, Color.white);
            nameTm.anchor = TextAnchor.MiddleLeft;
            nameTm.alignment = TextAlignment.Left;

            // State chip on the right.
            var chip = new GameObject("Chip").transform;
            chip.SetParent(row, false);
            chip.localPosition = new Vector3(PanelW / 2f - 0.115f, 0f, -0.006f);
            Quad("ChipBg", chip, Vector3.zero, new Vector2(0.13f, 0.04f),
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
            // Crisp raster, identical world size to the legacy fontSize-64 sites.
            return XrText.Make(name, parent, pos, msg, size, color);
        }
    }
}
