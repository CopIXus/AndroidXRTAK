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
    /// Modern Stitch-inspired spatial XR HUD for Android XR TAK:
    /// - Curved Bottom Spatial Dock with:
    ///   1. Left Wing: Interactive 3D World Globe (<see cref="XrMiniWorldGlobe"/>) showing live 3D CoT blips
    ///   2. Center Bar: 8 ergonomic Quick-Action buttons (Drop Pt, Draw/Rte, Overlays, GeoChat,
    ///      SPI Laser, Nav 3D, Tilt View, All Tools)
    ///   3. Right Wing: 3D North Compass Rose &amp; live HDG / Lat-Lon-Alt telemetry
    /// - Integrated Expandable Tray above the Bottom Dock:
    ///   Clicking any tool/menu button expands the bottom dock upward into a unified tray
    ///   (housing Overlays, GeoChat, Channels, Maps, Servers, Settings, Info, Video, or the
    ///   All Tools grid) with a 1-tap "▼ Collapse Menu" bar to fold everything back down cleanly.
    /// - Top Tactical Status Pill: Callsign/Team badge, TAK TLS status, 3D track count,
    ///   Immerse 360°, Wall Tilt, 3D Demo, and 911/Medevac buttons.
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
        [SerializeField] XrChatContactsPanel chatContactsPanel;
        [SerializeField] XrEmergencyPanel emergencyPanel;
        [SerializeField] XrOverlayManagerPanel overlayManagerPanel;
        [SerializeField] XrTacticalToolsSuite tacticalSuite;

        Transform _compassRoot;
        Transform _northGroup;
        TextMesh _northLabel;
        Transform _telemRoot;
        TextMesh _telemHdg;
        TextMesh _telemGeo;
        string _lastTelem = "";
        int _drawCycle;

        Transform _dockRoot;
        Transform _trayAnchor;
        Transform _collapseStrip;
        TextMesh _collapseStripLabel;
        TextMesh _dockTiltLabel;
        Transform _topStatusPill;
        TextMesh _topStatusText;
        Renderer _topLedRend;
        XrMiniWorldGlobe _miniGlobe;

        Transform _menuRoot;
        Transform _menuPageRoot;
        Transform _menuBtnIcon;
        TextMesh _menuPageLabel;
        bool _menuOpen;
        int _menuPage;
        bool _quitArmed;
        float _quitArmedUntil;
        TextMesh _statusLabel;
        Transform _statusPlate;
        float _statusHideAt;
        readonly List<ToolBtn> _buttons = new List<ToolBtn>();
        readonly List<ToolDef> _toolCatalog = new List<ToolDef>();
        float _nextClickTime;
        float _nextTopPillUpdate;
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
            XrElevationTool elevationToolRef = null,
            XrChatContactsPanel chatContactsRef = null,
            XrEmergencyPanel emergencyRef = null,
            XrOverlayManagerPanel overlayManagerRef = null,
            XrTacticalToolsSuite tacticalSuiteRef = null)
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
            chatContactsPanel = chatContactsRef;
            emergencyPanel = emergencyRef;
            overlayManagerPanel = overlayManagerRef;
            tacticalSuite = tacticalSuiteRef;
        }

        void Start()
        {
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;

            BuildCompass();
            BuildTopStatusPill();
            BuildSpatialDock();
            BuildStatus();
            _statusHideAt = Time.unscaledTime + 5f;
            FlashStatus("TAKXR 3D Ready · Tap 'Tilt View' to stand up & look straight ahead at the Earth");
        }

        void OnDestroy()
        {
            if (XrUiFacing.DockTrayAnchor == _trayAnchor)
                XrUiFacing.DockTrayAnchor = null;
        }

        void BuildCompass()
        {
            _compassRoot = new GameObject("Compass").transform;
            _compassRoot.SetParent(transform, false);

            CreateRingLine("Ring", _compassRoot, 0.040f, 48,
                new Color(0.25f, 0.85f, 1f, 0.78f), 0.0026f);

            // Lubber line = look direction (fixed at far edge / local +Z).
            CreateLine("LookLine", _compassRoot,
                new Vector3(0f, 0.002f, 0.0f),
                new Vector3(0f, 0.002f, 0.054f),
                new Color(1f, 1f, 1f, 0.95f), 0.003f);
            CreateLine("LookTick", _compassRoot,
                new Vector3(-0.006f, 0.002f, 0.048f),
                new Vector3(0f, 0.002f, 0.048f),
                new Color(1f, 1f, 1f, 0.9f), 0.0025f);

            // North group rotates so N sits toward geographic north.
            _northGroup = new GameObject("NorthGroup").transform;
            _northGroup.SetParent(_compassRoot, false);
            CreateLine("NorthNeedle", _northGroup,
                Vector3.zero,
                new Vector3(0f, 0.002f, 0.044f),
                new Color(0.25f, 0.92f, 1f, 0.98f), 0.0034f);
            _northLabel = Text("N", _northGroup, new Vector3(0f, 0.003f, 0.054f),
                "N", 0.0028f, new Color(1f, 1f, 1f, 0.98f));

            _telemRoot = new GameObject("Telem").transform;
            _telemRoot.SetParent(_compassRoot, false);
            _telemRoot.localPosition = new Vector3(0f, -0.002f, -0.072f);
            _telemHdg = XrText.Make("Hdg", _telemRoot, new Vector3(0f, 0.009f, 0f), "",
                0.0019f, new Color(0.88f, 0.97f, 1f, 0.96f),
                TextAnchor.MiddleCenter, TextAlignment.Center);
            _telemGeo = XrText.Make("Geo", _telemRoot, new Vector3(0f, -0.004f, 0f), "",
                0.0016f, new Color(0.72f, 0.88f, 1f, 0.92f),
                TextAnchor.MiddleCenter, TextAlignment.Center);
        }

        void BuildTopStatusPill()
        {
            _topStatusPill = new GameObject("TopStatusPill").transform;
            _topStatusPill.SetParent(transform, false);

            Quad("PillBg", _topStatusPill, new Vector3(0f, 0f, 0.006f), new Vector2(1.16f, 0.068f),
                new Color(0.03f, 0.07f, 0.13f, 0.90f), 2994);
            Quad("PillTrim", _topStatusPill, new Vector3(0f, -0.032f, 0.004f), new Vector2(1.16f, 0.004f),
                new Color(0.15f, 0.82f, 1f, 0.85f), 2996);

            // Status LED sphere
            var led = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            led.name = "TakLed";
            Destroy(led.GetComponent<Collider>());
            led.transform.SetParent(_topStatusPill, false);
            led.transform.localPosition = new Vector3(-0.53f, 0f, -0.004f);
            led.transform.localScale = Vector3.one * 0.020f;
            _topLedRend = led.GetComponent<Renderer>();
            if (_topLedRend != null)
                _topLedRend.sharedMaterial = Cot3dModelFactory.GetLitMaterial(new Color(0.2f, 0.95f, 0.5f, 1f), 0.8f);

            _topStatusText = XrText.Make("TopTelemetry", _topStatusPill, new Vector3(-0.50f, 0f, -0.004f),
                "TAKXR 3D  ·  Initializing…", 0.0038f, new Color(0.90f, 0.97f, 1f, 0.98f),
                TextAnchor.MiddleLeft, TextAlignment.Left, maxWidthM: 0.44f, maxHeightM: 0.044f);

            // Quick Upright God's-Eye Wall Tilt button on top pill
            AddPillButton("top_tilt", new Vector3(0.03f, 0f, -0.002f), new Vector2(0.14f, 0.048f),
                "Wall Tilt", new Color(0.16f, 0.36f, 0.62f, 0.96f), CycleGodsEyeTilt);

            // Quick 1:1 360° Ground Immersion + X-Ray Occlusion button on top pill
            AddPillButton("top_immerse", new Vector3(0.19f, 0f, -0.002f), new Vector2(0.15f, 0.048f),
                "Immerse 360°", new Color(0.08f, 0.48f, 0.36f, 0.96f), () =>
                {
                    tacticalSuite?.ToggleImmersiveGroundView();
                });

            // Quick 3D Demo Spawn button on top pill
            AddPillButton("top_demo3d", new Vector3(0.35f, 0f, -0.002f), new Vector2(0.13f, 0.048f),
                "3D Demo", new Color(0.12f, 0.42f, 0.58f, 0.95f), () =>
                {
                    overlayManagerPanel?.ToggleDemoFormation();
                });

            // Quick 911 / CASEVAC button on top pill
            AddPillButton("top_911", new Vector3(0.495f, 0f, -0.002f), new Vector2(0.14f, 0.048f),
                "911 / MED", new Color(0.68f, 0.14f, 0.16f, 0.96f), () =>
                {
                    bool wasOpen = emergencyPanel != null && emergencyPanel.IsVisible;
                    CollapseAllMenus();
                    if (!wasOpen) emergencyPanel?.ShowEmergency(cameraTransform);
                });
        }

        void AddPillButton(string id, Vector3 localPos, Vector2 size, string label, Color bg, Action onClick)
        {
            var root = new GameObject("PillBtn_" + id).transform;
            root.SetParent(_topStatusPill, false);
            root.localPosition = localPos;
            Quad("Bg", root, Vector3.zero, size, bg, 2998);
            XrText.Make("Lbl", root, new Vector3(0f, 0f, -0.004f), label, 0.0044f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.05f);
            col.isTrigger = true;
            _buttons.Add(new ToolBtn { Root = root, OnClick = onClick, Id = id });
        }

        void BuildSpatialDock()
        {
            _dockRoot = new GameObject("SpatialQuickDock").transform;
            _dockRoot.SetParent(transform, false);

            // Unified Tray Anchor directly above the bottom dock so all sub-panels
            // expand upward from the dock as one cohesive drawer.
            _trayAnchor = new GameObject("DockTrayAnchor").transform;
            _trayAnchor.SetParent(transform, false);
            XrUiFacing.DockTrayAnchor = _trayAnchor;

            // Sleek dark glassmorphic dock plate with cyan top border
            const float dockW = 1.18f;
            const float dockH = 0.155f;
            Quad("DockBg", _dockRoot, new Vector3(0.06f, 0f, 0.008f), new Vector2(dockW, dockH),
                new Color(0.03f, 0.07f, 0.13f, 0.92f), 2994);
            Quad("DockTopAccent", _dockRoot, new Vector3(0.06f, dockH * 0.5f - 0.004f, 0.005f),
                new Vector2(dockW, 0.006f), new Color(0.16f, 0.82f, 1f, 0.95f), 2996);

            // Collapse Bar perched on top edge of dock whenever any panel/drawer is expanded
            _collapseStrip = new GameObject("DockCollapseStrip").transform;
            _collapseStrip.SetParent(_dockRoot, false);
            _collapseStrip.localPosition = new Vector3(0.06f, dockH * 0.5f + 0.034f, -0.002f);
            Quad("CollapseBg", _collapseStrip, Vector3.zero, new Vector2(0.56f, 0.048f),
                new Color(0.10f, 0.34f, 0.54f, 0.97f), 3004);
            Quad("CollapseAccent", _collapseStrip, new Vector3(0f, 0.022f, -0.001f), new Vector2(0.56f, 0.004f),
                new Color(0.25f, 0.88f, 1f, 0.98f), 3006);
            _collapseStripLabel = XrText.Make("CollapseLbl", _collapseStrip, new Vector3(0f, 0f, -0.005f),
                "▼ COLLAPSE MENU", 0.0042f, Color.white);
            var collapseCol = _collapseStrip.gameObject.AddComponent<BoxCollider>();
            collapseCol.size = new Vector3(0.58f, 0.056f, 0.06f);
            collapseCol.isTrigger = true;
            _buttons.Add(new ToolBtn
            {
                Root = _collapseStrip,
                Id = "dock_collapse",
                OnClick = () =>
                {
                    CollapseAllMenus(cancelActiveTools: true);
                    FlashStatus("Menu collapsed");
                },
            });
            _collapseStrip.gameObject.SetActive(false);

            // Left Wing of Dock: Interactive 3D World Globe showing live CoT locations
            var globeHost = new GameObject("GlobeWidgetHost").transform;
            globeHost.SetParent(_dockRoot, false);
            globeHost.localPosition = new Vector3(-0.62f, 0.01f, 0f);
            _miniGlobe = globeHost.gameObject.AddComponent<XrMiniWorldGlobe>();
            _miniGlobe.Configure(config, feed, cameraTransform);

            var globeCol = globeHost.gameObject.AddComponent<BoxCollider>();
            globeCol.size = new Vector3(0.18f, 0.18f, 0.18f);
            globeCol.isTrigger = true;
            _buttons.Add(new ToolBtn
            {
                Root = globeHost,
                Id = "dock_globe3d",
                OnClick = () => _miniGlobe.CycleGlobeMode(FlashStatus),
            });

            // 8 Ergonomic Quick-Action Buttons across the Spatial Dock
            var quickItems = new (string id, string icon, string label, Action act)[]
            {
                ("q_point", "pointadd", "Drop Pt", () =>
                {
                    CollapseAllMenus();
                    drawTool?.ToggleMode(XrDrawTool.Mode.Point);
                }),
                ("q_draw", "draw", "Draw/Rte", () =>
                {
                    CollapseAllMenus();
                    CycleDrawingTool();
                }),
                ("q_overlays", "layers", "Overlays", () =>
                {
                    bool wasOpen = overlayManagerPanel != null && overlayManagerPanel.IsVisible;
                    CollapseAllMenus();
                    if (!wasOpen) overlayManagerPanel?.Show(cameraTransform);
                }),
                ("q_chat", "chat", "GeoChat", () =>
                {
                    bool wasOpen = chatContactsPanel != null && chatContactsPanel.IsVisible;
                    CollapseAllMenus();
                    if (!wasOpen) chatContactsPanel?.ShowChat(cameraTransform);
                }),
                ("q_spi", "pointer", "SPI Laser", () =>
                {
                    CollapseAllMenus();
                    tacticalSuite?.ToggleDigitalPointer();
                }),
                ("q_nav", "quicknav", "Nav 3D", () =>
                {
                    CollapseAllMenus();
                    tacticalSuite?.ToggleQuickNav();
                }),
                ("q_tilt", "orientation", "Tilt View", CycleGodsEyeTilt),
                ("menu", "hamburger", "All Tools", () =>
                {
                    if (_menuOpen || IsAnyTrayPanelOpen())
                        CollapseAllMenus();
                    else
                        SetMenu(true);
                }),
            };

            const float step = 0.140f;
            float startX = -0.43f;
            for (int i = 0; i < quickItems.Length; i++)
            {
                float bx = startX + i * step;
                var item = quickItems[i];
                AddDockButton(item.id, item.icon, item.label, new Vector3(bx, 0f, 0f), item.act);
            }

            foreach (var b in _buttons)
            {
                if (b.Root == null) continue;
                if (b.Id == "menu")
                {
                    var icon = b.Root.Find("Icon");
                    if (icon != null) _menuBtnIcon = icon;
                }
                else if (b.Id == "q_tilt")
                {
                    var lbl = b.Root.Find("Lbl");
                    if (lbl != null) _dockTiltLabel = lbl.GetComponent<TextMesh>();
                }
            }

            BuildMenu();
        }

        void CycleGodsEyeTilt()
        {
            if (locomotion == null) return;
            if (tacticalSuite != null && tacticalSuite.IsTabletopActive)
                tacticalSuite.ToggleTabletopMode();

            float deg = locomotion.CycleTiltPreset();
            string modeName = deg >= 50f
                ? "WALL VIEW (62° Upright — Look Straight Ahead)"
                : deg >= 20f
                    ? "ANGLED DESK VIEW (38° Tilted Command Table)"
                    : "FLAT FLOOR VIEW (0° Top-Down)";
            FlashStatus($"Earth Tilt: {modeName}\n(Hold B/Y + Thumbstick Up/Down for smooth custom tilt)");
            UpdateTiltButtonLabel();
        }

        void UpdateTiltButtonLabel()
        {
            if (_dockTiltLabel == null || world == null) return;
            int p = Mathf.RoundToInt(world.WorldPitchDeg);
            _dockTiltLabel.text = p <= 2 ? "Tilt: 0°" : $"Tilt: {p}°";
        }

        void AddDockButton(string id, string iconName, string label, Vector3 localPos, Action onClick)
        {
            var root = new GameObject("Dock_" + id).transform;
            root.SetParent(_dockRoot, false);
            root.localPosition = localPos;

            bool isMenu = id == "menu";
            Color cellColor = isMenu
                ? new Color(0.14f, 0.38f, 0.62f, 0.96f)
                : new Color(0.08f, 0.15f, 0.24f, 0.95f);

            Quad("CellBg", root, new Vector3(0f, 0f, 0.004f), new Vector2(0.126f, 0.128f), cellColor, 2998);
            MakeIconQuad(root, iconName, 0.068f, new Vector3(0f, 0.018f, -0.003f));
            Text("Lbl", root, new Vector3(0f, -0.044f, -0.003f), label, 0.0029f,
                new Color(0.92f, 0.97f, 1f, 0.98f));

            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.132f, 0.135f, 0.08f);
            col.isTrigger = true;
            _buttons.Add(new ToolBtn { Root = root, OnClick = onClick, Id = id });
        }

        void BuildMenu()
        {
            _menuRoot = new GameObject("Menu").transform;
            _menuRoot.SetParent(transform, false);

            // Centered on local (0,0,0) so it sits cleanly inside _trayAnchor above the bottom dock.
            Quad("PanelBg", _menuRoot, new Vector3(0f, 0f, 0.008f), new Vector2(1.24f, 0.86f),
                new Color(0.03f, 0.07f, 0.13f, 0.96f), 2990);
            Quad("PanelAccent", _menuRoot, new Vector3(0f, 0.424f, 0.005f), new Vector2(1.24f, 0.010f),
                new Color(0.15f, 0.82f, 1f, 0.96f), 2995);

            var header = Text("Header", _menuRoot, new Vector3(-0.56f, 0.37f, -0.002f),
                "ATAK XR TACTICAL SUITE (ALL 36 TOOLS)", 0.0044f, new Color(0.90f, 0.98f, 1f, 0.98f));
            header.anchor = TextAnchor.MiddleLeft;
            header.alignment = TextAlignment.Left;

            AddHeaderIconBtn("hdr_settings", "settings", new Vector3(0.42f, 0.37f, -0.002f), () =>
            {
                CollapseAllMenus();
                settingsPanel?.Open(cameraTransform);
            });
            AddHeaderIconBtn("hdr_close", "close", new Vector3(0.55f, 0.37f, -0.002f), () => CollapseAllMenus());

            _menuPageLabel = Text("Page", _menuRoot, new Vector3(0f, -0.37f, -0.002f),
                "", 0.0036f, new Color(0.80f, 0.92f, 1f, 0.95f));
            AddHeaderIconBtn("page_prev", "pageup", new Vector3(-0.54f, -0.37f, -0.002f), () =>
            {
                if (_menuPage > 0) { _menuPage--; RebuildMenuPage(); }
            });
            AddHeaderIconBtn("page_next", "pagedown", new Vector3(0.54f, -0.37f, -0.002f), () =>
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

            // Complete ATAK & VR-TAK Tool Suite — ALL 36 TOOLS ENABLED & WIRED (Zero Stubs!)
            Tool("alert", "alert", "Alert 911", () =>
            {
                HideSidePanels();
                emergencyPanel?.ShowEmergency(cameraTransform);
            });
            Tool("brightness", "brightness", "Brightness", CycleBrightness);
            Tool("casevac", "casevac", "CASEVAC", () =>
            {
                HideSidePanels();
                emergencyPanel?.ShowCasevac(cameraTransform);
            });
            Tool("chat", "chat", "GeoChat", () =>
            {
                HideSidePanels();
                chatContactsPanel?.ShowChat(cameraTransform);
            });
            Tool("clear", "clear", "Clear Content", ClearAllTools);
            Tool("contacts", "contacts", "Contacts", () =>
            {
                HideSidePanels();
                chatContactsPanel?.ShowContacts(cameraTransform);
            });
            Tool("package", "package", "Data Packages", () =>
            {
                HideSidePanels();
                layersPanel?.Open(XrLayersPanel.Tab.Packages, cameraTransform);
            });
            Tool("datasync", "datasync", "Data Sync", () =>
            {
                HideSidePanels();
                layersPanel?.Open(XrLayersPanel.Tab.Missions, cameraTransform);
            });
            Tool("pointer", "pointer", "Digital Pointer", () =>
            {
                HideSidePanels();
                tacticalSuite?.ToggleDigitalPointer();
            });
            Tool("drawing", "draw", "Drawing Tools", () =>
            {
                HideSidePanels();
                CycleDrawingTool();
            });
            Tool("elevation", "elevation", "Elevation", () =>
            {
                HideSidePanels();
                elevationTool?.Toggle();
            });
            Tool("firstperson", "firstperson", "First Person", ToggleFollowSelected);
            Tool("gallery", "gallery", "3D Overlays", () =>
            {
                HideSidePanels();
                overlayManagerPanel?.Show(cameraTransform);
            });
            Tool("geofence", "geofence", "Geofence 3D", () =>
            {
                HideSidePanels();
                tacticalSuite?.ToggleGeofenceTool();
            });
            Tool("goto", "goto", "GoTo", () =>
            {
                HideSidePanels();
                goToPanel?.Open(cameraTransform);
            });
            Tool("import", "import", "Import KML/CoT", () =>
            {
                overlayManagerPanel?.ImportLocalFiles();
            });
            Tool("lasso", "lasso", "Lasso Select", () =>
            {
                HideSidePanels();
                tacticalSuite?.ToggleLassoTool();
            });
            Tool("linkeud", "plugins", "Link EUD", () =>
            {
                tacticalSuite?.ShowLinkEudStatus();
            });
            Tool("orientation", "orientation", "Tilt / North", () =>
            {
                locomotion?.OrientNorth();
                CycleGodsEyeTilt();
            });
            Tool("plugins", "plugins", "Plugins", () =>
            {
                tacticalSuite?.ShowPluginsStatus();
            });
            Tool("quicknav", "quicknav", "Quick Nav", () =>
            {
                HideSidePanels();
                tacticalSuite?.ToggleQuickNav();
            });
            Tool("quickpic", "quickpic", "Quick Pic", () =>
            {
                tacticalSuite?.CaptureQuickPic();
            });
            Tool("radio", "radio", "Radio Controls", () =>
            {
                tacticalSuite?.ShowRadioStatus();
            });
            Tool("range", "range", "Range Tools", () =>
            {
                HideSidePanels();
                rangeTool?.Toggle();
            });
            Tool("resection", "resection", "Resection", () =>
            {
                HideSidePanels();
                tacticalSuite?.ToggleResectionTool();
            });
            Tool("routes", "route", "Routes", () =>
            {
                HideSidePanels();
                drawTool?.ToggleMode(XrDrawTool.Mode.Route);
            });
            Tool("maps", "map", "Maps", () =>
            {
                HideSidePanels();
                basemapPanel?.Open(cameraTransform);
            });
            Tool("pointdrop", "pointadd", "Point Drop", () =>
            {
                HideSidePanels();
                drawTool?.ToggleMode(XrDrawTool.Mode.Point);
            });
            Tool("rubbersheet", "rubbersheet", "Tabletop 3D", () =>
            {
                HideSidePanels();
                tacticalSuite?.ToggleTabletopMode();
            });
            Tool("tracks", "tracks", "Track History", () => trackHistory?.Toggle());
            Tool("video", "video", "Video", () =>
            {
                HideSidePanels();
                videoBrowser?.Open(cameraTransform);
            });

            // XR Tactical System Section
            Tool("channels", "channels", "Channels", () =>
            {
                HideSidePanels();
                layersPanel?.Open(XrLayersPanel.Tab.Channels, cameraTransform);
            });
            Tool("layers", "layers", "Layers", () =>
            {
                HideSidePanels();
                overlayManagerPanel?.Show(cameraTransform);
            });
            Tool("server", "server", "Servers", () =>
            {
                HideSidePanels();
                serverPanel?.Open(cameraTransform);
            });
            Tool("closeall", "close", "Close Tools", () =>
            {
                CollapseAllMenus(cancelActiveTools: true);
                FlashStatus("All panels & tools closed");
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
                FlashStatus($"3D CoT scale x{s:0.##}");
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

        void RebuildMenuPage()
        {
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
                _menuPageLabel.text = $"Page {_menuPage + 1} / {maxPage + 1}";

            int start = _menuPage * MenuPageSize;
            for (int i = 0; i < MenuPageSize; i++)
            {
                int idx = start + i;
                if (idx >= _toolCatalog.Count) break;
                var t = _toolCatalog[idx];
                AddToolTile(t.Id, t.Icon, t.Label, i, t.OnClick, t.Enabled);
            }
        }

        public bool IsAnyTrayPanelOpen()
        {
            if (_menuOpen) return true;
            if (layersPanel != null && layersPanel.IsVisible) return true;
            if (basemapPanel != null && basemapPanel.IsVisible) return true;
            if (serverPanel != null && serverPanel.IsVisible) return true;
            if (settingsPanel != null && settingsPanel.IsVisible) return true;
            if (goToPanel != null && goToPanel.IsVisible) return true;
            if (videoBrowser != null && videoBrowser.IsVisible) return true;
            if (chatContactsPanel != null && chatContactsPanel.IsVisible) return true;
            if (emergencyPanel != null && emergencyPanel.IsVisible) return true;
            if (overlayManagerPanel != null && overlayManagerPanel.IsVisible) return true;
            if (infoPanel != null && infoPanel.IsVisible) return true;
            if (videoPanel != null && videoPanel.IsVisible) return true;
            if (drawTool != null && drawTool.ActiveMode != XrDrawTool.Mode.None) return true;
            if (tacticalSuite != null && tacticalSuite.ActiveMode != XrTacticalToolsSuite.ToolMode.None) return true;
            if (rangeTool != null && XrRangeMeasureTool.IsArmed) return true;
            if (elevationTool != null && XrElevationTool.IsArmed) return true;
            return false;
        }

        string ActiveTrayTitle()
        {
            if (_menuOpen) return "▼ COLLAPSE ALL TOOLS";
            if (overlayManagerPanel != null && overlayManagerPanel.IsVisible) return "▼ COLLAPSE OVERLAYS";
            if (chatContactsPanel != null && chatContactsPanel.IsVisible) return "▼ COLLAPSE GEOCHAT / CONTACTS";
            if (emergencyPanel != null && emergencyPanel.IsVisible) return "▼ COLLAPSE 911 / CASEVAC";
            if (layersPanel != null && layersPanel.IsVisible) return "▼ COLLAPSE CHANNELS / PACKAGES";
            if (basemapPanel != null && basemapPanel.IsVisible) return "▼ COLLAPSE MAPS";
            if (serverPanel != null && serverPanel.IsVisible) return "▼ COLLAPSE SERVERS";
            if (settingsPanel != null && settingsPanel.IsVisible) return "▼ COLLAPSE SETTINGS";
            if (goToPanel != null && goToPanel.IsVisible) return "▼ COLLAPSE GOTO";
            if (videoBrowser != null && videoBrowser.IsVisible) return "▼ COLLAPSE VIDEO LIST";
            if (videoPanel != null && videoPanel.IsVisible) return "▼ COLLAPSE VIDEO PLAYER";
            if (infoPanel != null && infoPanel.IsVisible) return "▼ COLLAPSE DETAILS";
            if (drawTool != null && drawTool.ActiveMode != XrDrawTool.Mode.None) return "▼ FINISH / CLOSE DRAW TOOL";
            if (tacticalSuite != null && tacticalSuite.ActiveMode != XrTacticalToolsSuite.ToolMode.None) return "▼ CLOSE ACTIVE TOOL";
            return "▼ COLLAPSE MENU";
        }

        public void CollapseAllMenus(bool cancelActiveTools = false)
        {
            SetMenu(false);
            HideSidePanels();
            if (cancelActiveTools)
            {
                drawTool?.Cancel();
                rangeTool?.Cancel();
                tacticalSuite?.CancelActiveTool();
                if (elevationTool != null && XrElevationTool.IsArmed)
                    elevationTool.Toggle();
            }
        }

        public void HideSidePanels()
        {
            layersPanel?.Hide();
            basemapPanel?.Hide();
            serverPanel?.Hide();
            settingsPanel?.Hide();
            goToPanel?.Hide();
            videoBrowser?.Hide();
            chatContactsPanel?.Hide();
            emergencyPanel?.Hide();
            overlayManagerPanel?.Hide();
            infoPanel?.Hide();
            videoPanel?.Hide();
            radialMenu?.Hide();
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
            MakeIconQuad(root, iconName, 0.052f, new Vector3(0f, 0f, -0.002f));
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.085f, 0.085f, 0.08f);
            col.isTrigger = true;
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
                FlashStatus("Select a 3D CoT → Follow");
                return;
            }
            follow?.SetFollow(uid);
            FlashStatus("Following " + uid);
        }

        void ClearAllTools()
        {
            CollapseAllMenus(cancelActiveTools: true);
            tacticalSuite?.StopQuickNav();
            tacticalSuite?.ClearAllGeofences();
            radialMenu?.ClearRangeBearing();
            trackHistory?.Clear();
            if (feed != null)
            {
                var kill = new List<string>();
                foreach (var kv in feed.Cots)
                {
                    if (kv.Key != null && kv.Key.StartsWith("takxr.", StringComparison.Ordinal))
                        kill.Add(kv.Key);
                }
                foreach (var uid in kill)
                {
                    if (feed.Cots.TryGetValue(uid, out var cot) && direct != null)
                    {
                        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
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
            FlashStatus("Cleared local drawings & tools");
        }

        void SetMenu(bool open)
        {
            if (open)
                HideSidePanels();
            _menuOpen = open;
            if (_menuRoot != null) _menuRoot.gameObject.SetActive(open);
            UpdateMenuButtonVisual();
        }

        void UpdateMenuButtonVisual()
        {
            bool anyOpen = IsAnyTrayPanelOpen();
            if (_menuBtnIcon != null)
            {
                var r = _menuBtnIcon.GetComponent<Renderer>();
                if (r != null && r.sharedMaterial != null)
                {
                    var tex = AtakToolbarIcons.Get(anyOpen ? "menuopen" : "hamburger");
                    r.sharedMaterial.mainTexture = tex;
                    if (r.sharedMaterial.HasProperty("_BaseMap"))
                        r.sharedMaterial.SetTexture("_BaseMap", tex);
                }
            }
            if (_collapseStrip != null)
            {
                if (_collapseStrip.gameObject.activeSelf != anyOpen)
                    _collapseStrip.gameObject.SetActive(anyOpen);
                if (anyOpen && _collapseStripLabel != null)
                    _collapseStripLabel.text = ActiveTrayTitle();
            }
        }

        public void FlashStatus(string msg)
        {
            bool longMsg = !string.IsNullOrEmpty(msg) &&
                           (msg.Length > 40 || msg.IndexOf('\n') >= 0 ||
                            msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            msg.IndexOf("TCP", StringComparison.OrdinalIgnoreCase) >= 0);
            _statusHideAt = Time.unscaledTime + (longMsg ? 7.5f : 3.5f);
            if (_statusLabel != null)
            {
                _statusLabel.gameObject.SetActive(true);
                _statusLabel.text = msg ?? "";
                if (_statusPlate != null) _statusPlate.gameObject.SetActive(true);
            }
        }

        // Tools grid: 4 columns × 4 rows per page, sized to sit cleanly inside the dock tray.
        const int MenuCols = 4;
        const int MenuRows = 4;
        const int MenuPageSize = MenuCols * MenuRows;
        const float TileW = 0.28f;
        const float TileH = 0.145f;
        const float TileGap = 0.010f;

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
                0.23f - rIdx * (TileH + TileGap),
                0f);

            OpaqueQuad("Bg", root, new Vector3(0f, 0f, 0.004f), new Vector2(TileW - 0.008f, TileH - 0.006f),
                new Color(0.08f, 0.14f, 0.22f, 1f), 3000);

            float alpha = enabled ? 1f : 0.4f;
            MakeIconQuad(root, iconName, 0.062f, new Vector3(0f, 0.018f, -0.004f), alpha);
            string display = label.Length > 14 ? label.Replace(" ", "\n") : label;
            if (label == "Digital Pointer" || label == "Radio Controls" || label == "Track History"
                || label == "Tabletop 3D" || label == "Drawing Tools" || label == "Data Packages"
                || label == "Lasso Select" || label == "Clear Content" || label == "First Person"
                || label == "Point Drop" || label == "Close Tools" || label == "Import KML/CoT"
                || label == "Geofence 3D" || label == "3D Overlays")
                display = label.Replace(" ", "\n");
            Text("Label", root, new Vector3(0f, -0.045f, -0.004f), display,
                0.0029f, new Color(1f, 1f, 1f, 0.96f * alpha));

            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(TileW + 0.005f, TileH + 0.005f, 0.08f);
            col.isTrigger = true;
            _buttons.Add(new ToolBtn
            {
                Root = root,
                OnClick = () => { if (enabled) SetMenu(false); onClick?.Invoke(); },
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
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            mat.renderQueue = 3010;
            r.sharedMaterial = mat;
        }

        void BuildStatus()
        {
            _statusLabel = XrText.Make("Status", transform, Vector3.zero, "",
                0.0048f, new Color(0.92f, 0.98f, 1f, 0.96f),
                TextAnchor.MiddleCenter, TextAlignment.Center);
            var plate = Quad("ToastBg", _statusLabel.transform, new Vector3(0f, 0f, 0.005f),
                new Vector2(0.84f, 0.085f), new Color(0.03f, 0.07f, 0.13f, 0.88f), 2995);
            _statusPlate = plate.transform;
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
            UpdateMenuButtonVisual();
            UpdateTiltButtonLabel();
            UpdateCompass();
            BillboardCompassText();
            UpdateTopStatusPill();
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

        void UpdateTopStatusPill()
        {
            if (_topStatusText == null || Time.unscaledTime < _nextTopPillUpdate) return;
            _nextTopPillUpdate = Time.unscaledTime + 0.5f;

            bool connected = (direct != null && direct.IsConnected) || _takConnected;
            int totalTracks = feed != null ? feed.Cots.Count : 0;
            int shown3D = cotLayer != null ? cotLayer.VisibleMarkerCount : 0;
            string cs = TakIdentity.Callsign;
            string team = TakIdentity.Team;

            string activeTool = "";
            if (drawTool != null && drawTool.ActiveMode != XrDrawTool.Mode.None)
                activeTool = $"  ·  [DRAW: {drawTool.ActiveMode}]";
            else if (tacticalSuite != null && tacticalSuite.ActiveMode != XrTacticalToolsSuite.ToolMode.None)
                activeTool = $"  ·  [TOOL: {tacticalSuite.ActiveMode}]";
            else if (tacticalSuite != null && tacticalSuite.IsTabletopActive)
                activeTool = "  ·  [3D TABLETOP]";
            else if (world != null && Mathf.Abs(world.WorldPitchDeg) >= 5f)
                activeTool = $"  ·  [TILT {Mathf.RoundToInt(world.WorldPitchDeg)}°]";

            string netStr = connected ? "TAK ONLINE" : (direct != null ? direct.State.ToUpperInvariant() : "LOCAL");
            _topStatusText.text = $"{cs} [{team}]  ·  {netStr}  ·  3D CoTs: {shown3D}/{totalTracks}{activeTool}";

            if (_topLedRend != null)
            {
                Color ledCol = connected
                    ? new Color(0.18f, 0.96f, 0.48f, 1f)
                    : new Color(1f, 0.72f, 0.15f, 1f);
                _topLedRend.sharedMaterial = Cot3dModelFactory.GetLitMaterial(ledCol, 0.8f);
            }
        }

        void BillboardCompassText()
        {
            if (cameraTransform == null) return;
            var camPos = cameraTransform.position;
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

            // Spatial Bottom Dock: sits comfortably in lower FOV at 1.55m
            if (_dockRoot != null)
            {
                _dockRoot.position = camPos + cameraTransform.TransformDirection(new Vector3(0f, -0.36f, 1.55f));
                _dockRoot.rotation = XrUiFacing.RotationFacingUser(_dockRoot.position, camPos);
            }

            // Integrated Dock Tray Anchor: sits directly above the bottom dock so expanded
            // menus/panels attach to the dock and never collide with or hide underneath it.
            if (_trayAnchor != null)
            {
                _trayAnchor.position = camPos + cameraTransform.TransformDirection(new Vector3(0.04f, 0.16f, 1.55f));
                _trayAnchor.rotation = XrUiFacing.RotationFacingUser(_trayAnchor.position, camPos);
            }

            // Right Wing of Dock: 3D North Compass Rose
            if (_compassRoot != null)
            {
                _compassRoot.position = camPos + cameraTransform.TransformDirection(new Vector3(0.56f, -0.30f, 1.32f));
                _compassRoot.rotation = yawOnly;
            }

            // Top Status & Telemetry Pill
            if (_topStatusPill != null)
            {
                _topStatusPill.position = camPos + cameraTransform.TransformDirection(new Vector3(0f, 0.42f, 1.85f));
                _topStatusPill.rotation = XrUiFacing.RotationFacingUser(_topStatusPill.position, camPos);
            }

            // All Tools Drawer (anchored in the integrated dock tray)
            if (_menuRoot != null && _menuOpen && _trayAnchor != null)
            {
                _menuRoot.position = _trayAnchor.position;
                _menuRoot.rotation = _trayAnchor.rotation;
            }

            // Status Toast Banner
            if (_statusLabel != null && _statusLabel.gameObject.activeSelf)
            {
                var st = _statusLabel.transform;
                st.position = camPos + cameraTransform.TransformDirection(new Vector3(0f, 0.31f, 1.65f));
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
            int worldTilt = Mathf.RoundToInt(world.WorldPitchDeg);
            ApproximateViewerGeo(out double lat, out double lon, out float altM);

            int h = ((Mathf.RoundToInt(headingDeg) % 360) + 360) % 360;
            string cardinal = Cardinals16[Mathf.RoundToInt(h / 22.5f) % 16];
            string tiltStr = worldTilt > 0 ? $"+{worldTilt}" : $"{worldTilt}";
            string line1 = $"HDG {h:000}° {cardinal}  EARTH {tiltStr}°";
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
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    foreach (var hit in hits)
                    {
                        foreach (var b in _buttons)
                        {
                            if (b.Root == null || !b.Root.gameObject.activeInHierarchy) continue;
                            if (hit.transform == b.Root || hit.transform.IsChildOf(b.Root))
                            {
                                pointingAtUi = true;
                                if (rising && Time.unscaledTime >= _nextClickTime)
                                {
                                    b.OnClick?.Invoke();
                                    _nextClickTime = Time.unscaledTime + 0.32f;
                                }
                                break;
                            }
                        }
                        if (pointingAtUi) break;
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
