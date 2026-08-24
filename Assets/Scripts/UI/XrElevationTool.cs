using System;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK Elevation tool: live HAE/MSL readout under the aim ray; pinch drops a spot marker.
    /// </summary>
    public class XrElevationTool : MonoBehaviour
    {
        public static bool IsArmed => Instance != null && Instance._armed;
        public static XrElevationTool Instance { get; private set; }

        AppConfig _config;
        CotFeedClient _feed;
        TakDirectHub _direct;
        XrWorldRoot _world;
        DemTerrainMap _terrain;
        Transform _cam;
        Action<string> _flash;

        bool _armed;
        Transform _hudRoot;
        TextMesh _hudText;
        readonly bool[] _wasGrabbing = new bool[2];
        float _armedAt;
        int _spot;

        public static XrElevationTool Create()
        {
            var go = new GameObject("XrElevationTool");
            return go.AddComponent<XrElevationTool>();
        }

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
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public void Toggle()
        {
            _armed = !_armed;
            _armedAt = Time.unscaledTime;
            EnsureHud();
            if (_hudRoot != null) _hudRoot.gameObject.SetActive(_armed);
            _flash?.Invoke(_armed ? "Elevation: aim at terrain · pinch to mark" : "Elevation off");
        }

        void LateUpdate()
        {
            if (!_armed || _cam == null || _world == null || _config == null) return;
            EnsureHud();

            // Prefer right-hand aim, else left, else camera forward.
            Ray ray = new Ray(_cam.position, _cam.forward);
            for (int h = 1; h >= 0; h--)
            {
                if (XrHandPinchInput.TryGetAim(h, out var o, out var f))
                {
                    ray = new Ray(o, f);
                    break;
                }
            }

            string msg = "Elevation: no terrain";
            if (TryHitTerrain(ray, out var local, out double lat, out double lon, out float hae))
            {
                // Approximate MSL ≈ HAE (EGM offset unknown without geoid).
                msg = $"EL {hae:0.#} m HAE\n{lat:F5}, {lon:F5}";
            }
            if (_hudText != null) _hudText.text = msg;
            if (_hudRoot != null)
            {
                var pos = _cam.position + _cam.TransformDirection(new Vector3(0.35f, -0.18f, 1.2f));
                _hudRoot.position = pos;
                _hudRoot.rotation = XrUiFacing.RotationFacingUser(pos, _cam.position);
            }

            if (Time.unscaledTime < _armedAt + 0.4f) return;
            for (int h = 0; h < 2; h++)
            {
                if (XrHandPinchInput.IsUiBlocking(h)) continue;
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;
                if (!rising) continue;
                if (!TryHitTerrain(new Ray(origin, fwd), out _, out lat, out lon, out hae)) continue;
                DropSpot(lat, lon, hae);
            }
        }

        void DropSpot(double lat, double lon, float hae)
        {
            _spot++;
            var now = DateTime.UtcNow;
            var cot = new NormalizedCot
            {
                uid = "takxr.el." + Guid.NewGuid().ToString("N").Substring(0, 10),
                type = "b-m-p-s-m",
                how = "h-g-i-g-o",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddHours(24).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = $"EL-{_spot} {hae:0}m" },
                detail = new CotDetail { remarks = "Elevation spot", markerColor = "#ffcc00" },
            };
            bool sent = _direct != null && _direct.SendCot(CotXmlBuilder.Build(cot));
            _feed?.UpsertDirect(cot);
            _flash?.Invoke($"Elevation mark {hae:0.#} m{(sent ? "" : " (local)")}");
        }

        void EnsureHud()
        {
            if (_hudRoot != null) return;
            _hudRoot = new GameObject("ElevHud").transform;
            _hudRoot.SetParent(transform, false);
            _hudText = XrText.Make("T", _hudRoot, Vector3.zero, "", 0.0045f,
                new Color(0.95f, 0.9f, 0.55f, 0.95f),
                TextAnchor.MiddleLeft, TextAlignment.Left);
            _hudRoot.gameObject.SetActive(false);
        }

        GeoMath.Geodetic Origin =>
            new GeoMath.Geodetic(_config.originLat, _config.originLon, _config.originAlt);

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

        bool TryHitTerrain(Ray ray, out Vector3 localHit, out double lat, out double lon, out float hae)
        {
            localHit = default;
            lat = lon = 0;
            hae = 0;
            var root = _world.Root;
            float t = 1f, step = 2f;
            Vector3 prevLocal = root.InverseTransformPoint(ray.origin + ray.direction * t);
            LocalToLatLon(prevLocal, out var lat0, out var lon0);
            float prevDelta = prevLocal.y - GroundLocalY(lat0, lon0, out _);
            const float maxDist = 12000f;
            while (t < maxDist)
            {
                t += step;
                step = Mathf.Min(step * 1.06f, 120f);
                var local = root.InverseTransformPoint(ray.origin + ray.direction * t);
                LocalToLatLon(local, out var la, out var lo);
                float delta = local.y - GroundLocalY(la, lo, out _);
                if (prevDelta > 0f && delta <= 0f)
                {
                    float loT = t - step, hiT = t;
                    for (int i = 0; i < 12; i++)
                    {
                        float mid = (loT + hiT) * 0.5f;
                        var ml = root.InverseTransformPoint(ray.origin + ray.direction * mid);
                        LocalToLatLon(ml, out var mlat, out var mlon);
                        if (ml.y - GroundLocalY(mlat, mlon, out _) > 0f) loT = mid; else hiT = mid;
                    }
                    localHit = root.InverseTransformPoint(ray.origin + ray.direction * ((loT + hiT) * 0.5f));
                    LocalToLatLon(localHit, out lat, out lon);
                    GroundLocalY(lat, lon, out hae);
                    return true;
                }
                prevDelta = delta;
            }
            return false;
        }
    }
}
