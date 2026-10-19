using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK Overlay Manager &amp; Local File Importer (`Import .cot / .kml`):
    /// - Category visibility toggles for 3D Units/Teams, 3D Aircraft, 3D Video Pods,
    ///   3D XR Observers, 3D Extruded Waypoints/Icons, and Data Packages.
    /// - Searchable/paginated entity directory with per-UID Show/Hide and GoTo.
    /// - Local `.cot`, `.xml`, and `.kml` scanner &amp; importer (`persistentDataPath` + `streamingAssetsPath`).
    /// - 1-tap 3D Tactical Demo Formation generator (spawns 3D jets, spinning helicopters,
    ///   team balls, video pods, observer pawns, and extruded 2525 icons around map origin).
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class XrOverlayManagerPanel : MonoBehaviour
    {
        const float PanelW = 1.10f;
        const float PanelH = 0.80f;
        const int ItemsPerPage = 5;

        AppConfig _config;
        CotFeedClient _feed;
        CotLayerController _cotLayer;
        XrLayersPanel _packagePanel;
        XrWorldRoot _world;
        DemTerrainMap _terrain;
        Transform _cam;
        Action<string> _flash;

        Transform _root;
        Transform _contentRoot;
        int _page;
        bool _demoSpawned;
        readonly List<string> _demoUids = new List<string>();

        readonly List<(Transform root, Action onClick)> _hitTargets = new List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        readonly bool[] _blockedByMe = new bool[2];
        float _nextClickTime;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrOverlayManagerPanel Create()
        {
            var go = new GameObject("XrOverlayManagerPanel");
            return go.AddComponent<XrOverlayManagerPanel>();
        }

        public void Configure(
            AppConfig config,
            CotFeedClient feed,
            CotLayerController cotLayer,
            XrLayersPanel packagePanel,
            XrWorldRoot world,
            DemTerrainMap terrain,
            Transform cam,
            Action<string> flash)
        {
            _config = config;
            _feed = feed;
            _cotLayer = cotLayer;
            _packagePanel = packagePanel;
            _world = world;
            _terrain = terrain;
            _cam = cam;
            _flash = flash;
        }

        void Awake()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.04f, 0.08f, 0.14f, 0.95f), 2990);
            Quad("TopBar", _root, new Vector3(0f, PanelH / 2f - 0.008f, 0.005f),
                new Vector2(PanelW, 0.014f), new Color(0.20f, 0.85f, 0.65f, 0.96f), 2995);

            _contentRoot = new GameObject("Content").transform;
            _contentRoot.SetParent(_root, false);
        }

        public void Show(Transform cam = null)
        {
            if (cam != null) _cam = cam;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildUi();
        }

        public void Toggle(Transform cam = null)
        {
            if (IsVisible) Hide();
            else Show(cam);
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            for (int h = 0; h < 2; h++)
            {
                if (_blockedByMe[h]) XrHandPinchInput.SetUiBlocking(h, false);
                _blockedByMe[h] = false;
            }
        }

        void PlaceFacing()
        {
            XrUiFacing.PlaceInDockTray(_root, _cam, new Vector3(0f, 0.14f, 1.55f));
        }

        void RebuildUi()
        {
            foreach (Transform child in _contentRoot) Destroy(child.gameObject);
            _hitTargets.Clear();

            var titleTm = Text("Title", _contentRoot, new Vector3(-PanelW / 2f + 0.045f, PanelH / 2f - 0.048f, -0.01f),
                "ATAK OVERLAY MANAGER  ·  3D LAYERS & IMPORT",
                0.0072f, new Color(0.88f, 0.98f, 0.94f, 1f), TextAnchor.MiddleLeft, TextAlignment.Left);
            XrText.SetMaxBounds(titleTm, PanelW - 0.22f, 0.044f);

            AddButton("Close", new Vector3(PanelW / 2f - 0.055f, PanelH / 2f - 0.048f, -0.01f),
                new Vector2(0.068f, 0.048f), "X", false, Hide);

            if (_cotLayer == null) return;

            // Row 1 & 2: 6 Category Filter Toggles
            float y = PanelH / 2f - 0.115f;
            AddButton("F_Units", new Vector3(-0.34f, y, -0.01f), new Vector2(0.31f, 0.048f),
                $"3D Units/Balls: {(_cotLayer.ShowUnits ? "ON" : "OFF")}", _cotLayer.ShowUnits, () =>
                {
                    _cotLayer.ShowUnits = !_cotLayer.ShowUnits;
                    _cotLayer.ForceSync();
                    RebuildUi();
                });
            AddButton("F_Air", new Vector3(0f, y, -0.01f), new Vector2(0.31f, 0.048f),
                $"3D Aircraft: {(_cotLayer.ShowAircraft ? "ON" : "OFF")}", _cotLayer.ShowAircraft, () =>
                {
                    _cotLayer.ShowAircraft = !_cotLayer.ShowAircraft;
                    _cotLayer.ForceSync();
                    RebuildUi();
                });
            AddButton("F_Video", new Vector3(0.34f, y, -0.01f), new Vector2(0.31f, 0.048f),
                $"3D Video Pods: {(_cotLayer.ShowVideo ? "ON" : "OFF")}", _cotLayer.ShowVideo, () =>
                {
                    _cotLayer.ShowVideo = !_cotLayer.ShowVideo;
                    _cotLayer.ForceSync();
                    RebuildUi();
                });

            y -= 0.058f;
            AddButton("F_Obs", new Vector3(-0.34f, y, -0.01f), new Vector2(0.31f, 0.048f),
                $"3D Observers: {(_cotLayer.ShowObservers ? "ON" : "OFF")}", _cotLayer.ShowObservers, () =>
                {
                    _cotLayer.ShowObservers = !_cotLayer.ShowObservers;
                    _cotLayer.ForceSync();
                    RebuildUi();
                });
            AddButton("F_Wpt", new Vector3(0f, y, -0.01f), new Vector2(0.31f, 0.048f),
                $"3D Icons/2525: {(_cotLayer.ShowWaypoints ? "ON" : "OFF")}", _cotLayer.ShowWaypoints, () =>
                {
                    _cotLayer.ShowWaypoints = !_cotLayer.ShowWaypoints;
                    _cotLayer.ForceSync();
                    RebuildUi();
                });
            AddButton("F_Pkg", new Vector3(0.34f, y, -0.01f), new Vector2(0.31f, 0.048f),
                $"Packages: {(_cotLayer.ShowPackages ? "ON" : "OFF")}", _cotLayer.ShowPackages, () =>
                {
                    _cotLayer.ShowPackages = !_cotLayer.ShowPackages;
                    _cotLayer.ForceSync();
                    RebuildUi();
                });

            // Row 3: Import / Server Packages / Server Channels
            y -= 0.062f;
            AddButton("ImportBtn", new Vector3(-0.34f, y, -0.01f), new Vector2(0.31f, 0.048f),
                "Import .cot / .kml", true, ImportLocalFiles);
            AddButton("MartiPkg", new Vector3(0f, y, -0.01f), new Vector2(0.31f, 0.048f),
                "Marti Data Packages", false, () =>
                {
                    Hide();
                    _packagePanel?.Open(XrLayersPanel.Tab.Packages, _cam);
                });
            AddButton("MartiCh", new Vector3(0.34f, y, -0.01f), new Vector2(0.31f, 0.048f),
                "Marti Channels", false, () =>
                {
                    Hide();
                    _packagePanel?.Open(XrLayersPanel.Tab.Channels, _cam);
                });

            // Entity List
            y -= 0.055f;
            var all = new List<NormalizedCot>();
            if (_feed != null)
            {
                foreach (var c in _feed.Cots.Values)
                    if (c != null && !string.IsNullOrEmpty(c.uid) && !c.IsChatMessage)
                        all.Add(c);
            }
            all.Sort((a, b) => string.Compare(a.Callsign, b.Callsign, StringComparison.OrdinalIgnoreCase));

            int totalPages = Mathf.Max(1, Mathf.CeilToInt(all.Count / (float)ItemsPerPage));
            _page = Mathf.Clamp(_page, 0, totalPages - 1);

            Text("ListHdr", _contentRoot, new Vector3(-PanelW / 2f + 0.045f, y, -0.01f),
                $"OVERLAY ENTITIES ({all.Count} total, {_cotLayer.VisibleMarkerCount} rendered in 3D)  ·  Page {_page + 1}/{totalPages}",
                0.0064f, new Color(0.68f, 0.88f, 0.96f, 0.95f), TextAnchor.MiddleLeft, TextAlignment.Left);

            y -= 0.052f;
            int start = _page * ItemsPerPage;
            int end = Mathf.Min(all.Count, start + ItemsPerPage);
            for (int i = start; i < end; i++)
            {
                var c = all[i];
                var kind = CotClassifier.Classify(c);
                bool hidden = _cotLayer.IsUidHidden(c.uid);

                Quad("Row_" + i, _contentRoot, new Vector3(0f, y, 0.002f),
                    new Vector2(PanelW - 0.07f, 0.060f), new Color(0.06f, 0.12f, 0.19f, 0.92f), 2996);

                Color badgeCol = CotMarkerView.GetAffiliationOrTeamColor(c);
                Quad("Badge_" + i, _contentRoot, new Vector3(-PanelW / 2f + 0.065f, y, -0.004f),
                    new Vector2(0.024f, 0.038f), badgeCol, 3001);

                var nameTm = Text("Name_" + i, _contentRoot, new Vector3(-PanelW / 2f + 0.095f, y + 0.011f, -0.01f),
                    c.Callsign ?? c.uid, 0.0062f, hidden ? new Color(0.5f, 0.55f, 0.6f) : Color.white,
                    TextAnchor.MiddleLeft, TextAlignment.Left);
                XrText.SetMaxBounds(nameTm, 0.52f, 0.030f);
                var kindTm = Text("Kind_" + i, _contentRoot, new Vector3(-PanelW / 2f + 0.095f, y - 0.013f, -0.01f),
                    $"3D {kind}  ·  {c.type}  ·  {c.point?.lat:F4}, {c.point?.lon:F4}",
                    0.0048f, new Color(0.65f, 0.82f, 0.94f, 0.90f), TextAnchor.MiddleLeft, TextAlignment.Left);
                XrText.SetMaxBounds(kindTm, 0.52f, 0.026f);

                var captured = c;
                AddButton("Vis_" + i, new Vector3(0.18f, y, -0.01f), new Vector2(0.15f, 0.044f),
                    hidden ? "Hidden" : "Visible", !hidden, () =>
                    {
                        _cotLayer.SetUidVisible(captured.uid, hidden);
                        RebuildUi();
                    });
                AddButton("Go_" + i, new Vector3(0.36f, y, -0.01f), new Vector2(0.15f, 0.044f),
                    "Go To", false, () => GoToCot(captured));

                y -= 0.068f;
            }

            float footY = -PanelH / 2f + 0.048f;
            AddButton("Prev", new Vector3(-0.22f, footY, -0.01f), new Vector2(0.18f, 0.046f),
                "< Prev", false, () => { _page = Mathf.Max(0, _page - 1); RebuildUi(); });
            AddButton("ShowAll", new Vector3(0f, footY, -0.01f), new Vector2(0.20f, 0.046f),
                "All Layers ON", false, () =>
                {
                    _cotLayer.ShowUnits = _cotLayer.ShowAircraft = _cotLayer.ShowVideo =
                        _cotLayer.ShowObservers = _cotLayer.ShowWaypoints = _cotLayer.ShowPackages = true;
                    _cotLayer.ForceSync();
                    RebuildUi();
                });
            AddButton("Next", new Vector3(0.22f, footY, -0.01f), new Vector2(0.18f, 0.046f),
                "Next >", false, () => { _page = Mathf.Min(totalPages - 1, _page + 1); RebuildUi(); });
        }

        void GoToCot(NormalizedCot c)
        {
            if (c == null || _cotLayer == null || _world == null || _cam == null) return;
            if (_cotLayer.TryGetMarkerWorldPos(c.uid, out var pos))
            {
                var flat = _cam.forward; flat.y = 0f;
                if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
                flat.Normalize();
                var desired = _cam.position + flat * 160f + Vector3.down * 50f;
                _world.Root.position += desired - pos;
                _flash?.Invoke($"Framed {c.Callsign}");
                Hide();
            }
        }

        public void ImportLocalFiles()
        {
            int imported = 0;
            int filesScanned = 0;
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(Application.persistentDataPath) && Directory.Exists(Application.persistentDataPath))
                dirs.Add(Application.persistentDataPath);
            if (!string.IsNullOrEmpty(Application.streamingAssetsPath) && Directory.Exists(Application.streamingAssetsPath))
                dirs.Add(Application.streamingAssetsPath);

            foreach (var dir in dirs)
            {
                try
                {
                    var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".cot" && ext != ".kml" && ext != ".xml") continue;
                        if (Path.GetFileName(f).StartsWith("AndroidManifest", StringComparison.OrdinalIgnoreCase)) continue;
                        filesScanned++;
                        string text = File.ReadAllText(f);
                        if (ext == ".kml" || text.IndexOf("<kml", StringComparison.OrdinalIgnoreCase) >= 0)
                            imported += ImportKmlText(text, Path.GetFileNameWithoutExtension(f));
                        else
                            imported += ImportCotText(text, Path.GetFileNameWithoutExtension(f));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[OverlayImport] scan warning: " + ex.Message);
                }
            }

            if (imported > 0)
            {
                _feed?.NotifyChanged();
                _cotLayer?.ForceSync();
            }
            _flash?.Invoke(imported > 0
                ? $"Imported {imported} CoT/KML items from {filesScanned} local files"
                : $"Scanned {filesScanned} local files (.cot/.kml in persistentDataPath) — 0 new items");
            if (IsVisible) RebuildUi();
        }

        int ImportCotText(string xmlText, string sourceName)
        {
            int count = 0;
            var blocks = TakDirectClient.SplitEvents(xmlText);
            foreach (var b in blocks)
            {
                var cot = TakDirectClient.ParseCot(b);
                if (cot != null)
                {
                    if (string.IsNullOrEmpty(cot.group)) cot.group = "import:" + sourceName;
                    _feed?.UpsertDirect(cot, notify: false);
                    count++;
                }
            }
            return count;
        }

        int ImportKmlText(string kmlText, string sourceName)
        {
            int count = 0;
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(kmlText);
                var placemarks = doc.GetElementsByTagName("Placemark");
                var now = DateTime.UtcNow;
                for (int i = 0; i < placemarks.Count; i++)
                {
                    if (placemarks[i] is not XmlElement pm) continue;
                    string name = pm["name"]?.InnerText ?? $"{sourceName}-{i + 1}";
                    string desc = pm["description"]?.InnerText ?? $"Imported from {sourceName}.kml";
                    var coordsNodes = pm.GetElementsByTagName("coordinates");
                    if (coordsNodes.Count == 0) continue;
                    string rawCoords = coordsNodes[0].InnerText?.Trim();
                    if (string.IsNullOrEmpty(rawCoords)) continue;

                    var tuples = rawCoords.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tuples.Length == 1)
                    {
                        var parts = tuples[0].Split(',');
                        if (parts.Length >= 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                        {
                            float hae = parts.Length >= 3 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float h) ? h : 0f;
                            var cot = new NormalizedCot
                            {
                                uid = $"kml.{sourceName}.{i}",
                                type = "a-u-G",
                                how = "m-g",
                                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                stale = now.AddHours(24).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                group = "import:" + sourceName,
                                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 10f, le = 10f },
                                contact = new CotContact { callsign = name },
                                detail = new CotDetail { remarks = desc, markerColor = "#00e5ff" },
                            };
                            _feed?.UpsertDirect(cot, notify: false);
                            count++;
                        }
                    }
                    else if (tuples.Length >= 2)
                    {
                        var pts = new List<CotShapePoint>();
                        double clat = 0, clon = 0;
                        foreach (var t in tuples)
                        {
                            var parts = t.Split(',');
                            if (parts.Length < 2) continue;
                            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                            float hae = parts.Length >= 3 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float h) ? h : 0f;
                            pts.Add(new CotShapePoint { lat = lat, lon = lon, hae = hae });
                            clat += lat; clon += lon;
                        }
                        if (pts.Count >= 2)
                        {
                            clat /= pts.Count; clon /= pts.Count;
                            bool isPoly = pm.GetElementsByTagName("Polygon").Count > 0;
                            var cot = new NormalizedCot
                            {
                                uid = $"kml.shape.{sourceName}.{i}",
                                type = isPoly ? "u-d-f" : "b-m-r",
                                how = "m-g",
                                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                stale = now.AddHours(24).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                group = "import:" + sourceName,
                                point = new CotPoint { lat = clat, lon = clon, hae = 0f, ce = 10f, le = 10f },
                                contact = new CotContact { callsign = name },
                                detail = new CotDetail
                                {
                                    remarks = desc,
                                    shapePoints = pts,
                                    closed = isPoly,
                                    strokeColor = "#00e5ff",
                                    fillColor = isPoly ? "#00e5ff" : null,
                                },
                            };
                            _feed?.UpsertDirect(cot, notify: false);
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OverlayImport] KML parse warning: " + ex.Message);
            }
            return count;
        }

        public void ToggleDemoFormation()
        {
            if (_demoSpawned)
            {
                foreach (var uid in _demoUids)
                    _feed?.RemoveByUid(uid, notify: false);
                _demoUids.Clear();
                _demoSpawned = false;
                _feed?.NotifyChanged();
                _cotLayer?.ForceSync();
                _flash?.Invoke("Cleared 3D tactical demo formation");
                if (IsVisible) RebuildUi();
                return;
            }

            double baseLat = _config != null ? _config.originLat : 35.9606;
            double baseLon = _config != null ? _config.originLon : -83.9207;
            float baseHae = _config != null ? (float)_config.originAlt : 300f;
            if (_terrain != null && _terrain.TrySampleHae(baseLat, baseLon, out float dem))
                baseHae = dem;

            var now = DateTime.UtcNow;
            string tNow = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string tStale = now.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            void AddDemo(string uid, string callsign, string type, double dLat, double dLon, float dAlt,
                float course, float speed, string teamName = null, string videoUrl = null, bool observer = false, string iconPath = null)
            {
                var c = new NormalizedCot
                {
                    uid = uid,
                    type = type,
                    how = "m-g",
                    time = tNow,
                    start = tNow,
                    stale = tStale,
                    point = new CotPoint { lat = baseLat + dLat, lon = baseLon + dLon, hae = baseHae + dAlt, ce = 10f, le = 10f },
                    contact = new CotContact { callsign = callsign },
                    detail = new CotDetail
                    {
                        remarks = "3D Tactical Demo Entity",
                        track = new CotTrack { course = course, speed = speed },
                    },
                };
                if (!string.IsNullOrEmpty(teamName))
                    c.detail.team = new CotTeam { name = teamName, role = "Team Member" };
                if (!string.IsNullOrEmpty(videoUrl))
                    c.detail.video = new CotVideo { url = videoUrl, protocol = "rtsp" };
                if (observer)
                {
                    c.detail.takv = new CotTakv { platform = "VRTAK-XR", device = "Samsung Galaxy XR" };
                    c.detail.sensor = new CotSensor { azimuth = course, fov = 70f, range = 300f };
                }
                if (!string.IsNullOrEmpty(iconPath))
                    c.detail.userIcon = new CotUserIcon { iconsetpath = iconPath };

                _demoUids.Add(uid);
                _feed?.UpsertDirect(c, notify: false);
            }

            // 1. 3D Fixed-Wing Fighter & Recon Aircraft (with altitude drop-lines)
            AddDemo("takxr.demo.viper1", "VIPER-1 (F-16)", "a-f-A-M-F-F", 0.0045, -0.0035, 380f, 65f, 145f);
            AddDemo("takxr.demo.reaper2", "REAPER-2 (MQ-9)", "a-f-A-M-F-Q", 0.0065, 0.0040, 460f, 210f, 82f);
            AddDemo("takxr.demo.bogey1", "BANDIT-1 (MiG)", "a-h-A-M-F-F", 0.0085, 0.0010, 520f, 180f, 190f);

            // 2. 3D Rotary-Wing Helicopters (with spinning 3D main & tail rotors)
            AddDemo("takxr.demo.dustoff1", "DUSTOFF-1 (UH-60)", "a-f-A-M-H-U", 0.0028, 0.0022, 190f, 315f, 58f);
            AddDemo("takxr.demo.apache1", "SABER-6 (AH-64)", "a-f-A-M-H-A", 0.0032, -0.0018, 220f, 45f, 64f);

            // 3. 3D Team Spheres ("Balls") with equatorial rings & 3D course cones
            AddDemo("takxr.demo.alpha1", "ALPHA-1", "a-f-G-U-C", 0.0015, -0.0012, 0f, 30f, 2.2f, teamName: "Cyan");
            AddDemo("takxr.demo.bravo2", "BRAVO-2", "a-f-G-U-C", 0.0018, 0.0014, 0f, 120f, 1.8f, teamName: "Green");
            AddDemo("takxr.demo.redfor1", "OPFOR-ARMOR", "a-h-G-E-V-A", 0.0048, -0.0005, 0f, 195f, 8.5f);

            // 4. 3D Gimbal Video Camera Pod
            AddDemo("takxr.demo.isr_cam", "ISR-TOWER-CAM", "b-m-p-s-p-loc", 0.0022, 0.0002, 15f, 0f, 0f,
                videoUrl: "rtsp://demo.takxr.local:554/live/isr1");

            // 5. 3D Standing XR Observer with volumetric FOV cone
            AddDemo("takxr.demo.xrobs1", "XR-JTAC-1", "a-f-G-U-C", 0.0010, 0.0006, 0f, 40f, 0f,
                teamName: "Magenta", observer: true);

            // 6. 3D Extruded dCFS & 2525 Icon Medallions
            var dcfsCot = new NormalizedCot
            {
                uid = "takxr.demo.dcfs1",
                type = "a-n-G",
                how = "m-g",
                time = tNow,
                start = tNow,
                stale = tStale,
                group = "dcfs:events",
                point = new CotPoint { lat = baseLat + 0.0026, lon = baseLon - 0.0025, hae = baseHae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = "DCFS-INCIDENT-42" },
                detail = new CotDetail { remarks = "3D Extruded dCFS Incident Badge", markerColor = "#ff7700" },
            };
            _demoUids.Add(dcfsCot.uid);
            _feed?.UpsertDirect(dcfsCot, notify: false);

            _demoSpawned = true;
            _feed?.NotifyChanged();
            _cotLayer?.ForceSync();
            _flash?.Invoke("Spawned 11 3D Tactical Demo Objects!\n(3D Jets, Spinning Helicopters, Team Balls, Video Pod, XR Observer)");
            if (IsVisible) RebuildUi();
        }

        void Update()
        {
            if (!IsVisible) return;
            PlaceFacing();
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
                        foreach (var (root, onClick) in _hitTargets)
                        {
                            if (root == null) continue;
                            if (hit.transform != root && !hit.transform.IsChildOf(root)) continue;
                            pointingAtUi = true;
                            if (rising && Time.unscaledTime >= _nextClickTime)
                            {
                                _nextClickTime = Time.unscaledTime + 0.26f;
                                onClick?.Invoke();
                            }
                            break;
                        }
                        if (pointingAtUi) break;
                    }
                }

                bool block = pointingAtUi && grabbing;
                if (block)
                {
                    XrHandPinchInput.SetUiBlocking(h, true);
                    _blockedByMe[h] = true;
                }
                else if (_blockedByMe[h])
                {
                    XrHandPinchInput.SetUiBlocking(h, false);
                    _blockedByMe[h] = false;
                }
                if (!IsVisible) return;
            }
        }

        void AddButton(string name, Vector3 pos, Vector2 size, string label, bool active, Action onClick)
        {
            var root = new GameObject("Btn_" + name).transform;
            root.SetParent(_contentRoot, false);
            root.localPosition = pos;
            Color bg = active
                ? new Color(0.14f, 0.50f, 0.44f, 0.97f)
                : new Color(0.10f, 0.19f, 0.28f, 0.95f);
            Quad("Bg", root, Vector3.zero, size, bg, 3000);
            Text("Lbl", root, new Vector3(0f, 0f, -0.005f), label, 0.0060f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.035f);
            col.isTrigger = true;
            _hitTargets.Add((root, onClick));
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
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", color);
            r.sharedMaterial.renderQueue = queue;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static TextMesh Text(string name, Transform parent, Vector3 pos, string msg, float size, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center)
        {
            return XrText.Make(name, parent, pos, msg, size, color, anchor, alignment);
        }
    }
}
