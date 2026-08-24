using System;
using System.Collections.Generic;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// Point dropping + drawing (route / polygon / circle) on the terrain, published
    /// straight to the TAK server as CoT (b-m-p-s-m, b-m-r, u-d-f, u-d-c-c) — the
    /// standalone equivalent of the web version's drawing tools. Arm a mode from
    /// the toolbar, pinch to place vertices on the map, press the toolbar button
    /// again to finish (route/polygon).
    /// </summary>
    public class XrDrawTool : MonoBehaviour
    {
        public enum Mode { None, Point, Route, Polygon, Circle }

        public static XrDrawTool Instance { get; private set; }
        public static bool IsArmed => Instance != null && Instance._mode != Mode.None;

        AppConfig _config;
        CotFeedClient _feed;
        TakDirectHub _direct;
        XrWorldRoot _world;
        DemTerrainMap _terrain;
        Transform _cam;
        Action<string> _flash;

        Mode _mode = Mode.None;
        readonly List<Vector3> _verts = new List<Vector3>(); // local under world root
        readonly bool[] _wasGrabbing = new bool[2];
        float _armedAt;
        int _dropCounter;
        LineRenderer _preview;
        Transform _previewRoot;

        // Draw options (persisted last-used).
        const string PrefCallsign = "takxr.drawCallsign";
        const string PrefAff = "takxr.drawAff";
        const string PrefStroke = "takxr.drawStroke";
        static readonly string[] AffCycles = { "f", "h", "n", "u" };
        static readonly string[] StrokeCycles =
        {
            "#00d0ff", "#ffcc00", "#ff4444", "#00ff88", "#ff00ff", "#ffffff",
        };
        string _drawCallsign = "XR-PT";
        string _affiliation = "f";
        string _strokeCss = "#00d0ff";
        Transform _optsRoot;
        TextMesh _optsLabel;

        public Mode ActiveMode => _mode;
        public string DrawCallsign => _drawCallsign;
        public string Affiliation => _affiliation;
        public string StrokeCss => _strokeCss;

        public void Configure(AppConfig config, CotFeedClient feed, TakDirectHub direct,
            XrWorldRoot world, DemTerrainMap terrain, Transform cam, Action<string> flash)
        {
            _config = config;
            _feed = feed;
            _direct = direct;
            _world = world;
            _terrain = terrain;
            _cam = cam;
            _flash = flash;
            _drawCallsign = PlayerPrefs.GetString(PrefCallsign, "XR-PT");
            _affiliation = PlayerPrefs.GetString(PrefAff, "f");
            _strokeCss = PlayerPrefs.GetString(PrefStroke, "#00d0ff");
        }

        void SaveDrawPrefs()
        {
            PlayerPrefs.SetString(PrefCallsign, _drawCallsign);
            PlayerPrefs.SetString(PrefAff, _affiliation);
            PlayerPrefs.SetString(PrefStroke, _strokeCss);
            PlayerPrefs.Save();
        }

        public void CycleAffiliation()
        {
            int idx = 0;
            for (int i = 0; i < AffCycles.Length; i++)
                if (AffCycles[i] == _affiliation) { idx = i; break; }
            _affiliation = AffCycles[(idx + 1) % AffCycles.Length];
            SaveDrawPrefs();
            RefreshOptsUi();
            _flash?.Invoke($"Affiliation: {_affiliation}");
        }

        public void CycleStrokeColor()
        {
            int idx = 0;
            for (int i = 0; i < StrokeCycles.Length; i++)
                if (string.Equals(StrokeCycles[i], _strokeCss, StringComparison.OrdinalIgnoreCase))
                { idx = i; break; }
            _strokeCss = StrokeCycles[(idx + 1) % StrokeCycles.Length];
            SaveDrawPrefs();
            RefreshOptsUi();
            _flash?.Invoke($"Stroke {_strokeCss}");
        }

        public void CycleDrawCallsign()
        {
            string[] presets = { "XR-PT", "XR-MARK", "POINT", "OP", "LP" };
            int idx = 0;
            for (int i = 0; i < presets.Length; i++)
                if (presets[i] == _drawCallsign) { idx = i; break; }
            _drawCallsign = presets[(idx + 1) % presets.Length];
            SaveDrawPrefs();
            RefreshOptsUi();
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>Toolbar entry point: arms the mode, finishes it when re-pressed.</summary>
        public void ToggleMode(Mode mode)
        {
            if (_mode == mode)
            {
                if ((_mode == Mode.Route || _mode == Mode.Polygon) && _verts.Count >= 2)
                    FinishShape();
                else
                {
                    Cancel();
                    _flash?.Invoke("Draw cancelled");
                }
                return;
            }

            Cancel();
            _mode = mode;
            _armedAt = Time.unscaledTime;
            EnsureOptsUi();
            RefreshOptsUi();
            _flash?.Invoke(mode switch
            {
                Mode.Point => $"DROP POINT ({_drawCallsign} a-{_affiliation}-G)\npinch map · opts cycle on chrome",
                Mode.Route => "DRAW ROUTE\npinch vertices · press route again to finish",
                Mode.Polygon => "DRAW SHAPE\npinch vertices · press shape again to finish",
                Mode.Circle => "DRAW CIRCLE\npinch center, then edge",
                _ => "",
            });
        }

        public void Cancel()
        {
            _mode = Mode.None;
            _verts.Clear();
            UpdatePreview();
            if (_optsRoot != null) _optsRoot.gameObject.SetActive(false);
        }

        void Update()
        {
            if (_mode == Mode.None || _world == null) return;
            // Swallow the pinch that pressed the toolbar button.
            if (Time.unscaledTime - _armedAt < 0.5f)
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

                // Ignore pinches aimed at UI (toolbar, panels).
                if (PointsAtUi(origin, fwd)) continue;

                if (TryHitTerrain(new Ray(origin, fwd), out var localPos))
                    PlaceVertex(localPos);
            }
        }

        static bool PointsAtUi(Vector3 origin, Vector3 fwd)
        {
            var hits = Physics.RaycastAll(origin, fwd, 8f, ~0, QueryTriggerInteraction.Collide);
            foreach (var hit in hits)
            {
                if (hit.transform.GetComponentInParent<XrChromeHud>() != null) return true;
                if (hit.transform.GetComponentInParent<XrLayersPanel>() != null) return true;
                if (hit.transform.GetComponentInParent<XrInfoPanel>() != null) return true;
                if (hit.transform.GetComponentInParent<XrVideoPanel>() != null) return true;
                if (hit.transform.GetComponentInParent<XrServerPanel>() != null) return true;
                if (hit.transform.GetComponentInParent<XrSettingsPanel>() != null) return true;
                if (hit.transform.GetComponentInParent<XrBasemapPanel>() != null) return true;
                if (hit.transform.GetComponentInParent<XrDrawOptHit>() != null) return true;
            }
            return false;
        }

        void PlaceVertex(Vector3 localPos)
        {
            switch (_mode)
            {
                case Mode.Point:
                    PublishPoint(localPos);
                    Cancel();
                    break;
                case Mode.Circle:
                    _verts.Add(localPos);
                    if (_verts.Count == 1)
                        _flash?.Invoke("Circle center set — pinch the edge");
                    else
                        FinishCircle();
                    UpdatePreview();
                    break;
                default:
                    _verts.Add(localPos);
                    _flash?.Invoke($"{(_mode == Mode.Route ? "Route" : "Shape")}: {_verts.Count} vertices\npress the tool again to finish");
                    UpdatePreview();
                    break;
            }
        }

        // ---------------- geodesy ----------------

        GeoMath.Geodetic Origin => new GeoMath.Geodetic(
            _config.originLat, _config.originLon, _config.originAlt);

        /// <summary>Local (under world root) → lat/lon via equirectangular inverse
        /// around the map origin (plenty accurate at AOI scale for placement).</summary>
        void LocalToLatLon(Vector3 local, out double lat, out double lon)
        {
            const double mPerDegLat = 111320.0;
            double mPerDegLon = mPerDegLat * Math.Cos(_config.originLat * Math.PI / 180.0);
            if (Math.Abs(mPerDegLon) < 1e-3) mPerDegLon = mPerDegLat;
            lat = _config.originLat + local.z / mPerDegLat;
            lon = _config.originLon + local.x / mPerDegLon;
        }

        float GroundLocalY(double lat, double lon, out float hae)
        {
            hae = (float)_config.originAlt;
            if (_terrain != null && _terrain.TrySampleHae(lat, lon, out var demHae))
                hae = demHae;
            var enu = GeoMath.GeodeticToEnu(new GeoMath.Geodetic(lat, lon, hae), Origin);
            return GeoMath.EnuToUnity(enu).y;
        }

        /// <summary>Raymarch the DEM surface (no terrain colliders exist).</summary>
        bool TryHitTerrain(Ray ray, out Vector3 localHit)
        {
            localHit = default;
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
                    // Crossed the surface — bisect for a clean hit.
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

        // ---------------- publishing ----------------

        NormalizedCot BaseCot(string type, double lat, double lon, float hae, string callsign)
        {
            var now = DateTime.UtcNow;
            return new NormalizedCot
            {
                uid = "takxr." + Guid.NewGuid().ToString("N").Substring(0, 12),
                type = type,
                how = "h-g-i-g-o",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddHours(24).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = callsign },
                detail = new CotDetail(),
            };
        }

        void Publish(NormalizedCot cot, string what)
        {
            bool sent = _direct != null && _direct.SendCot(CotXmlBuilder.Build(cot));
            _feed?.UpsertDirect(cot);
            _flash?.Invoke($"{what} {cot.contact.callsign}\n{(sent ? "sent to TAK server" : "LOCAL ONLY — TAK stream down")}");
            Debug.Log($"[XrDraw] {what} {cot.uid} sent={sent}");
        }

        void PublishPoint(Vector3 local)
        {
            LocalToLatLon(local, out var lat, out var lon);
            GroundLocalY(lat, lon, out var hae);
            // Web parity: point type a-{aff}-G (not b-m-p-s-m).
            string type = $"a-{_affiliation}-G";
            string cs = $"{_drawCallsign}-{++_dropCounter}";
            var cot = BaseCot(type, lat, lon, hae, cs);
            cot.detail.remarks = "Dropped from TAKXR headset";
            cot.detail.markerColor = _strokeCss;
            cot.detail.strokeColor = _strokeCss;
            Publish(cot, "Point");
        }

        void FinishShape()
        {
            bool closed = _mode == Mode.Polygon;
            var pts = new List<CotShapePoint>();
            double clat = 0, clon = 0;
            foreach (var v in _verts)
            {
                LocalToLatLon(v, out var lat, out var lon);
                GroundLocalY(lat, lon, out var hae);
                pts.Add(new CotShapePoint { lat = lat, lon = lon, hae = hae });
                clat += lat; clon += lon;
            }
            clat /= pts.Count; clon /= pts.Count;
            GroundLocalY(clat, clon, out var chae);

            var cot = BaseCot(closed ? "u-d-f" : "b-m-r", clat, clon, chae,
                $"XR-{(closed ? "SHAPE" : "RTE")}-{++_dropCounter}");
            cot.detail.shapePoints = pts;
            cot.detail.closed = closed;
            cot.detail.strokeColor = _strokeCss;
            if (closed) cot.detail.fillColor = _strokeCss;
            Publish(cot, closed ? "Shape" : "Route");
            Cancel();
        }

        void FinishCircle()
        {
            var center = _verts[0];
            float radiusM = Vector3.Distance(
                new Vector3(center.x, 0, center.z), new Vector3(_verts[1].x, 0, _verts[1].z));
            radiusM = Mathf.Max(radiusM, 5f);
            LocalToLatLon(center, out var lat, out var lon);
            GroundLocalY(lat, lon, out var hae);

            var cot = BaseCot("u-d-c-c", lat, lon, hae, $"XR-CIRC-{++_dropCounter}");
            cot.detail.ellipse = new CotEllipse { major = radiusM * 2f, minor = radiusM * 2f, angle = 0f };
            cot.detail.strokeColor = _strokeCss;
            cot.detail.fillColor = _strokeCss;
            Publish(cot, $"Circle r={Mathf.RoundToInt(radiusM)}m");
            Cancel();
        }

        void EnsureOptsUi()
        {
            if (_optsRoot != null)
            {
                _optsRoot.gameObject.SetActive(true);
                return;
            }
            _optsRoot = new GameObject("DrawOpts").transform;
            _optsRoot.SetParent(transform, false);
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "Bg";
            bg.transform.SetParent(_optsRoot, false);
            bg.transform.localPosition = new Vector3(0f, 0f, 0.008f);
            bg.transform.localScale = new Vector3(0.72f, 0.12f, 1f);
            Destroy(bg.GetComponent<Collider>());
            var r = bg.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"));
            mat.color = new Color(0.04f, 0.06f, 0.1f, 0.92f);
            mat.renderQueue = 2990;
            r.sharedMaterial = mat;
            _optsLabel = XrText.Make("Opts", _optsRoot, new Vector3(0f, 0f, -0.004f), "",
                0.0045f, new Color(0.85f, 0.95f, 1f, 0.95f));

            // Cycle buttons
            AddOptBtn("Aff", new Vector3(-0.22f, -0.055f, -0.002f), "AFF", CycleAffiliation);
            AddOptBtn("Col", new Vector3(0f, -0.055f, -0.002f), "COLOR", CycleStrokeColor);
            AddOptBtn("Cs", new Vector3(0.22f, -0.055f, -0.002f), "NAME", CycleDrawCallsign);
        }

        void AddOptBtn(string id, Vector3 pos, string label, Action onClick)
        {
            var t = new GameObject("Opt_" + id).transform;
            t.SetParent(_optsRoot, false);
            t.localPosition = pos;
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "Bg";
            q.transform.SetParent(t, false);
            q.transform.localScale = new Vector3(0.18f, 0.04f, 1f);
            Destroy(q.GetComponent<Collider>());
            var rr = q.GetComponent<Renderer>();
            var m = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"));
            m.color = new Color(0.14f, 0.28f, 0.4f, 0.96f);
            m.renderQueue = 3000;
            rr.sharedMaterial = m;
            XrText.Make("L", t, new Vector3(0f, 0f, -0.004f), label, 0.0038f, Color.white);
            var col = t.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.2f, 0.05f, 0.04f);
            col.isTrigger = true;
            var hit = t.gameObject.AddComponent<XrDrawOptHit>();
            hit.OnClick = onClick;
        }

        void RefreshOptsUi()
        {
            if (_optsLabel != null)
                _optsLabel.text = $"{_drawCallsign} · a-{_affiliation}-G · {_strokeCss}";
        }

        float _nextOptClick;

        void LateUpdate()
        {
            if (_mode == Mode.None || _cam == null || _optsRoot == null) return;
            if (!_optsRoot.gameObject.activeSelf) return;
            var camPos = _cam.position;
            _optsRoot.position = camPos + _cam.TransformDirection(new Vector3(0f, -0.42f, 1.6f));
            _optsRoot.rotation = XrUiFacing.RotationFacingUser(_optsRoot.position, camPos);

            if (Time.unscaledTime < _nextOptClick) return;
            if (Time.unscaledTime - _armedAt < 0.35f) return;

            for (int h = 0; h < 2; h++)
            {
                if (!XrHandPinchInput.TryGetAim(h, out var origin, out var fwd)) continue;
                if (!XrHandPinchInput.IsGrabbing(h)) continue;
                var hits = Physics.RaycastAll(origin, fwd, 6f, ~0, QueryTriggerInteraction.Collide);
                foreach (var hit in hits)
                {
                    var opt = hit.transform.GetComponentInParent<XrDrawOptHit>();
                    if (opt == null) continue;
                    opt.OnClick?.Invoke();
                    _nextOptClick = Time.unscaledTime + 0.4f;
                    return;
                }
            }
        }

        // ---------------- preview ----------------

        void UpdatePreview()
        {
            if (_verts.Count == 0)
            {
                if (_previewRoot != null) Destroy(_previewRoot.gameObject);
                _previewRoot = null;
                _preview = null;
                return;
            }

            if (_previewRoot == null)
            {
                _previewRoot = new GameObject("DrawPreview").transform;
                _previewRoot.SetParent(_world.Root, false);
                var go = new GameObject("Line");
                go.transform.SetParent(_previewRoot, false);
                _preview = go.AddComponent<LineRenderer>();
                _preview.useWorldSpace = false;
                _preview.startWidth = 6f;
                _preview.endWidth = 6f;
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                _preview.sharedMaterial = new Material(sh);
                var c = new Color(1f, 0.85f, 0.2f, 0.95f);
                if (_preview.sharedMaterial.HasProperty("_Color"))
                    _preview.sharedMaterial.SetColor("_Color", c);
                _preview.startColor = c;
                _preview.endColor = c;
            }

            // Old vertex balls off, rebuild (vertex counts are tiny).
            foreach (Transform child in _previewRoot)
                if (child.name.StartsWith("V")) Destroy(child.gameObject);

            _preview.positionCount = _verts.Count;
            for (int i = 0; i < _verts.Count; i++)
            {
                var p = _verts[i] + Vector3.up * 4f;
                _preview.SetPosition(i, p);
                var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = "V" + i;
                Destroy(ball.GetComponent<Collider>());
                ball.transform.SetParent(_previewRoot, false);
                ball.transform.localPosition = p;
                ball.transform.localScale = Vector3.one * 12f;
            }
        }
    }

    /// <summary>Hit target for draw-options chrome buttons.</summary>
    public class XrDrawOptHit : MonoBehaviour
    {
        public Action OnClick;
    }
}
