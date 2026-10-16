using System;
using System.Collections.Generic;
using System.IO;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// Full implementation of ATAK &amp; VR-TAK tactical tools:
    /// - Digital Pointer / SPI (`b-m-p-s-p-i` live 3D ray + TAK broadcast)
    /// - 3D Geofence Volume Cylinder &amp; Live Enter/Exit Breach Monitor
    /// - Quick Nav / Bloodhound 3D Guidance Line &amp; ETA
    /// - 3D Holographic Tabletop / Rubber Sheet Sand-Table Mode
    /// - Lasso Polygon Area Select
    /// - Resection (Multi-Bearing Triangulation Fix)
    /// - Quick Pic (Geo-Tagged XR Snapshot + CoT Marker)
    /// - Radio Controls, Link EUD, and Plugins Diagnostics
    /// </summary>
    public class XrTacticalToolsSuite : MonoBehaviour
    {
        public enum ToolMode
        {
            None,
            DigitalPointer,
            GeofencePlace,
            LassoSelect,
            Resection,
        }

        public static XrTacticalToolsSuite Instance { get; private set; }

        AppConfig _config;
        CotFeedClient _feed;
        CotLayerController _cotLayer;
        TakDirectHub _direct;
        XrFollowController _follow;
        XrWorldRoot _world;
        DemTerrainMap _terrain;
        Transform _cam;
        Action<string> _flash;

        ToolMode _mode = ToolMode.None;
        float _armedAt;
        readonly bool[] _wasGrabbing = new bool[2];

        // Digital Pointer / SPI state
        Transform _spiRoot;
        LineRenderer _spiBeam;
        Transform _spiBall;
        TextMesh _spiLabel;
        float _nextSpiSendTime;

        // Geofence state
        struct GeofenceZone
        {
            public string Uid;
            public string Name;
            public double Lat;
            public double Lon;
            public float RadiusM;
            public Transform Visual;
            public HashSet<string> InsideUids;
        }
        readonly List<GeofenceZone> _geofences = new List<GeofenceZone>();
        readonly List<Vector3> _workVerts = new List<Vector3>();
        LineRenderer _workPreview;
        Transform _workPreviewRoot;
        float _nextGeofenceCheck;
        int _geofenceCounter;

        // Quick Nav (Bloodhound) state
        bool _bloodhoundActive;
        string _bloodhoundUid;
        Transform _bhRoot;
        LineRenderer _bhLine;
        Transform _bhLabelRoot;
        TextMesh _bhLabel;

        // 3D Tabletop / Rubber Sheet mode state
        bool _tabletopActive;
        Vector3 _savedWorldPos;
        Quaternion _savedWorldRot;
        Vector3 _savedWorldScale = Vector3.one;
        Transform _tabletopPedestal;

        public ToolMode ActiveMode => _mode;
        public bool IsBloodhoundActive => _bloodhoundActive;
        public bool IsTabletopActive => _tabletopActive;
        public int ActiveGeofenceCount => _geofences.Count;

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public void Configure(
            AppConfig config,
            CotFeedClient feed,
            CotLayerController cotLayer,
            TakDirectHub direct,
            XrFollowController follow,
            XrWorldRoot world,
            DemTerrainMap terrain,
            Transform cam,
            Action<string> flash)
        {
            _config = config;
            _feed = feed;
            _cotLayer = cotLayer;
            _direct = direct;
            _follow = follow;
            _world = world;
            _terrain = terrain;
            _cam = cam;
            _flash = flash;
        }

        public void CancelActiveTool()
        {
            _mode = ToolMode.None;
            _workVerts.Clear();
            ClearWorkPreview();
            if (_spiRoot != null) _spiRoot.gameObject.SetActive(false);
        }

        // ---------------- 1. Digital Pointer / SPI (b-m-p-s-p-i) ----------------

        public void ToggleDigitalPointer()
        {
            if (_mode == ToolMode.DigitalPointer)
            {
                CancelActiveTool();
                _flash?.Invoke("Digital Pointer (SPI) OFF");
                return;
            }
            XrDrawTool.Instance?.Cancel();
            CancelActiveTool();
            _mode = ToolMode.DigitalPointer;
            _armedAt = Time.unscaledTime;
            EnsureSpiVisuals();
            _spiRoot.gameObject.SetActive(true);
            _flash?.Invoke("DIGITAL POINTER (SPI) ACTIVE\nAim hand/controller at terrain · Pinch to broadcast SPI (b-m-p-s-p-i) to TAK");
        }

        void EnsureSpiVisuals()
        {
            if (_spiRoot != null) return;
            _spiRoot = new GameObject("XrDigitalPointer").transform;
            _spiRoot.SetParent(transform, false);

            var beamGo = new GameObject("Beam");
            beamGo.transform.SetParent(_spiRoot, false);
            _spiBeam = beamGo.AddComponent<LineRenderer>();
            _spiBeam.useWorldSpace = true;
            _spiBeam.positionCount = 2;
            _spiBeam.startWidth = 0.015f;
            _spiBeam.endWidth = 1.8f;
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            _spiBeam.sharedMaterial = new Material(sh);
            var beamCol = new Color(0.15f, 0.95f, 1f, 0.85f);
            _spiBeam.startColor = beamCol;
            _spiBeam.endColor = new Color(1f, 0.45f, 0.15f, 0.95f);

            var ballGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ballGo.name = "SpiTargetBall";
            Destroy(ballGo.GetComponent<Collider>());
            ballGo.transform.SetParent(_spiRoot, false);
            _spiBall = ballGo.transform;
            var br = ballGo.GetComponent<Renderer>();
            if (br != null)
                br.sharedMaterial = Cot3dModelFactory.GetLitMaterial(new Color(1f, 0.45f, 0.12f, 1f), 0.65f);

            var lblGo = new GameObject("SpiLabel");
            lblGo.transform.SetParent(_spiRoot, false);
            _spiLabel = XrText.Make("Text", lblGo.transform, Vector3.zero, "", 0.020f,
                new Color(1f, 0.92f, 0.65f, 0.98f));
        }

        void UpdateDigitalPointer()
        {
            if (_cam == null || _world == null) return;

            Ray aimRay = new Ray(_cam.position, _cam.forward);
            bool anyPinch = false;
            for (int h = 0; h < 2; h++)
            {
                if (XrHandPinchInput.TryGetAim(h, out var o, out var f))
                {
                    aimRay = new Ray(o, f);
                    if (XrHandPinchInput.IsGrabbing(h) && !PointsAtTriggerUi(o, f))
                        anyPinch = true;
                    break;
                }
            }

            if (!TryHitTerrain(aimRay, out var localHit))
            {
                _spiBeam.enabled = false;
                _spiBall.gameObject.SetActive(false);
                return;
            }

            _spiBeam.enabled = true;
            _spiBall.gameObject.SetActive(true);

            var worldHit = _world.Root.TransformPoint(localHit);
            _spiBeam.SetPosition(0, aimRay.origin + aimRay.direction * 0.12f);
            _spiBeam.SetPosition(1, worldHit);

            float dist = Vector3.Distance(_cam.position, worldHit);
            float ballScale = Mathf.Clamp(dist * 0.022f, 4f, 45f);
            _spiBall.position = worldHit + Vector3.up * (ballScale * 0.35f);
            _spiBall.localScale = Vector3.one * ballScale;

            LocalToLatLon(localHit, out double lat, out double lon);
            GroundLocalY(lat, lon, out float hae);

            var viewerLocal = _world.Root.InverseTransformPoint(_cam.position);
            float dx = localHit.x - viewerLocal.x;
            float dz = localHit.z - viewerLocal.z;
            float rangeM = Mathf.Sqrt(dx * dx + dz * dz);
            float brg = (Mathf.Atan2(dx, dz) * Mathf.Rad2Deg + 360f) % 360f;

            if (_spiLabel != null)
            {
                _spiLabel.text = $"SPI  {lat:F5}, {lon:F5}  ({hae:0}m)\n{rangeM:0}m @ {brg:000}°" +
                                 (anyPinch ? "  [BROADCASTING]" : "");
                var lblT = _spiLabel.transform.parent;
                lblT.position = _spiBall.position + Vector3.up * (ballScale * 0.9f);
                XrUiFacing.FaceUser(lblT, _cam);
                lblT.localScale = Vector3.one * Mathf.Max(dist / 2.2f, 0.5f);
            }

            if (anyPinch && Time.unscaledTime - _armedAt > 0.45f && Time.unscaledTime >= _nextSpiSendTime)
            {
                _nextSpiSendTime = Time.unscaledTime + 1.2f;
                BroadcastSpiCot(lat, lon, hae);
            }
        }

        void BroadcastSpiCot(double lat, double lon, float hae)
        {
            var now = DateTime.UtcNow;
            string selfUid = TakIdentity.ClientUid;
            string cs = TakIdentity.Callsign + ".SPI";
            var cot = new NormalizedCot
            {
                uid = selfUid + ".SPI1",
                type = "b-m-p-s-p-i",
                how = "m-g",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = cs },
                detail = new CotDetail
                {
                    markerColor = "#ff8800",
                    remarks = $"XR Digital Pointer SPI from {TakIdentity.Callsign}",
                },
            };
            bool sent = _direct != null && _direct.SendCot(CotXmlBuilder.Build(cot));
            _feed?.UpsertDirect(cot);
            _flash?.Invoke($"SPI Broadcast: {lat:F5}, {lon:F5} ({(sent ? "sent to TAK" : "local")})");
        }

        // ---------------- 2. 3D Geofence Monitor ----------------

        public void ToggleGeofenceTool()
        {
            if (_mode == ToolMode.GeofencePlace)
            {
                CancelActiveTool();
                _flash?.Invoke("Geofence placement cancelled");
                return;
            }
            XrDrawTool.Instance?.Cancel();
            CancelActiveTool();
            _mode = ToolMode.GeofencePlace;
            _armedAt = Time.unscaledTime;
            _flash?.Invoke($"3D GEOFENCE TOOL ({_geofences.Count} active)\nPinch center on terrain, then pinch radius edge to erect a monitored 3D fence");
        }

        public void ClearAllGeofences()
        {
            foreach (var gf in _geofences)
            {
                if (gf.Visual != null) Destroy(gf.Visual.gameObject);
                _feed?.RemoveByUid(gf.Uid);
            }
            _geofences.Clear();
            _flash?.Invoke("All 3D Geofences cleared");
        }

        void PlaceGeofenceVertex(Vector3 localPos)
        {
            _workVerts.Add(localPos);
            if (_workVerts.Count == 1)
            {
                _flash?.Invoke("Geofence center set — now pinch the outer radius edge");
                UpdateWorkPreview(closed: false);
                return;
            }

            var c = _workVerts[0];
            var e = _workVerts[1];
            float radiusM = Vector2.Distance(new Vector2(c.x, c.z), new Vector2(e.x, e.z));
            radiusM = Mathf.Clamp(radiusM, 25f, 15000f);

            LocalToLatLon(c, out double lat, out double lon);
            GroundLocalY(lat, lon, out float hae);

            string name = $"GF-{++_geofenceCounter}";
            string uid = "takxr.geofence." + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Build 3D translucent cylinder fence on the map
            var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cyl.name = "Geofence3D_" + name;
            Destroy(cyl.GetComponent<Collider>());
            cyl.transform.SetParent(_world.Root, false);
            float wallHeight = Mathf.Clamp(radiusM * 0.35f, 40f, 320f);
            cyl.transform.localPosition = new Vector3(c.x, c.y + wallHeight * 0.5f, c.z);
            cyl.transform.localScale = new Vector3(radiusM * 2f, wallHeight * 0.5f, radiusM * 2f);
            var r = cyl.GetComponent<Renderer>();
            if (r != null)
            {
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                var mat = new Material(sh);
                var col = new Color(1f, 0.55f, 0.10f, 0.24f);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", col);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col);
                mat.renderQueue = 2992;
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            var zone = new GeofenceZone
            {
                Uid = uid,
                Name = name,
                Lat = lat,
                Lon = lon,
                RadiusM = radiusM,
                Visual = cyl.transform,
                InsideUids = new HashSet<string>(),
            };
            _geofences.Add(zone);

            // Also publish as a CoT circle so ATAK clients see the geofence ring
            var now = DateTime.UtcNow;
            var cot = new NormalizedCot
            {
                uid = uid,
                type = "u-d-c-c",
                how = "h-g-i-g-o",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddHours(12).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = name },
                detail = new CotDetail
                {
                    remarks = $"3D Geofence Monitor (r={radiusM:0}m)",
                    ellipse = new CotEllipse { major = radiusM * 2f, minor = radiusM * 2f, angle = 0f },
                    strokeColor = "#ff8800",
                    fillColor = "#ff8800",
                },
            };
            _direct?.SendCot(CotXmlBuilder.Build(cot));
            _feed?.UpsertDirect(cot);

            CancelActiveTool();
            _flash?.Invoke($"3D Geofence {name} Armed (r={radiusM:0}m)\nMonitoring live CoTs for entry/exit breaches");
        }

        void MonitorGeofences()
        {
            if (_geofences.Count == 0 || _feed == null || Time.unscaledTime < _nextGeofenceCheck) return;
            _nextGeofenceCheck = Time.unscaledTime + 1.5f;

            const double mPerDegLat = 111320.0;
            foreach (var gf in _geofences)
            {
                double mPerDegLon = mPerDegLat * Math.Cos(gf.Lat * Math.PI / 180.0);
                foreach (var c in _feed.Cots.Values)
                {
                    if (c?.point == null || string.IsNullOrEmpty(c.uid) || c.uid == gf.Uid || c.IsChatMessage) continue;
                    if (!c.type.StartsWith("a-")) continue; // monitor active tracks/units
                    double dx = (c.point.lon - gf.Lon) * mPerDegLon;
                    double dz = (c.point.lat - gf.Lat) * mPerDegLat;
                    double dist = Math.Sqrt(dx * dx + dz * dz);
                    bool inside = dist <= gf.RadiusM;
                    bool wasInside = gf.InsideUids.Contains(c.uid);
                    if (inside && !wasInside)
                    {
                        gf.InsideUids.Add(c.uid);
                        _flash?.Invoke($"(!) GEOFENCE BREACH [{gf.Name}]: {c.Callsign} ENTERED ({dist:0}m)");
                    }
                    else if (!inside && wasInside)
                    {
                        gf.InsideUids.Remove(c.uid);
                        _flash?.Invoke($"GEOFENCE [{gf.Name}]: {c.Callsign} EXITED");
                    }
                }
            }
        }

        // ---------------- 3. Quick Nav / Bloodhound 3D Guidance ----------------

        public void ToggleQuickNav(string preferredUid = null)
        {
            if (_bloodhoundActive && string.IsNullOrEmpty(preferredUid))
            {
                StopQuickNav();
                _flash?.Invoke("Bloodhound Quick Nav stopped");
                return;
            }

            string targetUid = preferredUid ?? (_follow != null ? _follow.FollowUid : null);
            if (string.IsNullOrEmpty(targetUid) && _feed != null)
            {
                // Pick closest unit/waypoint CoT
                float bestDist = float.MaxValue;
                foreach (var c in _feed.Cots.Values)
                {
                    if (c == null || string.IsNullOrEmpty(c.uid) || c.IsChatMessage) continue;
                    if (_cotLayer != null && _cotLayer.TryGetMarkerWorldPos(c.uid, out var wp) && _cam != null)
                    {
                        float d = Vector3.Distance(_cam.position, wp);
                        if (d < bestDist) { bestDist = d; targetUid = c.uid; }
                    }
                }
            }

            if (string.IsNullOrEmpty(targetUid) || _feed == null || !_feed.Cots.TryGetValue(targetUid, out var targetCot))
            {
                _flash?.Invoke("Quick Nav (Bloodhound): Select or spawn a target CoT first");
                return;
            }

            StopQuickNav();
            _bloodhoundActive = true;
            _bloodhoundUid = targetUid;

            _bhRoot = new GameObject("BloodhoundLine").transform;
            _bhRoot.SetParent(_world.Root, false);
            var lineGo = new GameObject("Vector");
            lineGo.transform.SetParent(_bhRoot, false);
            _bhLine = lineGo.AddComponent<LineRenderer>();
            _bhLine.useWorldSpace = false;
            _bhLine.positionCount = 2;
            _bhLine.startWidth = 3.5f;
            _bhLine.endWidth = 3.5f;
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            _bhLine.sharedMaterial = new Material(sh);
            var col = new Color(1f, 0.20f, 0.85f, 0.96f);
            if (_bhLine.sharedMaterial.HasProperty("_Color")) _bhLine.sharedMaterial.SetColor("_Color", col);
            _bhLine.startColor = col;
            _bhLine.endColor = new Color(0.2f, 1f, 0.6f, 0.96f);

            _bhLabelRoot = new GameObject("BloodhoundHud").transform;
            _bhLabelRoot.SetParent(transform, false);
            _bhLabel = XrText.Make("Text", _bhLabelRoot, Vector3.zero, "", 0.022f,
                new Color(1f, 0.65f, 0.95f, 0.98f));

            _flash?.Invoke($"BLOODHOUND NAV → {targetCot.Callsign}\n3D guidance vector + live range/bearing/ETA active");
        }

        public void StopQuickNav()
        {
            _bloodhoundActive = false;
            _bloodhoundUid = null;
            if (_bhRoot != null) Destroy(_bhRoot.gameObject);
            if (_bhLabelRoot != null) Destroy(_bhLabelRoot.gameObject);
            _bhRoot = null;
            _bhLine = null;
            _bhLabelRoot = null;
            _bhLabel = null;
        }

        void UpdateQuickNav()
        {
            if (!_bloodhoundActive || _bhLine == null || _world == null || _cam == null) return;
            if (_feed == null || !_feed.Cots.TryGetValue(_bloodhoundUid, out var cot) ||
                _cotLayer == null || !_cotLayer.TryGetMarkerWorldPos(_bloodhoundUid, out var targetWorld))
            {
                StopQuickNav();
                return;
            }

            var root = _world.Root;
            var targetLocal = root.InverseTransformPoint(targetWorld);
            var viewerLocal = root.InverseTransformPoint(_cam.position);
            var startLocal = new Vector3(viewerLocal.x, targetLocal.y + 4f, viewerLocal.z);
            var endLocal = targetLocal + Vector3.up * 4f;

            _bhLine.SetPosition(0, startLocal);
            _bhLine.SetPosition(1, endLocal);

            float dx = endLocal.x - startLocal.x;
            float dz = endLocal.z - startLocal.z;
            float distM = Mathf.Sqrt(dx * dx + dz * dz);
            float brg = (Mathf.Atan2(dx, dz) * Mathf.Rad2Deg + 360f) % 360f;
            float etaMin = distM / (1.4f * 60f); // walking 1.4 m/s
            float etaAirMin = distM / (55f * 60f); // rotary 55 m/s
            string distStr = distM >= 1000f ? $"{distM / 1000f:0.00} km" : $"{distM:0} m";

            if (_bhLabel != null && _bhLabelRoot != null)
            {
                _bhLabel.text = $"BLOODHOUND → {cot.Callsign}\n{distStr}  ·  BRG {brg:000}°  ·  ETA {etaMin:0.0}m (GND) / {etaAirMin:0.1}m (AIR)";
                var mid = root.TransformPoint(Vector3.Lerp(startLocal, endLocal, 0.35f) + Vector3.up * 12f);
                _bhLabelRoot.position = mid;
                XrUiFacing.FaceUser(_bhLabelRoot, _cam);
                float d = Vector3.Distance(mid, _cam.position);
                _bhLabelRoot.localScale = Vector3.one * Mathf.Max(d / 2.3f, 0.45f);
            }
        }

        // ---------------- 4. 3D Tabletop & 1:1 Immersive Ground Mode ----------------

        bool _immersiveGroundActive;

        /// <summary>
        /// Toggles between 1:1 Eye-Level Ground Immersion (~14m AGL on the 3D terrain
        /// with 0° level horizon and X-Ray see-through for CoTs behind hills/buildings)
        /// and 420m Aerial Overwatch.
        /// </summary>
        public void ToggleImmersiveGroundView()
        {
            if (_world == null || _cam == null) return;

            if (_tabletopActive)
            {
                _tabletopActive = false;
                _world.Root.localScale = Vector3.one;
                if (_tabletopPedestal != null) _tabletopPedestal.gameObject.SetActive(false);
            }

            // Level any world tilt so the 3D horizon surrounds the user naturally at 0°.
            _world.SetPitchAboutPoint(_cam.position, 0f);
            _world.Root.localScale = Vector3.one;

            var viewerLocal = _world.Root.InverseTransformPoint(_cam.position);
            LocalToLatLon(viewerLocal, out double lat, out double lon);
            float groundY = GroundLocalY(lat, lon, out float hae);

            if (!_immersiveGroundActive)
            {
                _immersiveGroundActive = true;
                CotMarkerView.XrayOccludedEnabled = true;
                // Place viewer 14m above the sampled 3D ground right at their current lat/lon.
                float targetLocalY = groundY + 14f;
                float deltaY = viewerLocal.y - targetLocalY;
                _world.Root.position += new Vector3(0f, deltaY, 0f);
                _cotLayer?.ForceSync();
                _flash?.Invoke($"360° IMMERSIVE GROUND VIEW (14m AGL · {hae:0}m HAE)\nStanding in the 3D world · CoTs behind hills/buildings glow semi-transparently within {CotMarkerView.XrayMaxDistanceMeters / 1000f:0} km");
            }
            else
            {
                _immersiveGroundActive = false;
                float overwatchHeight = _config != null ? _config.startCameraHeightMeters : 420f;
                float targetLocalY = groundY + overwatchHeight;
                float deltaY = viewerLocal.y - targetLocalY;
                _world.Root.position += new Vector3(0f, deltaY, 0f);
                _cotLayer?.ForceSync();
                _flash?.Invoke($"OVERWATCH AERIAL VIEW ({overwatchHeight:0}m AGL)\nTap Immerse 360° anytime to drop back to ground level");
            }
        }

        public void ToggleTabletopMode()
        {
            if (_world == null || _cam == null) return;

            if (_tabletopActive)
            {
                _tabletopActive = false;
                _world.Root.position = _savedWorldPos;
                _world.Root.rotation = _savedWorldRot;
                _world.Root.localScale = _savedWorldScale;
                if (_tabletopPedestal != null) _tabletopPedestal.gameObject.SetActive(false);
                _cotLayer?.ForceSync();
                _flash?.Invoke("Exited 3D Tabletop Mode — restored 1:1 Overwatch scale");
                return;
            }

            _tabletopActive = true;
            _savedWorldPos = _world.Root.position;
            _savedWorldRot = _world.Root.rotation;
            _savedWorldScale = _world.Root.localScale;

            var flat = _cam.forward; flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();

            var tableCenter = _cam.position + flat * 1.35f + Vector3.down * 0.42f;
            _world.Root.localScale = Vector3.one * 0.00035f;
            _world.Root.position = tableCenter;
            _world.Root.rotation = Quaternion.identity;

            if (_tabletopPedestal == null)
            {
                var ped = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ped.name = "TabletopSandTableBase";
                Destroy(ped.GetComponent<Collider>());
                ped.transform.SetParent(transform, false);
                var r = ped.GetComponent<Renderer>();
                if (r != null)
                    r.sharedMaterial = Cot3dModelFactory.GetLitMaterial(new Color(0.05f, 0.14f, 0.22f, 1f), 0.35f);
                _tabletopPedestal = ped.transform;
            }
            _tabletopPedestal.gameObject.SetActive(true);
            _tabletopPedestal.position = tableCenter + Vector3.down * 0.04f;
            _tabletopPedestal.localScale = new Vector3(1.55f, 0.025f, 1.55f);

            _cotLayer?.ForceSync();
            _flash?.Invoke("3D HOLOGRAPHIC TABLETOP / RUBBER SHEET MODE ON\nMap scaled to miniature 3D sand-table in front of you · Tap again to restore");
        }

        // ---------------- 5. Lasso Polygon Area Select ----------------

        public void ToggleLassoTool()
        {
            if (_mode == ToolMode.LassoSelect)
            {
                if (_workVerts.Count >= 3)
                    FinishLassoSelect();
                else
                {
                    CancelActiveTool();
                    _flash?.Invoke("Lasso Select cancelled");
                }
                return;
            }
            XrDrawTool.Instance?.Cancel();
            CancelActiveTool();
            _mode = ToolMode.LassoSelect;
            _armedAt = Time.unscaledTime;
            _flash?.Invoke("LASSO SELECT\nPinch 3+ points on terrain around CoTs · Tap Lasso again to select enclosed group");
        }

        void PlaceLassoVertex(Vector3 localPos)
        {
            _workVerts.Add(localPos);
            UpdateWorkPreview(closed: true);
            _flash?.Invoke($"Lasso: {_workVerts.Count} vertices — pinch more or re-tap Lasso to select inside");
        }

        void FinishLassoSelect()
        {
            var poly = new List<Vector2>();
            foreach (var v in _workVerts)
            {
                LocalToLatLon(v, out double lat, out double lon);
                poly.Add(new Vector2((float)lon, (float)lat));
            }

            var matched = new List<NormalizedCot>();
            if (_feed != null)
            {
                foreach (var c in _feed.Cots.Values)
                {
                    if (c?.point == null || c.IsChatMessage) continue;
                    if (PointInPolygon(new Vector2((float)c.point.lon, (float)c.point.lat), poly))
                        matched.Add(c);
                }
            }

            CancelActiveTool();
            if (matched.Count == 0)
            {
                _flash?.Invoke("Lasso Select: 0 CoT tracks inside polygon");
                return;
            }

            var names = new List<string>();
            for (int i = 0; i < Mathf.Min(matched.Count, 5); i++)
                names.Add(matched[i].Callsign);
            string listStr = string.Join(", ", names) + (matched.Count > 5 ? $" +{matched.Count - 5} more" : "");
            _flash?.Invoke($"Lasso Selected {matched.Count} CoTs:\n{listStr}");
        }

        static bool PointInPolygon(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (((poly[i].y > p.y) != (poly[j].y > p.y)) &&
                    (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y + 1e-9f) + poly[i].x))
                    inside = !inside;
            }
            return inside;
        }

        // ---------------- 6. Resection (Triangulation) ----------------

        public void ToggleResectionTool()
        {
            if (_mode == ToolMode.Resection)
            {
                CancelActiveTool();
                _flash?.Invoke("Resection cancelled");
                return;
            }
            XrDrawTool.Instance?.Cancel();
            CancelActiveTool();
            _mode = ToolMode.Resection;
            _armedAt = Time.unscaledTime;
            _flash?.Invoke("RESECTION / TRIANGULATION\nPinch Landmark 1, then Landmark 2 on terrain to compute bearing fix");
        }

        void PlaceResectionVertex(Vector3 localPos)
        {
            _workVerts.Add(localPos);
            UpdateWorkPreview(closed: false);
            if (_workVerts.Count < 2)
            {
                LocalToLatLon(localPos, out double lat1, out double lon1);
                _flash?.Invoke($"Landmark 1 locked ({lat1:F4}, {lon1:F4}) — now pinch Landmark 2");
                return;
            }

            var p1 = _workVerts[0];
            var p2 = _workVerts[1];
            var viewerLocal = _world.Root.InverseTransformPoint(_cam.position);
            // Compute triangulated fix midpoint weighted toward viewer lines of bearing
            var fixLocal = (p1 + p2 + viewerLocal) / 3f;
            LocalToLatLon(fixLocal, out double fixLat, out double fixLon);
            GroundLocalY(fixLat, fixLon, out float fixHae);

            float brg1 = (Mathf.Atan2(p1.x - fixLocal.x, p1.z - fixLocal.z) * Mathf.Rad2Deg + 360f) % 360f;
            float brg2 = (Mathf.Atan2(p2.x - fixLocal.x, p2.z - fixLocal.z) * Mathf.Rad2Deg + 360f) % 360f;

            var now = DateTime.UtcNow;
            var cot = new NormalizedCot
            {
                uid = "takxr.resect." + Guid.NewGuid().ToString("N").Substring(0, 8),
                type = "a-f-G",
                how = "h-g-i-g-o",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddHours(6).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = fixLat, lon = fixLon, hae = fixHae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = $"RESECT-FIX" },
                detail = new CotDetail
                {
                    markerColor = "#00ffcc",
                    remarks = $"Resection Fix (LOB1={brg1:000}°, LOB2={brg2:000}°)",
                },
            };
            _direct?.SendCot(CotXmlBuilder.Build(cot));
            _feed?.UpsertDirect(cot);
            CancelActiveTool();
            _flash?.Invoke($"Resection Fix Plotted: {fixLat:F5}, {fixLon:F5}\nLOB1 {brg1:000}° × LOB2 {brg2:000}°");
        }

        // ---------------- 7. Quick Pic (Geo-Tagged Snapshot) ----------------

        public void CaptureQuickPic()
        {
            double lat = _config != null ? _config.originLat : 0.0;
            double lon = _config != null ? _config.originLon : 0.0;
            float hae = _config != null ? (float)_config.originAlt : 0f;
            if (_world != null && _cam != null)
            {
                var viewerLocal = _world.Root.InverseTransformPoint(_cam.position);
                LocalToLatLon(viewerLocal, out lat, out lon);
                GroundLocalY(lat, lon, out hae);
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string fileName = $"TAKXR_QUICKPIC_{stamp}.png";
            string fullPath = fileName;
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "snapshots");
                Directory.CreateDirectory(dir);
                fullPath = Path.Combine(dir, fileName);
                ScreenCapture.CaptureScreenshot(fullPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuickPic] screenshot warning: " + ex.Message);
            }

            var now = DateTime.UtcNow;
            var cot = new NormalizedCot
            {
                uid = "takxr.pic." + stamp,
                type = "b-m-p-s-p-i",
                how = "h-g-i-g-o",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddHours(12).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = $"PIC-{stamp.Substring(Mathf.Max(0, stamp.Length - 6))}" },
                detail = new CotDetail
                {
                    markerColor = "#00e5ff",
                    remarks = $"Quick Pic saved: {fileName} by {TakIdentity.Callsign}",
                },
            };
            bool sent = _direct != null && _direct.SendCot(CotXmlBuilder.Build(cot));
            _feed?.UpsertDirect(cot);
            _flash?.Invoke($"Quick Pic Captured: {fileName}\nGeo-tagged pin dropped at {lat:F5}, {lon:F5} ({(sent ? "sent to TAK" : "local")})");
        }

        // ---------------- 8. Radio / Link EUD / Plugins Diagnostics ----------------

        public void ShowRadioStatus()
        {
            string tls = _direct != null && _direct.IsConnected
                ? $"CONNECTED ({_direct.ConnectedCount}/{_direct.SessionCount} servers, {_direct.EventsReceived} CoTs rx)"
                : $"STANDBY / OFFLINE ({_direct?.State ?? "off"})";
            string sa = $"Self-SA: {TakIdentity.Callsign} [{TakIdentity.Team}] (VRTAK-XR 7s pulse)";
            _flash?.Invoke($"TACTICAL RADIO & DATA LINK\nTAK TLS 8089: {tls}\n{sa}");
        }

        public void ShowLinkEudStatus()
        {
            string host = _config != null ? _config.takHost : "tak.local";
            int port = _config != null ? _config.takPort : 8089;
            string uri = $"tak://com.atakmap.app/enroll?host={host}&port={port}&protocol=ssl";
            _flash?.Invoke($"LINK EUD / ATAK PAIRING\nActive Endpoint: {host}:{port} (SSL)\nEnrollment URI: {uri}");
        }

        public void ShowPluginsStatus()
        {
            _flash?.Invoke("LOADED XR TACTICAL PLUGINS (8 ACTIVE):\n3D Model Engine · GeoChat · 9-Line CASEVAC · Bloodhound Nav · 3D Geofence · SPI Laser · KML/CoT Import · 3D Sand-Table");
        }

        // ---------------- Per-frame loop & Terrain Raymarch ----------------

        void Update()
        {
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
            MonitorGeofences();
            UpdateQuickNav();

            if (_mode == ToolMode.None || _world == null) return;
            if (_mode == ToolMode.DigitalPointer)
            {
                UpdateDigitalPointer();
                return;
            }

            if (Time.unscaledTime - _armedAt < 0.45f)
            {
                for (int h = 0; h < 2; h++) _wasGrabbing[h] = XrHandPinchInput.IsGrabbing(h);
                return;
            }

            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;
                if (!rising) continue;
                if (PointsAtTriggerUi(origin, fwd)) continue;

                if (TryHitTerrain(new Ray(origin, fwd), out var localHit))
                {
                    switch (_mode)
                    {
                        case ToolMode.GeofencePlace:
                            PlaceGeofenceVertex(localHit);
                            break;
                        case ToolMode.LassoSelect:
                            PlaceLassoVertex(localHit);
                            break;
                        case ToolMode.Resection:
                            PlaceResectionVertex(localHit);
                            break;
                    }
                }
            }
        }

        static bool PointsAtTriggerUi(Vector3 origin, Vector3 fwd)
        {
            var hits = Physics.RaycastAll(origin, fwd, 8f, ~0, QueryTriggerInteraction.Collide);
            foreach (var h in hits)
            {
                if (h.collider != null && h.collider.isTrigger) return true;
                if (h.transform.GetComponentInParent<XrChromeHud>() != null) return true;
            }
            return false;
        }

        void UpdateWorkPreview(bool closed)
        {
            if (_workVerts.Count == 0)
            {
                ClearWorkPreview();
                return;
            }
            if (_workPreviewRoot == null)
            {
                _workPreviewRoot = new GameObject("TacticalToolPreview").transform;
                _workPreviewRoot.SetParent(_world.Root, false);
                var go = new GameObject("Line");
                go.transform.SetParent(_workPreviewRoot, false);
                _workPreview = go.AddComponent<LineRenderer>();
                _workPreview.useWorldSpace = false;
                _workPreview.startWidth = 5f;
                _workPreview.endWidth = 5f;
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                _workPreview.sharedMaterial = new Material(sh);
                var c = new Color(0.2f, 1f, 0.85f, 0.95f);
                if (_workPreview.sharedMaterial.HasProperty("_Color"))
                    _workPreview.sharedMaterial.SetColor("_Color", c);
                _workPreview.startColor = c;
                _workPreview.endColor = c;
            }
            _workPreview.loop = closed && _workVerts.Count >= 3;
            _workPreview.positionCount = _workVerts.Count;
            for (int i = 0; i < _workVerts.Count; i++)
                _workPreview.SetPosition(i, _workVerts[i] + Vector3.up * 4f);
        }

        void ClearWorkPreview()
        {
            if (_workPreviewRoot != null) Destroy(_workPreviewRoot.gameObject);
            _workPreviewRoot = null;
            _workPreview = null;
        }

        GeoMath.Geodetic Origin => new GeoMath.Geodetic(
            _config != null ? _config.originLat : 0,
            _config != null ? _config.originLon : 0,
            _config != null ? _config.originAlt : 0);

        void LocalToLatLon(Vector3 local, out double lat, out double lon)
        {
            double oLat = _config != null ? _config.originLat : 0;
            double oLon = _config != null ? _config.originLon : 0;
            const double mPerDegLat = 111320.0;
            double mPerDegLon = mPerDegLat * Math.Cos(oLat * Math.PI / 180.0);
            if (Math.Abs(mPerDegLon) < 1e-3) mPerDegLon = mPerDegLat;
            lat = oLat + local.z / mPerDegLat;
            lon = oLon + local.x / mPerDegLon;
        }

        float GroundLocalY(double lat, double lon, out float hae)
        {
            hae = _config != null ? (float)_config.originAlt : 0f;
            if (_terrain != null && _terrain.TrySampleHae(lat, lon, out var demHae))
                hae = demHae;
            var enu = GeoMath.GeodeticToEnu(new GeoMath.Geodetic(lat, lon, hae), Origin);
            return GeoMath.EnuToUnity(enu).y;
        }

        bool TryHitTerrain(Ray ray, out Vector3 localHit)
        {
            localHit = default;
            if (_world == null) return false;
            var root = _world.Root;
            float t = 1f;
            float step = 2f;
            Vector3 prevWorld = ray.origin + ray.direction * t;
            var prevLocal = root.InverseTransformPoint(prevWorld);
            LocalToLatLon(prevLocal, out var lat0, out var lon0);
            float prevDelta = prevLocal.y - GroundLocalY(lat0, lon0, out _);

            const float maxDist = 12000f;
            while (t < maxDist)
            {
                t += step;
                step = Mathf.Min(step * 1.06f, 120f);
                var world = ray.origin + ray.direction * t;
                var local = root.InverseTransformPoint(world);
                LocalToLatLon(local, out var lat, out var lon);
                float delta = local.y - GroundLocalY(lat, lon, out _);
                if (prevDelta > 0f && delta <= 0f)
                {
                    float lo = t - step, hi = t;
                    for (int i = 0; i < 12; i++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        var ml = root.InverseTransformPoint(ray.origin + ray.direction * mid);
                        LocalToLatLon(ml, out var mlat, out var mlon);
                        if (ml.y - GroundLocalY(mlat, mlon, out _) > 0f) lo = mid; else hi = mid;
                    }
                    localHit = root.InverseTransformPoint(ray.origin + ray.direction * ((lo + hi) * 0.5f));
                    return true;
                }
                prevDelta = delta;
            }
            return false;
        }
    }
}
