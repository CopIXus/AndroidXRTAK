using System;
using System.Collections;
using System.Collections.Generic;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Locomotion;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.UI
{
    /// <summary>
    /// Headset chrome: compact hamburger tools menu plus a north compass dial
    /// with readable HDG/geo captions under the ring.
    /// </summary>
    public class XrChromeHud : MonoBehaviour
    {
        static readonly string[] Cardinals16 =
        {
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"
        };

        [SerializeField] Transform cameraTransform;
        [SerializeField] AppConfig config;
        [SerializeField] DemTerrainMap terrainMap;
        [SerializeField] XrWorldLocomotion locomotion;
        [SerializeField] XrWorldRoot world;
        [SerializeField] SettingsPanelRuntime settings;
        [SerializeField] CotFeedClient feed;
        [SerializeField] CotLayerController cotLayer;
        [SerializeField] XrFollowController follow;
        [SerializeField] TakDirectHub direct;
        [SerializeField] XrLayersPanel layersPanel;
        [SerializeField] XrDrawTool drawTool;
        [SerializeField] XrBasemapPanel basemapPanel;
        [SerializeField] XrServerPanel serverPanel;
        [SerializeField] XrSettingsPanel settingsPanel;
        [SerializeField] XrRadialMenu radialMenu;
        [SerializeField] XrCopController copController;
        [SerializeField] XrInfoPanel infoPanel;
        [SerializeField] XrVideoPanel videoPanel;
        [SerializeField] XrGoToPanel goToPanel;
        [SerializeField] XrVideoBrowser videoBrowser;
        [SerializeField] XrTrackHistory trackHistory;
        [SerializeField] XrRangeMeasureTool rangeTool;
        [SerializeField] XrElevationTool elevationTool;

        Transform _compassRoot;
        Transform _northGroup;
        TextMesh _northLabel;
        Transform _telemRoot;
        TextMesh _telemHdg;
        TextMesh _telemGeo;
        string _lastTelem = "";
        int _drawCycle;

        Transform _toolbar;
        Transform _menuRoot;
        Transform _menuPageRoot;
        Transform _menuBtnIcon;
        TextMesh _menuPageLabel;
        bool _menuOpen;
        int _menuPage;
        bool _quitArmed;
        float _quitArmedUntil;
        TextMesh _statusLabel;
        float _statusHideAt;
        readonly List<ToolBtn> _buttons = new List<ToolBtn>();
        readonly List<ToolDef> _toolCatalog = new List<ToolDef>();
        float _nextClickTime;
        readonly bool[] _wasGrabbing = new bool[2];
        bool _takConnected;
        float _nextHealthPoll;

        struct ToolBtn
        {
            public Transform Root;
            public Action OnClick;
            public string Id;
        }

        struct ToolDef
        {
            public string Id;
            public string Icon;
            public string Label;
            public Action OnClick;
            public bool Enabled;
        }

        public static XrChromeHud Create()
        {
            var go = new GameObject("XrChromeHud");
            return go.AddComponent<XrChromeHud>();
        }

        public void Configure(
            AppConfig cfg,
            Transform cam,
            DemTerrainMap terrain,
            XrWorldLocomotion loco,
            XrWorldRoot worldRoot,
            SettingsPanelRuntime settingsRuntime = null,
            CotFeedClient feedClient = null,
            CotLayerController layer = null,
            XrFollowController followCtrl = null,
            TakDirectHub directClient = null,
            XrLayersPanel layersPanelRef = null,
            XrDrawTool drawToolRef = null,
            XrBasemapPanel basemapPanelRef = null,
            XrServerPanel serverPanelRef = null,
            XrSettingsPanel settingsPanelRef = null,
            XrRadialMenu radialMenuRef = null,
            XrCopController copRef = null,
            XrInfoPanel infoRef = null,
            XrVideoPanel videoRef = null,
            XrGoToPanel goToRef = null,
            XrVideoBrowser videoBrowserRef = null,
            XrTrackHistory trackHistoryRef = null,
            XrRangeMeasureTool rangeToolRef = null,
            XrElevationTool elevationToolRef = null)
        {
            config = cfg;
            cameraTransform = cam;
            terrainMap = terrain;
            locomotion = loco;
            world = worldRoot;
            settings = settingsRuntime;
            feed = feedClient;
            cotLayer = layer;
            follow = followCtrl;
            direct = directClient;
            layersPanel = layersPanelRef;
            drawTool = drawToolRef;
            basemapPanel = basemapPanelRef;
            serverPanel = serverPanelRef;
            settingsPanel = settingsPanelRef;
            radialMenu = radialMenuRef;
            copController = copRef;
            infoPanel = infoRef;
            videoPanel = videoRef;
            goToPanel = goToRef;
            videoBrowser = videoBrowserRef;
            trackHistory = trackHistoryRef;
            rangeTool = rangeToolRef;
            elevationTool = elevationToolRef;
        }

        void Start()
        {
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;

            BuildCompass();
            BuildToolbar();
            BuildStatus();
            _statusHideAt = Time.unscaledTime + 4f;
            FlashStatus("Tools · aim + trigger");
        }

        void BuildCompass()
        {
            _compassRoot = new GameObject("Compass").transform;
            _compassRoot.SetParent(transform, false);

            CreateRingLine("Ring", _compassRoot, 0.038f, 48,
                new Color(0.43f, 0.78f, 1f, 0.65f), 0.0024f);

            // Lubber line = look direction (fixed at far edge / local +Z).
            CreateLine("LookLine", _compassRoot,
                new Vector3(0f, 0.002f, 0.0f),
                new Vector3(0f, 0.002f, 0.052f),
                new Color(1f, 1f, 1f, 0.95f), 0.003f);
            CreateLine("LookTick", _compassRoot,
                new Vector3(-0.006f, 0.002f, 0.048f),
                new Vector3(0.006f, 0.002f, 0.048f),
                new Color(1f, 1f, 1f, 0.9f), 0.0025f);

            // North group rotates so N sits toward geographic north.
            _northGroup = new GameObject("NorthGroup").transform;
            _northGroup.SetParent(_compassRoot, false);
            CreateLine("NorthNeedle", _northGroup,
                Vector3.zero,
                new Vector3(0f, 0.002f, 0.042f),
                new Color(0.35f, 0.85f, 1f, 0.95f), 0.0032f);
            _northLabel = Text("N", _northGroup, new Vector3(0f, 0.003f, 0.052f),
                "N", 0.0026f, new Color(1f, 1f, 1f, 0.95f));

            // Readable block captions under the dial — small like the old arc text,
            // billboarded as a whole toward the viewer each frame.
            _telemRoot = new GameObject("Telem").transform;
            _telemRoot.SetParent(_compassRoot, false);
            _telemRoot.localPosition = new Vector3(0f, -0.002f, -0.068f);
            _telemHdg = XrText.Make("Hdg", _telemRoot, new Vector3(0f, 0.008f, 0f), "",
                0.0018f, new Color(0.85f, 0.95f, 1f, 0.92f),
                TextAnchor.MiddleCenter, TextAlignment.Center);
            _telemGeo = XrText.Make("Geo", _telemRoot, new Vector3(0f, -0.004f, 0f), "",
                0.0015f, new Color(0.72f, 0.86f, 1f, 0.88f),
                TextAnchor.MiddleCenter, TextAlignment.Center);
        }

        void BuildToolbar()
        {
            // Compact hamburger chip only — all tools live in the overflow menu.
            _toolbar = new GameObject("TopBar").transform;
            _toolbar.SetParent(transform, false);

            OpaqueQuad("BarBg", _toolbar, new Vector3(0f, 0f, 0.006f), new Vector2(0.18f, 0.15f),
                new Color(0.04f, 0.06f, 0.09f, 1f), 2995);

            float x = 0f;
            const float step = 0.16f;
            AddBarBtn("menu", "hamburger", ref x, step, () => SetMenu(!_menuOpen), iconSize: 0.11f);

            foreach (var b in _buttons)
            {
                if (b.Id != "menu" || b.Root == null) continue;
                var icon = b.Root.Find("Icon");
                if (icon != null) _menuBtnIcon = icon;
                break;
            }

            BuildMenu();
        }

        void BuildMenu()
        {
            // Parent to chrome root (not the right-side hamburger) so the panel
            // can sit centered in the view when opened.
            _menuRoot = new GameObject("Menu").transform;
            _menuRoot.SetParent(transform, false);

            OpaqueQuad("PanelBg", _menuRoot, new Vector3(0f, -0.48f, 0.008f), new Vector2(1.28f, 1.08f),
                new Color(0.02f, 0.03f, 0.05f, 1f), 2990);

            var header = Text("Header", _menuRoot, new Vector3(-0.58f, -0.02f, -0.002f),
                "Tools", 0.0048f, new Color(1f, 1f, 1f, 0.95f));
            header.anchor = TextAnchor.MiddleLeft;
            header.alignment = TextAlignment.Left;

            AddHeaderIconBtn("hdr_settings", "settings", new Vector3(0.42f, -0.02f, -0.002f), () =>
            {
                HideSidePanels();
                settingsPanel?.Open(cameraTransform);
                SetMenu(false);
            });
            AddHeaderIconBtn("hdr_close", "close", new Vector3(0.56f, -0.02f, -0.002f), () => SetMenu(false));

            _menuPageLabel = Text("Page", _menuRoot, new Vector3(0f, -1.00f, -0.002f),
                "", 0.0036f, new Color(0.75f, 0.82f, 0.9f, 0.85f));
            AddHeaderIconBtn("page_prev", "pageup", new Vector3(-0.56f, -1.00f, -0.002f), () =>
            {
                if (_menuPage > 0) { _menuPage--; RebuildMenuPage(); }
            });
            AddHeaderIconBtn("page_next", "pagedown", new Vector3(0.56f, -1.00f, -0.002f), () =>
            {
                int maxPage = Mathf.Max(0, (_toolCatalog.Count - 1) / MenuPageSize);
                if (_menuPage < maxPage) { _menuPage++; RebuildMenuPage(); }
            });

            _menuPageRoot = new GameObject("Pages").transform;
            _menuPageRoot.SetParent(_menuRoot, false);

            BuildToolCatalog();
            _menuPage = 0;
            RebuildMenuPage();
            _menuRoot.gameObject.SetActive(false);
        }

        void BuildToolCatalog()
        {
            _toolCatalog.Clear();
            // ATAK default tools A–Z (enabled where we have XR implementations).
            Stub("alert", "alert", "Alert");
            Tool("brightness", "brightness", "Brightness", CycleBrightness);
            Stub("casevac", "casevac", "CASEVAC");
            Stub("chat", "chat", "Chat");
            Tool("clear", "clear", "Clear Content", ClearAllTools);
            Stub("contacts", "contacts", "Contacts");
            Tool("package", "package", "Data Packages",
                () => layersPanel?.Open(XrLayersPanel.Tab.Packages, cameraTransform));
            Tool("datasync", "datasync", "Data Sync",
                () => layersPanel?.Open(XrLayersPanel.Tab.Missions, cameraTransform));
            Stub("pointer", "pointer", "Digital Pointer");
            Tool("drawing", "draw", "Drawing Tools", CycleDrawingTool);
            Tool("elevation", "elevation", "Elevation", () => elevationTool?.Toggle());
            Tool("firstperson", "firstperson", "First Person", ToggleFollowSelected);
            Stub("gallery", "gallery", "Gallery");
            Stub("geofence", "geofence", "Geofence");
            Tool("goto", "goto", "GoTo", () =>
            {
                HideSidePanels();
                goToPanel?.Open(cameraTransform);
            });
            Stub("import", "import", "Import");
            Stub("lasso", "lasso", "Lasso Select");
            Stub("linkeud", "plugins", "Link EUD");
            Tool("orientation", "orientation", "Orientation",
                () => { locomotion?.OrientNorth(); FlashStatus("Orientation: north-up"); });
            Stub("plugins", "plugins", "Plugins");
            Stub("quicknav", "quicknav", "Quick Nav");
            Stub("quickpic", "quickpic", "Quick Pic");
            Stub("radio", "radio", "Radio Controls");
            Tool("range", "range", "Range Tools", () => rangeTool?.Toggle());
            Stub("resection", "resection", "Resection");
            Tool("routes", "route", "Routes",
                () => drawTool?.ToggleMode(XrDrawTool.Mode.Route));
            Tool("maps", "map", "Maps", () =>
            {
                HideSidePanels();
                basemapPanel?.Open(cameraTransform);
            });
            Tool("pointdrop", "pointadd", "Point Drop",
                () => drawTool?.ToggleMode(XrDrawTool.Mode.Point));
            Stub("rubbersheet", "rubbersheet", "Rubber Sheet");
            Tool("tracks", "tracks", "Track History", () => trackHistory?.Toggle());
            Tool("video", "video", "Video", () =>
            {
                HideSidePanels();
                videoBrowser?.Open(cameraTransform);
            });

            // XR "plugin" section (inserted before Settings/Quit, like ATAK plugins).
            Tool("channels", "channels", "Channels",
                () => layersPanel?.Open(XrLayersPanel.Tab.Channels, cameraTransform));
            Tool("layers", "layers", "Layers", () =>
            {
                HideSidePanels();
                layersPanel?.Open(XrLayersPanel.Tab.Channels, cameraTransform);
            });
            Tool("server", "server", "Servers", () =>
            {
                HideSidePanels();
                serverPanel?.Open(cameraTransform);
            });
            Tool("closeall", "close", "Close Tools", () =>
            {
                drawTool?.Cancel();
                rangeTool?.Cancel();
                HideSidePanels();
                SetMenu(false);
                FlashStatus("Closed");
            });
            Tool("diag", "plugins", "Diagnostics", () =>
            {
                string recent = TakConnectionLog.FormatRecent(6);
                if (string.IsNullOrEmpty(recent))
                    FlashStatus("No TAK connection log yet");
                else
                {
                    Debug.Log("[TakConn] dump:\n" + recent);
                    FlashStatus(recent.Length > 90 ? recent.Substring(0, 87) + "…" : recent);
                }
            });
            Tool("locate", "locate", "My Location", GoToMyLocation);
            Tool("size", "size", "CoT Size", () =>
            {
                settings?.CycleIconScale();
                float s = settings != null ? settings.IconScaleMultiplier : 1f;
                CotMarkerView.ScaleMultiplier = s;
                FlashStatus($"Icon size x{s:0.##}");
            });

            Tool("settings", "settings", "Settings", () =>
            {
                HideSidePanels();
                settingsPanel?.Open(cameraTransform);
            });
            Tool("quit", "quit", "Quit", ConfirmQuit);
        }

        void Tool(string id, string icon, string label, Action onClick) =>
            _toolCatalog.Add(new ToolDef
            {
                Id = id, Icon = icon, Label = label, OnClick = onClick, Enabled = true
            });

        void Stub(string id, string icon, string label) =>
            _toolCatalog.Add(new ToolDef
            {
                Id = id, Icon = icon, Label = label,
                OnClick = () => FlashStatus($"{label}: not available in XR yet"),
                Enabled = false
            });

        void RebuildMenuPage()
        {
            // Remove prior page tiles from hit list + hierarchy.
            for (int i = _buttons.Count - 1; i >= 0; i--)
            {
                var b = _buttons[i];
                if (b.Root != null && b.Root.name.StartsWith("Tile_", StringComparison.Ordinal))
                {
                    Destroy(b.Root.gameObject);
                    _buttons.RemoveAt(i);
                }
            }
            if (_menuPageRoot != null)
            {
                for (int i = _menuPageRoot.childCount - 1; i >= 0; i--)
                    Destroy(_menuPageRoot.GetChild(i).gameObject);
            }

            int maxPage = Mathf.Max(0, (_toolCatalog.Count - 1) / MenuPageSize);
            _menuPage = Mathf.Clamp(_menuPage, 0, maxPage);
            if (_menuPageLabel != null)
                _menuPageLabel.text = $"{_menuPage + 1} / {maxPage + 1}";

            int start = _menuPage * MenuPageSize;
            for (int i = 0; i < MenuPageSize; i++)
            {
                int idx = start + i;
                if (idx >= _toolCatalog.Count) break;
                var t = _toolCatalog[idx];
                AddToolTile(t.Id, t.Icon, t.Label, i, t.OnClick, t.Enabled);
            }
        }

        void HideSidePanels()
        {
            layersPanel?.Hide();
            basemapPanel?.Hide();
            serverPanel?.Hide();
            settingsPanel?.Hide();
            goToPanel?.Hide();
            videoBrowser?.Hide();
        }

        void CycleBrightness()
        {
            if (terrainMap == null) { FlashStatus("Brightness: no map"); return; }
            float cur = terrainMap.Brightness;
            float next = cur > 0.9f ? 0.75f : cur > 0.6f ? 0.5f : cur > 0.35f ? 0.25f : 1f;
            terrainMap.SetBrightness(next);
            FlashStatus($"Brightness {Mathf.RoundToInt(next * 100f)}%");
        }

        void ConfirmQuit()
        {
            if (_quitArmed && Time.unscaledTime <= _quitArmedUntil)
            {
                FlashStatus("Quitting…");
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
                return;
            }
            _quitArmed = true;
            _quitArmedUntil = Time.unscaledTime + 4f;
            FlashStatus("Quit: tap Quit again to confirm");
        }

        void AddHeaderIconBtn(string id, string iconName, Vector3 localPos, Action onClick)
        {
            var root = new GameObject("Hdr_" + id).transform;
            root.SetParent(_menuRoot, false);
            root.localPosition = localPos;
            MakeIconQuad(root, iconName, 0.055f, new Vector3(0f, 0f, -0.002f));
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.09f, 0.09f, 0.08f);
            _buttons.Add(new ToolBtn { Root = root, OnClick = onClick, Id = id });
        }

        /// <summary>Drawing Tools tile cycles Point → Route → Polygon → Circle.</summary>
        void CycleDrawingTool()
        {
            var modes = new[]
            {
                XrDrawTool.Mode.Point, XrDrawTool.Mode.Route,
                XrDrawTool.Mode.Polygon, XrDrawTool.Mode.Circle
            };
            var mode = modes[_drawCycle % modes.Length];
            _drawCycle++;
            drawTool?.ToggleMode(mode);
            FlashStatus($"Drawing: {mode}");
        }

        void GoToMyLocation()
        {
            // Prefer self SA / identity uid marker; else overview at config origin.
            string selfUid = TakIdentity.ClientUid;
            if (cotLayer != null && cotLayer.TryGetMarkerWorldPos(selfUid, out var selfPos))
            {
                locomotion?.FrameWorldPoint(selfPos, overviewDistM: 160f, heightM: 50f);
                FlashStatus("My location (self)");
                return;
            }
            locomotion?.ResetView();
            FlashStatus("My location (origin)");
        }

        void ToggleFollowSelected()
        {
            if (follow != null && follow.IsFollowing)
            {
                follow.SetFollow(null);
                FlashStatus("Follow stopped");
                return;
            }
            string uid = copController?.LastSelectedUid;
            if (string.IsNullOrEmpty(uid) && infoPanel != null)
                uid = infoPanel.CurrentUid;
            if (string.IsNullOrEmpty(uid))
            {
                FlashStatus("Select a CoT → Follow");
                return;
            }
            follow?.SetFollow(uid);
            FlashStatus("Following " + uid);
        }

        void ClearAllTools()
        {
            drawTool?.Cancel();
            rangeTool?.Cancel();
            if (elevationTool != null && XrElevationTool.IsArmed) elevationTool.Toggle();
            HideSidePanels();
            infoPanel?.Hide();
            videoPanel?.Hide();
            radialMenu?.Hide();
            radialMenu?.ClearRangeBearing();
            trackHistory?.Clear();
            // Remove locally drawn takxr.* CoTs only — never wipe live TAK feed.
            if (feed != null)
            {
                var kill = new System.Collections.Generic.List<string>();
                foreach (var kv in feed.Cots)
                {
                    if (kv.Key != null && kv.Key.StartsWith("takxr.", System.StringComparison.Ordinal))
                        kill.Add(kv.Key);
                }
                foreach (var uid in kill)
                {
                    if (feed.Cots.TryGetValue(uid, out var cot) && direct != null)
                    {
                        var now = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        var del =
                            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                            $"<event version=\"2.0\" uid=\"{uid}\" type=\"t-x-d-d\" how=\"h-g-i-g-o\" " +
                            $"time=\"{now}\" start=\"{now}\" stale=\"{now}\">" +
                            $"<point lat=\"{cot.point?.lat ?? 0}\" lon=\"{cot.point?.lon ?? 0}\" hae=\"0\" ce=\"9999999\" le=\"9999999\"/>" +
                            "<detail><link relation=\"p-p\" type=\"a-f-G\" uid=\"" + uid + "\"/></detail></event>";
                        direct.SendCot(del);
                    }
                    feed.RemoveByUid(uid, notify: false);
                }
                if (kill.Count > 0) feed.NotifyChanged();
            }
            FlashStatus("Cleared local drawings");
        }

        void SetMenu(bool open)
        {
            _menuOpen = open;
            if (_menuRoot != null) _menuRoot.gameObject.SetActive(open);
            // ATAK swaps hamburger ↔ X when the overflow/tools panel toggles.
            if (_menuBtnIcon != null)
            {
                var r = _menuBtnIcon.GetComponent<Renderer>();
                if (r != null && r.sharedMaterial != null)
                {
                    var tex = AtakToolbarIcons.Get(open ? "menuopen" : "hamburger");
                    r.sharedMaterial.mainTexture = tex;
                    if (r.sharedMaterial.HasProperty("_BaseMap"))
                        r.sharedMaterial.SetTexture("_BaseMap", tex);
                }
            }
        }

        public void FlashStatus(string msg)
        {
            // Longer hold for multi-line / timeout diagnostics.
            bool longMsg = !string.IsNullOrEmpty(msg) &&
                           (msg.Length > 40 || msg.IndexOf('\n') >= 0 ||
                            msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            msg.IndexOf("TCP", StringComparison.OrdinalIgnoreCase) >= 0);
            _statusHideAt = Time.unscaledTime + (longMsg ? 8f : 3.5f);
            if (_statusLabel != null)
            {
                _statusLabel.gameObject.SetActive(true);
                _statusLabel.text = msg ?? "";
            }
        }

        void AddBarBtn(string id, string iconName, ref float x, float step, Action onClick,
            float iconSize = 0.095f)
        {
            var root = new GameObject("Btn_" + id).transform;
            root.SetParent(_toolbar, false);
            root.localPosition = new Vector3(x, 0f, 0f);
            x += step;

            MakeIconQuad(root, iconName, iconSize, new Vector3(0f, 0f, -0.002f));

            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.14f, 0.13f, 0.1f);
            col.isTrigger = false;
            _buttons.Add(new ToolBtn { Root = root, OnClick = onClick, Id = id });
        }

        // Tools grid: 4 columns × 4 rows per page.
        const int MenuCols = 4;
        const int MenuRows = 4;
        const int MenuPageSize = MenuCols * MenuRows;
        const float TileW = 0.30f;
        const float TileH = 0.20f;
        const float TileGap = 0.01f;

        void AddToolTile(string id, string iconName, string label, int index, Action onClick,
            bool enabled = true)
        {
            int cIdx = index % MenuCols;
            int rIdx = index / MenuCols;
            var root = new GameObject("Tile_" + id).transform;
            root.SetParent(_menuPageRoot != null ? _menuPageRoot : _menuRoot, false);
            float originX = -1.5f * (TileW + TileGap);
            root.localPosition = new Vector3(
                originX + cIdx * (TileW + TileGap),
                -0.16f - rIdx * (TileH + TileGap),
                0f);

            // Soft cell plate (opaque) — no hairline separators (they alias badly).
            OpaqueQuad("Bg", root, new Vector3(0f, 0f, 0.004f), new Vector2(TileW - 0.008f, TileH - 0.008f),
                new Color(0.08f, 0.10f, 0.14f, 1f), 3000);

            float alpha = enabled ? 1f : 0.4f;
            MakeIconQuad(root, iconName, 0.078f, new Vector3(0f, 0.028f, -0.004f), alpha);
            string display = label.Length > 14 ? label.Replace(" ", "\n") : label;
            if (label == "Digital Pointer" || label == "Radio Controls" || label == "Track History"
                || label == "Rubber Sheet" || label == "Drawing Tools" || label == "Data Packages"
                || label == "Lasso Select" || label == "Clear Content" || label == "First Person"
                || label == "Point Drop" || label == "Close Tools")
                display = label.Replace(" ", "\n");
            Text("Label", root, new Vector3(0f, -0.062f, -0.004f), display,
                0.0034f, new Color(1f, 1f, 1f, 0.96f * alpha));

            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(TileW + 0.005f, TileH + 0.005f, 0.1f);
            col.isTrigger = false;
            _buttons.Add(new ToolBtn
            {
                Root = root,
                OnClick = () => { onClick?.Invoke(); if (enabled) SetMenu(false); },
                Id = id
            });
        }

        static void MakeIconQuad(Transform parent, string iconName, float size, Vector3 localPos,
            float alpha = 1f)
        {
            var iconGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            iconGo.name = "Icon";
            iconGo.transform.SetParent(parent, false);
            iconGo.transform.localPosition = localPos;
            iconGo.transform.localScale = new Vector3(size, size, 1f);
            Destroy(iconGo.GetComponent<Collider>());
            var r = iconGo.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")
                                   ?? Shader.Find("Unlit/Transparent")
                                   ?? Shader.Find("Sprites/Default"));
            var tex = AtakToolbarIcons.Get(iconName);
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            var tint = new Color(1f, 1f, 1f, alpha);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            // Alpha-clipped icons stay sharper under MSAA than blended Sprites.
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            mat.renderQueue = 3010;
            r.sharedMaterial = mat;
        }

        void BuildStatus()
        {
            // Flash-only toast — no persistent tracks/TAK Direct block.
            _statusLabel = XrText.Make("Status", transform, Vector3.zero, "",
                0.0052f, new Color(0.90f, 0.96f, 1f, 0.88f),
                TextAnchor.MiddleCenter, TextAlignment.Center);
            _statusLabel.gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (cameraTransform == null)
            {
                if (Camera.main != null) cameraTransform = Camera.main.transform;
                else return;
            }

            PlaceHeadLockedChrome();
            UpdateCompass();
            BillboardCompassText();
            UpdateStatus();
            PollUiSelect();

            bool mayPollHealth = config != null && config.allowBackendFallback &&
                                 (feed == null || !feed.DirectMode);
            if (Time.unscaledTime >= _nextHealthPoll && mayPollHealth)
            {
                _nextHealthPoll = Time.unscaledTime + 10f;
                StartCoroutine(PollHealthOnce());
            }
        }

        void BillboardCompassText()
        {
            if (cameraTransform == null) return;
            var camPos = cameraTransform.position;
            // Whole caption block faces the headset (not yaw-locked with the dial).
            if (_telemRoot != null)
                XrUiFacing.FaceUser(_telemRoot, cameraTransform);
            if (_northLabel != null)
            {
                var away = _northLabel.transform.position - camPos;
                if (away.sqrMagnitude > 1e-8f)
                    _northLabel.transform.rotation =
                        Quaternion.LookRotation(away.normalized, Vector3.up);
            }
        }

        IEnumerator PollHealthOnce()
        {
            // Standalone: never touch the backend unless opt-in fallback is enabled.
            if (config == null || !config.allowBackendFallback) yield break;
            if (feed != null && feed.DirectMode) yield break;
            using var req = UnityWebRequest.Get(config.HealthUrl);
            req.timeout = 8;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) yield break;
            var text = req.downloadHandler.text ?? "";
            _takConnected = text.IndexOf("\"takConnected\":true", StringComparison.Ordinal) >= 0;
        }

        void PlaceHeadLockedChrome()
        {
            var camPos = cameraTransform.position;
            float yaw = cameraTransform.eulerAngles.y;
            var yawOnly = Quaternion.Euler(0f, yaw, 0f);

            if (_compassRoot != null)
            {
                _compassRoot.position = camPos + cameraTransform.TransformDirection(new Vector3(0f, -0.32f, 1.15f));
                _compassRoot.rotation = yawOnly;
            }

            const float chromeZ = 2.0f;
            if (_toolbar != null)
            {
                // Compact hamburger chip — top-right; menu is placed separately.
                _toolbar.position = camPos + cameraTransform.TransformDirection(new Vector3(0.55f, 0.48f, chromeZ));
                _toolbar.rotation = XrUiFacing.RotationFacingUser(_toolbar.position, camPos);
            }

            if (_menuRoot != null && _menuOpen)
            {
                // Center the Tools panel in the view so every tile is reachable.
                _menuRoot.position = camPos + cameraTransform.TransformDirection(new Vector3(0f, 0.12f, 1.85f));
                _menuRoot.rotation = XrUiFacing.RotationFacingUser(_menuRoot.position, camPos);
            }

            if (_statusLabel != null && _statusLabel.gameObject.activeSelf)
            {
                var st = _statusLabel.transform;
                st.position = camPos + cameraTransform.TransformDirection(new Vector3(0f, 0.28f, chromeZ));
                XrUiFacing.FaceUser(st, cameraTransform);
            }
        }

        void UpdateCompass()
        {
            if (_northGroup == null || world == null || cameraTransform == null) return;

            var north = world.Root.TransformDirection(Vector3.forward);
            north.y = 0f;
            if (north.sqrMagnitude < 1e-6f) north = Vector3.forward;
            north.Normalize();

            var east = world.Root.TransformDirection(Vector3.right);
            east.y = 0f;
            if (east.sqrMagnitude < 1e-6f) east = Vector3.right;
            east.Normalize();

            var camFwd = cameraTransform.forward;
            camFwd.y = 0f;
            if (camFwd.sqrMagnitude < 1e-6f) return;
            camFwd.Normalize();

            float bearingRad = Mathf.Atan2(
                north.x * camFwd.z - north.z * camFwd.x,
                Vector3.Dot(north, camFwd));
            _northGroup.localRotation = Quaternion.Euler(0f, bearingRad * Mathf.Rad2Deg, 0f);

            float headingDeg = Mathf.Atan2(Vector3.Dot(camFwd, east), Vector3.Dot(camFwd, north)) * Mathf.Rad2Deg;
            if (headingDeg < 0f) headingDeg += 360f;
            float pitchDeg = Mathf.Asin(Mathf.Clamp(cameraTransform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            ApproximateViewerGeo(out double lat, out double lon, out float altM);

            int h = ((Mathf.RoundToInt(headingDeg) % 360) + 360) % 360;
            string cardinal = Cardinals16[Mathf.RoundToInt(h / 22.5f) % 16];
            int tiltI = Mathf.RoundToInt(pitchDeg);
            string tiltStr = tiltI > 0 ? $"+{tiltI}" : $"{tiltI}";
            string line1 = $"HDG {h:000}° {cardinal}  TILT {tiltStr}°";
            string line2 = $"{lat:F5}  {lon:F5}  ALT {Mathf.RoundToInt(altM)} m";
            string key = line1 + "|" + line2;
            if (key == _lastTelem) return;
            _lastTelem = key;
            if (_telemHdg != null) _telemHdg.text = line1;
            if (_telemGeo != null) _telemGeo.text = line2;
        }

        void ApproximateViewerGeo(out double lat, out double lon, out float altM)
        {
            lat = config != null ? config.originLat : 0;
            lon = config != null ? config.originLon : 0;
            altM = config != null ? (float)config.originAlt : 0f;
            if (world == null || cameraTransform == null || config == null) return;

            var local = world.Root.InverseTransformPoint(cameraTransform.position);
            const double mPerDegLat = 111320.0;
            double mPerDegLon = mPerDegLat * Math.Cos(config.originLat * Math.PI / 180.0);
            if (Math.Abs(mPerDegLon) < 1e-3) mPerDegLon = mPerDegLat;
            lat = config.originLat + local.z / mPerDegLat;
            lon = config.originLon + local.x / mPerDegLon;
            altM = Mathf.Max(0f, Vector3.Dot(
                cameraTransform.position - world.Root.position, world.Root.up));
        }

        void UpdateStatus()
        {
            if (_statusLabel == null) return;
            if (Time.unscaledTime < _statusHideAt) return;
            // Hide flash toast — no persistent tracks / TAK Direct telemetry.
            if (_statusLabel.gameObject.activeSelf)
            {
                _statusLabel.text = "";
                _statusLabel.gameObject.SetActive(false);
            }
        }

        void PollUiSelect()
        {
            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;

                bool pointingAtUi = false;
                if (aimOk)
                {
                    var hits = Physics.RaycastAll(origin, fwd, 8f, ~0, QueryTriggerInteraction.Collide);
                    System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    foreach (var hit in hits)
                    {
                        foreach (var b in _buttons)
                        {
                            if (b.Root == null) continue;
                            if (hit.transform == b.Root || hit.transform.IsChildOf(b.Root))
                            {
                                pointingAtUi = true;
                                if (rising && Time.unscaledTime >= _nextClickTime)
                                {
                                    b.OnClick?.Invoke();
                                    _nextClickTime = Time.unscaledTime + 0.35f;
                                }
                                break;
                            }
                        }
                        if (pointingAtUi) break;
                        // Skip CoT hits when looking for UI — keep searching farther for toolbar.
                        if (hit.transform.GetComponentInParent<CotMarkerView>() != null)
                            continue;
                    }
                }

                XrHandPinchInput.SetUiBlocking(h, pointingAtUi && grabbing);
            }
        }

        static GameObject CreateRingLine(string name, Transform parent, float radius, int segments,
            Color color, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = segments;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.sharedMaterial = MakeLineMat(color);
            lr.startColor = color;
            lr.endColor = color;
            for (int i = 0; i < segments; i++)
            {
                float a = (i / (float)segments) * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Sin(a) * radius, 0.001f, Mathf.Cos(a) * radius));
            }
            return go;
        }

        static GameObject CreateLine(string name, Transform parent, Vector3 a, Vector3 b, Color color, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.sharedMaterial = MakeLineMat(color);
            lr.startColor = color;
            lr.endColor = color;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            return go;
        }

        static Material MakeLineMat(Color c)
        {
            var sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Color");
            var mat = new Material(sh);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            else mat.color = c;
            return mat;
        }

        static GameObject Quad(string name, Transform parent, Vector3 localPos, Vector2 size, Color color,
            int renderQueue = 3000)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            Tint(go, color, opaque: false);
            var r = go.GetComponent<Renderer>();
            if (r != null && r.sharedMaterial != null) r.sharedMaterial.renderQueue = renderQueue;
            return go;
        }

        /// <summary>Opaque URP Unlit quad — cleaner MSAA edges than blended Sprites.</summary>
        static GameObject OpaqueQuad(string name, Transform parent, Vector3 localPos, Vector2 size,
            Color color, int renderQueue = 3000)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            Tint(go, color, opaque: true);
            var r = go.GetComponent<Renderer>();
            if (r != null && r.sharedMaterial != null) r.sharedMaterial.renderQueue = renderQueue;
            return go;
        }

        static TextMesh Text(string name, Transform parent, Vector3 localPos, string msg, float charSize, Color color)
        {
            return XrText.Make(name, parent, localPos, msg, charSize, color);
        }

        static void Tint(GameObject go, Color c, bool opaque = false)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var sh = opaque
                ? (Shader.Find("Universal Render Pipeline/Unlit")
                   ?? Shader.Find("Unlit/Color")
                   ?? Shader.Find("Sprites/Default"))
                : (Shader.Find("Sprites/Default")
                   ?? Shader.Find("Universal Render Pipeline/Unlit")
                   ?? Shader.Find("Unlit/Color"));
            if (sh == null) return;
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", c);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", c);
            else
            {
                try { r.sharedMaterial.color = c; } catch { /* ignore */ }
            }
            if (opaque && r.sharedMaterial.HasProperty("_Surface"))
                r.sharedMaterial.SetFloat("_Surface", 0f);
        }
    }
}
