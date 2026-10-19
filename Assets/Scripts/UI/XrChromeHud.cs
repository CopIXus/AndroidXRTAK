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
    /// Curved 3D Spatial XR HUD for Android XR TAK (matching Stitch Curved Dock aesthetic):
    /// - Curved Bottom Spatial Dock with glowing neon cyan perimeter rim &amp; 3D bevelled cards:
    ///   1. Right Pod (always visible): `<` / `>` Slide Toggle button + Hamburger (`All Tools` in the
    ///      rightmost slot with distinct accent border), and operator Callsign/Team badge directly underneath.
    ///   2. Sliding Curved Quick-Action Bar (`_quickBarRoot`):
    ///      Clicking `<` slides the curved quick-action bar out to the left, pushing the 3D World Globe
    ///      to the left and pushing the Center Compass down below the bar.
    ///      Clicking `>` slides the quick bar back in, pulling the 3D World Globe back to the left of
    ///      the compass and pulling the Compass back up to the center (#1).
    ///   3. Center Compass (#1) + Crisp Drop-Shadowed Telemetry (no dark background box!):
    ///      High-legibility shadowed text directly under the compass.
    ///   4. Re-clicking any active button or Hamburger toggles its panel/tool closed cleanly
    ///      (no intrusive "Collapse Menu" strip covering the options bar).
    /// </summary>
    public class XrChromeHud : MonoBehaviour
    {
        static readonly string[] Cardinals16 =
        {
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"
        };

        const float DockHalfSpan = 0.58f;
        const float DockArchY = 0.018f;
        const float DockWrapZ = 0.052f;

        const float MenuHalfSpan = 0.62f;
        const float MenuArchY = 0.015f;
        const float MenuWrapZ = 0.055f;

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
        TextMesh _telemTak;
        string _lastTelem = "";
        int _drawCycle;

        Transform _dockRoot;
        Transform _dockFullPlate;
        Transform _dockCompactPlate;
        Transform _quickBarRoot;
        Transform _globeHost;
        Transform _slideArrowBtn;
        TextMesh _slideArrowGlyph;
        TextMesh _slideArrowSub;
        Transform _userBadgeRoot;
        TextMesh _userBadgeText;
        Renderer _userLedRend;
        bool _quickBarExpanded;
        float _quickSlideT; // 0 = collapsed, 1 = expanded out to the left

        Transform _trayAnchor;
        TextMesh _dockTiltLabel;
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
        float _nextUserBadgeUpdate;
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
            BuildSpatialDock();
            BuildStatus();
            _statusHideAt = Time.unscaledTime + 4.5f;
            FlashStatus("TAKXR Ready · Tap < to slide out curved dock or ☰ for All Tools");
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

            CreateRingLine("Ring", _compassRoot, 0.044f, 48,
                new Color(0.22f, 0.88f, 1f, 0.88f), 0.0030f);

            // Lubber line = look direction (fixed at far edge / local +Z).
            CreateLine("LookLine", _compassRoot,
                new Vector3(0f, 0.002f, 0.0f),
                new Vector3(0f, 0.002f, 0.058f),
                new Color(1f, 1f, 1f, 0.96f), 0.0032f);
            CreateLine("LookTick", _compassRoot,
                new Vector3(-0.006f, 0.002f, 0.052f),
                new Vector3(0.006f, 0.002f, 0.052f),
                new Color(1f, 1f, 1f, 0.92f), 0.0026f);

            // North group rotates so N sits toward geographic north.
            _northGroup = new GameObject("NorthGroup").transform;
            _northGroup.SetParent(_compassRoot, false);
            CreateLine("NorthNeedle", _northGroup,
                Vector3.zero,
                new Vector3(0f, 0.002f, 0.048f),
                new Color(0.22f, 0.94f, 1f, 0.98f), 0.0036f);
            _northLabel = XrText.MakeWithShadow("N", _northGroup, new Vector3(0f, 0.003f, 0.060f),
                "N", 0.0034f, new Color(1f, 1f, 1f, 1f));

            // Crisp drop-shadowed telemetry directly underneath the Compass (#1) — NO dark box!
            _telemRoot = new GameObject("Telem").transform;
            _telemRoot.SetParent(_compassRoot, false);
            _telemRoot.localPosition = new Vector3(0f, -0.012f, -0.082f);

            _telemHdg = XrText.MakeWithShadow("Hdg", _telemRoot, new Vector3(0f, 0.022f, 0f), "",
                0.0036f, new Color(0.96f, 0.99f, 1f, 1f),
                TextAnchor.MiddleCenter, TextAlignment.Center, maxWidthM: 0.62f);
            _telemGeo = XrText.MakeWithShadow("Geo", _telemRoot, new Vector3(0f, -0.004f, 0f), "",
                0.0031f, new Color(0.84f, 0.95f, 1f, 0.98f),
                TextAnchor.MiddleCenter, TextAlignment.Center, maxWidthM: 0.62f);
            _telemTak = XrText.MakeWithShadow("TakInfo", _telemRoot, new Vector3(0f, -0.028f, 0f), "",
                0.0029f, new Color(0.55f, 0.98f, 0.82f, 0.98f),
                TextAnchor.MiddleCenter, TextAlignment.Center, maxWidthM: 0.62f);
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

            const float dockH = 0.162f;
            var dockFill = new Color(0.05f, 0.10f, 0.15f, 0.93f);
            var dockRim = new Color(0.18f, 0.88f, 0.98f, 0.98f);

            // Curved Full Dock Plate (shown when expanded)
            _dockFullPlate = XrCurvedWidgets.CreateCurvedPanelPlate(
                "DockFullCurvedPlate", _dockRoot, 1.24f, dockH, dockFill, dockRim,
                archHeight: DockArchY, depthWrap: DockWrapZ, renderQueue: 2994);
            _dockFullPlate.localPosition = new Vector3(0.035f, 0f, 0.008f);

            // Curved Compact Right Pod Plate (shown when collapsed)
            _dockCompactPlate = XrCurvedWidgets.CreateCurvedPanelPlate(
                "DockCompactCurvedPlate", _dockRoot, 0.265f, dockH, dockFill, dockRim,
                archHeight: 0.004f, depthWrap: 0.008f, renderQueue: 2994);
            XrCurvedWidgets.EvaluateArc(0.490f, DockHalfSpan, DockArchY, DockWrapZ, out var compactOff, out var compactRot);
            _dockCompactPlate.localPosition = compactOff + new Vector3(0f, 0f, 0.008f);
            _dockCompactPlate.localRotation = compactRot;

            // 3D World Globe: sits to the left of the center compass when collapsed,
            // and slides left when the quick toolbar expands out.
            _globeHost = new GameObject("GlobeWidgetHost").transform;
            _globeHost.SetParent(_dockRoot, false);
            _globeHost.localPosition = new Vector3(-0.26f, 0.01f, -0.02f);
            _miniGlobe = _globeHost.gameObject.AddComponent<XrMiniWorldGlobe>();
            _miniGlobe.Configure(config, feed, cameraTransform);

            var globeCol = _globeHost.gameObject.AddComponent<BoxCollider>();
            globeCol.size = new Vector3(0.18f, 0.18f, 0.18f);
            globeCol.isTrigger = true;
            _buttons.Add(new ToolBtn
            {
                Root = _globeHost,
                Id = "dock_globe3d",
                OnClick = () => _miniGlobe.CycleGlobeMode(FlashStatus),
            });

            // 1. Hamburger ("ALL TOOLS") button in the rightmost slot (where SOS 911 is in the reference image)
            AddCurvedDockButton(_dockRoot, "menu", "hamburger", "ALL TOOLS", 0.548f, isHamburgerSlot: true, onClick: () =>
            {
                if (_menuOpen || IsAnyTrayPanelOpen())
                    CollapseAllMenus(cancelActiveTools: true);
                else
                    SetMenu(true);
            });

            // 2. `<` / `>` Slide Arrow button immediately to the LEFT of the Hamburger menu (x = +0.430f)
            BuildSlideArrowButton(0.430f);

            // 3. Username / Callsign badge sitting directly UNDER the Hamburger menu
            BuildUserBadgeUnderHamburger(0.490f);

            // 4. Sliding Curved Quick-Action Bar (slides out to the left of the `<` button when expanded)
            _quickBarRoot = new GameObject("QuickBarSlideRoot").transform;
            _quickBarRoot.SetParent(_dockRoot, false);

            var quickItems = new (string id, string icon, string label, Action act)[]
            {
                ("q_point", "pointadd", "DROP PIN", () =>
                {
                    bool wasPoint = drawTool != null && drawTool.ActiveMode == XrDrawTool.Mode.Point;
                    CollapseAllMenus(cancelActiveTools: true);
                    if (!wasPoint) drawTool?.ToggleMode(XrDrawTool.Mode.Point);
                }),
                ("q_draw", "route", "DRAW/ROUTE", () =>
                {
                    HideSidePanels();
                    SetMenu(false);
                    CycleDrawingTool();
                }),
                ("q_overlays", "layers", "OVERLAYS", () =>
                {
                    bool wasOpen = overlayManagerPanel != null && overlayManagerPanel.IsVisible;
                    CollapseAllMenus(cancelActiveTools: true);
                    if (!wasOpen) overlayManagerPanel?.Show(cameraTransform);
                }),
                ("q_chat", "chat", "CHAT", () =>
                {
                    bool wasOpen = chatContactsPanel != null && chatContactsPanel.IsVisible;
                    CollapseAllMenus(cancelActiveTools: true);
                    if (!wasOpen) chatContactsPanel?.ShowChat(cameraTransform);
                }),
                ("q_spi", "pointer", "SPI LASER", () =>
                {
                    bool wasSpi = tacticalSuite != null &&
                                  tacticalSuite.ActiveMode == XrTacticalToolsSuite.ToolMode.DigitalPointer;
                    CollapseAllMenus(cancelActiveTools: true);
                    if (!wasSpi) tacticalSuite?.ToggleDigitalPointer();
                }),
                ("q_nav", "quicknav", "NAV 3D", () =>
                {
                    CollapseAllMenus(cancelActiveTools: false);
                    tacticalSuite?.ToggleQuickNav();
                }),
                ("q_tilt", "orientation", "FLAT", CycleGodsEyeTilt),
            };

            const float step = 0.132f;
            float startX = 0.305f - (quickItems.Length - 1) * step; // -0.487f
            for (int i = 0; i < quickItems.Length; i++)
            {
                float bx = startX + i * step;
                var item = quickItems[i];
                AddCurvedDockButton(_quickBarRoot, item.id, item.icon, item.label, bx, isHamburgerSlot: false, onClick: item.act);
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

            // Start collapsed so the Compass sits in the middle (#1) with the Globe to its left
            _quickBarExpanded = false;
            _quickSlideT = 0f;
            ApplyQuickBarSlideLayout(immediate: true);

            BuildMenu();
        }

        void BuildSlideArrowButton(float x)
        {
            XrCurvedWidgets.EvaluateArc(x, DockHalfSpan, DockArchY, DockWrapZ, out var pos, out var rot);
            var cardFill = new Color(0.08f, 0.18f, 0.28f, 0.96f);
            var cardBorder = new Color(0.22f, 0.78f, 0.96f, 0.88f);

            _slideArrowBtn = XrCurvedWidgets.CreateRoundedButtonCard(
                "Dock_slide_arrow", _dockRoot, new Vector2(0.084f, 0.128f), cardFill, cardBorder, 2998);
            _slideArrowBtn.localPosition = pos;
            _slideArrowBtn.localRotation = rot;

            _slideArrowGlyph = XrText.MakeWithShadow("ArrowGlyph", _slideArrowBtn, new Vector3(0f, 0.015f, -0.005f),
                "<", 0.0078f, new Color(0.35f, 0.95f, 1f, 1f),
                TextAnchor.MiddleCenter, TextAlignment.Center, maxWidthM: 0.068f);
            _slideArrowSub = XrText.MakeWithShadow("ArrowSub", _slideArrowBtn, new Vector3(0f, -0.042f, -0.005f),
                "TOOLS", 0.0025f, new Color(0.86f, 0.95f, 1f, 0.96f),
                TextAnchor.MiddleCenter, TextAlignment.Center, maxWidthM: 0.072f);

            var col = _slideArrowBtn.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.092f, 0.136f, 0.08f);
            col.isTrigger = true;
            _buttons.Add(new ToolBtn
            {
                Root = _slideArrowBtn,
                Id = "dock_slide_arrow",
                OnClick = ToggleQuickBarExpanded,
            });
        }

        void BuildUserBadgeUnderHamburger(float x)
        {
            XrCurvedWidgets.EvaluateArc(x, DockHalfSpan, DockArchY, DockWrapZ, out var arcPos, out var arcRot);
            _userBadgeRoot = XrCurvedWidgets.CreateRoundedButtonCard(
                "UserBadgeUnderHamburger", _dockRoot, new Vector2(0.240f, 0.042f),
                new Color(0.04f, 0.09f, 0.15f, 0.94f),
                new Color(0.18f, 0.78f, 0.95f, 0.80f), 2997);
            _userBadgeRoot.localPosition = arcPos + new Vector3(0f, -0.106f, -0.004f);
            _userBadgeRoot.localRotation = arcRot;

            var led = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            led.name = "TakLed";
            Destroy(led.GetComponent<Collider>());
            led.transform.SetParent(_userBadgeRoot, false);
            led.transform.localPosition = new Vector3(-0.098f, 0f, -0.004f);
            led.transform.localScale = Vector3.one * 0.014f;
            _userLedRend = led.GetComponent<Renderer>();
            if (_userLedRend != null)
                _userLedRend.sharedMaterial = Cot3dModelFactory.GetLitMaterial(new Color(0.2f, 0.95f, 0.5f, 1f), 0.8f);

            _userBadgeText = XrText.MakeWithShadow("UserLbl", _userBadgeRoot, new Vector3(0.008f, 0f, -0.005f),
                "XR-USER", 0.0032f, new Color(0.94f, 0.99f, 1f, 1f),
                TextAnchor.MiddleCenter, TextAlignment.Center, maxWidthM: 0.195f, maxHeightM: 0.034f);
        }

        void ToggleQuickBarExpanded()
        {
            _quickBarExpanded = !_quickBarExpanded;
            if (_slideArrowGlyph != null)
                _slideArrowGlyph.text = _quickBarExpanded ? ">" : "<";
            if (_slideArrowSub != null)
                _slideArrowSub.text = _quickBarExpanded ? "HIDE" : "TOOLS";
        }

        void ApplyQuickBarSlideLayout(bool immediate = false)
        {
            float target = _quickBarExpanded ? 1f : 0f;
            if (immediate)
                _quickSlideT = target;
            else
                _quickSlideT = Mathf.MoveTowards(_quickSlideT, target, Time.unscaledDeltaTime * 5.5f);

            float s = Mathf.SmoothStep(0f, 1f, _quickSlideT);

            // 1. Slide & scale QuickBar out to the left from the `<` button (x = +0.36f)
            if (_quickBarRoot != null)
            {
                bool active = s > 0.03f;
                if (_quickBarRoot.gameObject.activeSelf != active)
                    _quickBarRoot.gameObject.SetActive(active);
                if (active)
                {
                    _quickBarRoot.localPosition = new Vector3(Mathf.Lerp(0.36f, 0f, s), 0f, 0f);
                    _quickBarRoot.localScale = new Vector3(Mathf.Max(0.01f, s), 1f, 1f);
                }
            }

            // 2. Toggle between the compact curved pod plate and the full curved dock plate
            bool showFull = s > 0.15f;
            if (_dockFullPlate != null && _dockFullPlate.gameObject.activeSelf != showFull)
                _dockFullPlate.gameObject.SetActive(showFull);
            if (_dockCompactPlate != null && _dockCompactPlate.gameObject.activeSelf == showFull)
                _dockCompactPlate.gameObject.SetActive(!showFull);

            // 3. Push the 3D World Globe to the left when the toolbar slides out,
            //    and pull it back to the left of the center Compass (#1) when collapsed!
            if (_globeHost != null)
            {
                var collapsedGlobePos = new Vector3(-0.26f, 0.01f, -0.02f);
                var expandedGlobePos = new Vector3(-0.71f, -0.01f, -0.06f);
                _globeHost.localPosition = Vector3.Lerp(collapsedGlobePos, expandedGlobePos, s);
            }
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
            FlashStatus($"Earth Tilt: {modeName}\n(2-hand pinch & lift/lower or Hold B/Y + Stick for custom tilt)");
            UpdateTiltButtonLabel();
        }

        void UpdateTiltButtonLabel()
        {
            if (_dockTiltLabel == null || world == null) return;
            int p = Mathf.RoundToInt(world.WorldPitchDeg);
            _dockTiltLabel.text = p <= 2 ? "FLAT" : $"TILT {p}°";
        }

        void AddCurvedDockButton(
            Transform parent, string id, string iconName, string label, float x,
            bool isHamburgerSlot, Action onClick)
        {
            XrCurvedWidgets.EvaluateArc(x, DockHalfSpan, DockArchY, DockWrapZ, out var arcPos, out var arcRot);

            // Match the Stitch curved dock reference:
            // Standard slots = slate-teal rounded card with subtle cyan border;
            // Rightmost Hamburger slot = rich accent card with glowing border.
            Color fillColor = isHamburgerSlot
                ? new Color(0.24f, 0.11f, 0.14f, 0.96f)
                : new Color(0.08f, 0.15f, 0.22f, 0.96f);
            Color borderColor = isHamburgerSlot
                ? new Color(0.95f, 0.34f, 0.36f, 0.92f)
                : new Color(0.22f, 0.68f, 0.86f, 0.78f);

            var root = XrCurvedWidgets.CreateRoundedButtonCard(
                "Dock_" + id, parent, new Vector2(0.120f, 0.128f), fillColor, borderColor, 2998);
            root.localPosition = arcPos;
            root.localRotation = arcRot;

            MakeIconQuad(root, iconName, 0.064f, new Vector3(0f, 0.018f, -0.004f));
            XrText.MakeWithShadow("Lbl", root, new Vector3(0f, -0.042f, -0.004f), label, 0.0027f,
                isHamburgerSlot ? new Color(1f, 0.88f, 0.88f, 1f) : new Color(0.90f, 0.97f, 1f, 0.98f),
                maxWidthM: 0.108f);

            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.126f, 0.135f, 0.08f);
            col.isTrigger = true;
            _buttons.Add(new ToolBtn { Root = root, OnClick = onClick, Id = id });
        }

        void BuildMenu()
        {
            _menuRoot = new GameObject("Menu").transform;
            _menuRoot.SetParent(transform, false);

            // Curved 3D backing plate with neon cyan perimeter border
            XrCurvedWidgets.CreateCurvedPanelPlate(
                "CurvedMenuPlate", _menuRoot, 1.28f, 0.88f,
                new Color(0.04f, 0.09f, 0.15f, 0.95f),
                new Color(0.18f, 0.88f, 0.98f, 0.98f),
                archHeight: MenuArchY, depthWrap: MenuWrapZ, renderQueue: 2990);

            var header = XrText.MakeWithShadow("Header", _menuRoot, new Vector3(-0.56f, 0.37f, -0.015f),
                "ATAK XR TACTICAL SUITE  ·  36 TOOLS", 0.0044f, new Color(0.92f, 0.99f, 1f, 1f),
                TextAnchor.MiddleLeft, TextAlignment.Left, maxWidthM: 0.92f);

            AddHeaderIconBtn("hdr_settings", "settings", 0.42f, 0.37f, () =>
            {
                CollapseAllMenus(cancelActiveTools: true);
                settingsPanel?.Open(cameraTransform);
            });
            AddHeaderIconBtn("hdr_close", "close", 0.55f, 0.37f, () => CollapseAllMenus(cancelActiveTools: true));

            _menuPageLabel = XrText.MakeWithShadow("Page", _menuRoot, new Vector3(0f, -0.37f, -0.010f),
                "", 0.0038f, new Color(0.84f, 0.95f, 1f, 0.98f));
            AddHeaderIconBtn("page_prev", "pageup", -0.54f, -0.37f, () =>
            {
                if (_menuPage > 0) { _menuPage--; RebuildMenuPage(); }
            });
            AddHeaderIconBtn("page_next", "pagedown", 0.54f, -0.37f, () =>
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
            Tool("plugins", "plugins", "Immerse 360°", () =>
            {
                HideSidePanels();
                tacticalSuite?.ToggleImmersiveGroundView();
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

        void AddHeaderIconBtn(string id, string iconName, float x, float y, Action onClick)
        {
            XrCurvedWidgets.EvaluateArc(x, MenuHalfSpan, MenuArchY, MenuWrapZ, out var arcOff, out var arcRot);
            var root = XrCurvedWidgets.CreateRoundedButtonCard(
                "Hdr_" + id, _menuRoot, new Vector2(0.078f, 0.064f),
                new Color(0.10f, 0.20f, 0.32f, 0.96f),
                new Color(0.22f, 0.78f, 0.96f, 0.85f), 3000);
            root.localPosition = new Vector3(arcOff.x, y + arcOff.y * 0.4f, arcOff.z - 0.004f);
            root.localRotation = arcRot;
            MakeIconQuad(root, iconName, 0.046f, new Vector3(0f, 0f, -0.004f));
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.086f, 0.075f, 0.08f);
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

        // Tools grid: 4 columns × 4 rows per page, arranged along the 3D curved tray surface.
        const int MenuCols = 4;
        const int MenuRows = 4;
        const int MenuPageSize = MenuCols * MenuRows;
        const float TileW = 0.28f;
        const float TileH = 0.145f;
        const float TileGap = 0.012f;

        void AddToolTile(string id, string iconName, string label, int index, Action onClick,
            bool enabled = true)
        {
            int cIdx = index % MenuCols;
            int rIdx = index / MenuCols;
            float originX = -1.5f * (TileW + TileGap);
            float tx = originX + cIdx * (TileW + TileGap);
            float ty = 0.23f - rIdx * (TileH + TileGap);

            XrCurvedWidgets.EvaluateArc(tx, MenuHalfSpan, MenuArchY, MenuWrapZ, out var arcOff, out var arcRot);

            bool isDanger = id == "alert" || id == "casevac" || id == "quit";
            Color fillCol = isDanger
                ? new Color(0.24f, 0.10f, 0.13f, 0.96f)
                : new Color(0.08f, 0.15f, 0.23f, 0.96f);
            Color borderCol = isDanger
                ? new Color(0.95f, 0.34f, 0.36f, 0.88f)
                : new Color(0.20f, 0.72f, 0.92f, 0.82f);

            var root = XrCurvedWidgets.CreateRoundedButtonCard(
                "Tile_" + id, _menuPageRoot != null ? _menuPageRoot : _menuRoot,
                new Vector2(TileW - 0.008f, TileH - 0.006f), fillCol, borderCol, 3000);
            root.localPosition = new Vector3(arcOff.x, ty + arcOff.y * 0.5f, arcOff.z - 0.004f);
            root.localRotation = arcRot;

            float alpha = enabled ? 1f : 0.4f;
            MakeIconQuad(root, iconName, 0.060f, new Vector3(0f, 0.018f, -0.005f), alpha);
            string display = label.Length > 14 ? label.Replace(" ", "\n") : label;
            if (label == "Digital Pointer" || label == "Radio Controls" || label == "Track History"
                || label == "Tabletop 3D" || label == "Drawing Tools" || label == "Data Packages"
                || label == "Lasso Select" || label == "Clear Content" || label == "First Person"
                || label == "Point Drop" || label == "Close Tools" || label == "Import KML/CoT"
                || label == "Geofence 3D" || label == "3D Overlays")
                display = label.Replace(" ", "\n");
            XrText.MakeWithShadow("Label", root, new Vector3(0f, -0.044f, -0.005f), display,
                0.0028f, new Color(0.94f, 0.98f, 1f, 0.98f * alpha), maxWidthM: TileW - 0.028f);

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
            _statusLabel = XrText.MakeWithShadow("Status", transform, Vector3.zero, "",
                0.0046f, new Color(0.94f, 0.99f, 1f, 0.98f),
                TextAnchor.MiddleCenter, TextAlignment.Center, maxWidthM: 0.80f);
            var plate = XrCurvedWidgets.CreateCurvedPanelPlate(
                "ToastBg", _statusLabel.transform, 0.86f, 0.088f,
                new Color(0.04f, 0.09f, 0.15f, 0.92f),
                new Color(0.18f, 0.85f, 0.98f, 0.90f),
                archHeight: 0.006f, depthWrap: 0.018f, renderQueue: 2995);
            plate.localPosition = new Vector3(0f, 0f, 0.006f);
            _statusPlate = plate;
            _statusLabel.gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (cameraTransform == null)
            {
                if (Camera.main != null) cameraTransform = Camera.main.transform;
                else return;
            }

            ApplyQuickBarSlideLayout();
            PlaceHeadLockedChrome();
            UpdateMenuButtonVisual();
            UpdateTiltButtonLabel();
            UpdateUserBadgeUnderHamburger();
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

        void UpdateUserBadgeUnderHamburger()
        {
            if (_userBadgeText == null || Time.unscaledTime < _nextUserBadgeUpdate) return;
            _nextUserBadgeUpdate = Time.unscaledTime + 0.5f;

            bool connected = (direct != null && direct.IsConnected) || _takConnected;
            string cs = TakIdentity.Callsign;
            string team = TakIdentity.Team;
            _userBadgeText.text = $"{cs} [{team}]";

            if (_userLedRend != null)
            {
                Color ledCol = connected
                    ? new Color(0.18f, 0.96f, 0.48f, 1f)
                    : new Color(1f, 0.72f, 0.15f, 1f);
                _userLedRend.sharedMaterial = Cot3dModelFactory.GetLitMaterial(ledCol, 0.8f);
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
                _dockRoot.position = camPos + cameraTransform.TransformDirection(new Vector3(0f, -0.35f, 1.55f));
                _dockRoot.rotation = XrUiFacing.RotationFacingUser(_dockRoot.position, camPos);
            }

            // Integrated Dock Tray Anchor: sits directly above the bottom dock so expanded
            // menus/panels attach to the dock and never collide with or hide underneath it.
            if (_trayAnchor != null)
            {
                _trayAnchor.position = camPos + cameraTransform.TransformDirection(new Vector3(0.03f, 0.18f, 1.54f));
                _trayAnchor.rotation = XrUiFacing.RotationFacingUser(_trayAnchor.position, camPos);
            }

            // Center Compass (#1):
            // - When the quick toolbar is collapsed (s = 0), sits in the middle at y = -0.31f (#1)
            //   with the 3D World Globe just to its left.
            // - When the quick toolbar slides out to the left (s = 1), pushes the Compass (and the
            //   crisp shadowed telemetry underneath it) down below the toolbar to y = -0.49f!
            if (_compassRoot != null)
            {
                float s = Mathf.SmoothStep(0f, 1f, _quickSlideT);
                float compassY = Mathf.Lerp(-0.31f, -0.49f, s);
                _compassRoot.position = camPos + cameraTransform.TransformDirection(new Vector3(0.0f, compassY, 1.44f));
                _compassRoot.rotation = yawOnly;
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
                st.position = camPos + cameraTransform.TransformDirection(new Vector3(0f, 0.30f, 1.65f));
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

            bool connected = (direct != null && direct.IsConnected) || _takConnected;
            int totalTracks = feed != null ? feed.Cots.Count : 0;
            int shown3D = cotLayer != null ? cotLayer.VisibleMarkerCount : 0;
            string netStr = connected ? "TAK ONLINE" : (direct != null ? direct.State.ToUpperInvariant() : "LOCAL");
            string activeTool = "";
            if (drawTool != null && drawTool.ActiveMode != XrDrawTool.Mode.None)
                activeTool = $" · DRAW:{drawTool.ActiveMode}";
            else if (tacticalSuite != null && tacticalSuite.ActiveMode != XrTacticalToolsSuite.ToolMode.None)
                activeTool = $" · {tacticalSuite.ActiveMode}";

            string line1 = $"HDG {h:000}° {cardinal}   ·   EARTH {tiltStr}°";
            string line2 = $"{lat:F5},  {lon:F5}   ·   ALT {Mathf.RoundToInt(altM)} m";
            string line3 = $"{netStr}   ·   CoTs {shown3D}/{totalTracks}{activeTool}";
            string key = line1 + "|" + line2 + "|" + line3;
            if (key == _lastTelem) return;
            _lastTelem = key;
            if (_telemHdg != null) _telemHdg.text = line1;
            if (_telemGeo != null) _telemGeo.text = line2;
            if (_telemTak != null) _telemTak.text = line3;
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
    }
}
