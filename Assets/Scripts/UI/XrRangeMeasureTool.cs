using System;
using TakXr.Core;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK Range Tools: pinch two terrain points to draw a range &amp; bearing line.
    /// </summary>
    public class XrRangeMeasureTool : MonoBehaviour
    {
        public static bool IsArmed => Instance != null && Instance._armed;
        public static XrRangeMeasureTool Instance { get; private set; }

        AppConfig _config;
        XrWorldRoot _world;
        DemTerrainMap _terrain;
        Transform _cam;
        Action<string> _flash;

        bool _armed;
        bool _hasA;
        Vector3 _localA;
        Vector3 _localB;
        LineRenderer _line;
        Transform _labelRoot;
        TextMesh _label;
        readonly bool[] _wasGrabbing = new bool[2];
        float _armedAt;

        public static XrRangeMeasureTool Create()
        {
            var go = new GameObject("XrRangeMeasureTool");
            return go.AddComponent<XrRangeMeasureTool>();
        }

        public void Configure(AppConfig config, XrWorldRoot world, DemTerrainMap terrain,
            Transform cam, Action<string> flash)
        {
            _config = config;
            _world = world;
            _terrain = terrain;
            _cam = cam;
            _flash = flash;
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public void Toggle()
        {
            if (_armed) { Cancel(); return; }
            _armed = true;
            _hasA = false;
            _armedAt = Time.unscaledTime;
            EnsureVisuals();
            _flash?.Invoke("Range Tools: pinch point A, then B");
        }

        public void Cancel()
        {
            _armed = false;
            _hasA = false;
            if (_line != null) _line.positionCount = 0;
            if (_labelRoot != null) _labelRoot.gameObject.SetActive(false);
            _flash?.Invoke("Range Tools off");
        }

        void Update()
        {
            if (!_armed || _world == null) return;
            if (Time.unscaledTime < _armedAt + 0.4f) return;

            for (int h = 0; h < 2; h++)
            {
                if (XrHandPinchInput.IsUiBlocking(h)) continue;
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;
                if (!rising) continue;
                if (!TryHitTerrain(new Ray(origin, fwd), out var local))
                {
                    _flash?.Invoke("Range: aim at terrain");
                    continue;
                }
                if (!_hasA)
                {
                    _localA = local;
                    _hasA = true;
                    _flash?.Invoke("Point A set — pinch point B");
                }
                else
                {
                    _localB = local;
                    DrawMeasure();
                    _flash?.Invoke(_label != null ? _label.text : "R&B set");
                    _armed = false; // keep line visible; disarm further picks
                }
            }

            if (_labelRoot != null && _labelRoot.gameObject.activeSelf && _cam != null)
            {
                var mid = _world.Root.TransformPoint((_localA + _localB) * 0.5f + Vector3.up * 8f);
                _labelRoot.position = mid;
                _labelRoot.rotation = XrUiFacing.RotationFacingUser(mid, _cam.position);
            }
        }

        void DrawMeasure()
        {
            EnsureVisuals();
            _line.positionCount = 2;
            _line.SetPosition(0, _localA + Vector3.up * 2f);
            _line.SetPosition(1, _localB + Vector3.up * 2f);

            float dist = Vector3.Distance(
                new Vector3(_localA.x, 0, _localA.z),
                new Vector3(_localB.x, 0, _localB.z));
            float dx = _localB.x - _localA.x;
            float dz = _localB.z - _localA.z;
            float bearing = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            if (bearing < 0f) bearing += 360f;
            string units = dist >= 1000f ? $"{dist / 1000f:0.00} km" : $"{dist:0} m";
            if (_label != null) _label.text = $"{units}  {bearing:000}°";
            if (_labelRoot != null) _labelRoot.gameObject.SetActive(true);
        }

        void EnsureVisuals()
        {
            if (_line == null && _world != null)
            {
                var go = new GameObject("RangeLine");
                go.transform.SetParent(_world.Root, false);
                _line = go.AddComponent<LineRenderer>();
                _line.useWorldSpace = false;
                _line.startWidth = 1.4f;
                _line.endWidth = 1.4f;
                var c = new Color(1f, 0.75f, 0.25f, 0.98f);
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
                var mat = new Material(sh);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
                mat.renderQueue = 3060;
                _line.sharedMaterial = mat;
                _line.startColor = c;
                _line.endColor = c;
            }
            if (_labelRoot == null)
            {
                _labelRoot = new GameObject("RangeLabel").transform;
                _label = XrText.Make("T", _labelRoot, Vector3.zero, "", 0.006f,
                    new Color(1f, 0.85f, 0.4f, 0.98f));
                _labelRoot.gameObject.SetActive(false);
            }
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

        float GroundLocalY(double lat, double lon)
        {
            float hae = (float)_config.originAlt;
            if (_terrain != null && _terrain.TrySampleHae(lat, lon, out var demHae))
                hae = demHae;
            var enu = GeoMath.GeodeticToEnu(new GeoMath.Geodetic(lat, lon, hae), Origin);
            return GeoMath.EnuToUnity(enu).y;
        }

        bool TryHitTerrain(Ray ray, out Vector3 localHit)
        {
            localHit = default;
            var root = _world.Root;
            float t = 1f, step = 2f;
            Vector3 prevLocal = root.InverseTransformPoint(ray.origin + ray.direction * t);
            LocalToLatLon(prevLocal, out var lat0, out var lon0);
            float prevDelta = prevLocal.y - GroundLocalY(lat0, lon0);
            const float maxDist = 12000f;
            while (t < maxDist)
            {
                t += step;
                step = Mathf.Min(step * 1.06f, 120f);
                var local = root.InverseTransformPoint(ray.origin + ray.direction * t);
                LocalToLatLon(local, out var lat, out var lon);
                float delta = local.y - GroundLocalY(lat, lon);
                if (prevDelta > 0f && delta <= 0f)
                {
                    float lo = t - step, hi = t;
                    for (int i = 0; i < 12; i++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        var ml = root.InverseTransformPoint(ray.origin + ray.direction * mid);
                        LocalToLatLon(ml, out var mlat, out var mlon);
                        if (ml.y - GroundLocalY(mlat, mlon) > 0f) lo = mid; else hi = mid;
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
