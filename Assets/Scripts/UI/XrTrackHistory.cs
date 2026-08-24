using System;
using System.Collections.Generic;
using TakXr.Core;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK Track History: records self breadcrumbs and draws a polyline on the map.
    /// Toggle via Tools → Track History.
    /// </summary>
    public class XrTrackHistory : MonoBehaviour
    {
        const float SampleIntervalSec = 2.5f;
        const float MinMoveMeters = 4f;
        const int MaxPoints = 800;
        const string PrefOn = "takxr.trackHistoryOn";

        AppConfig _config;
        XrWorldRoot _world;
        DemTerrainMap _terrain;
        Transform _cam;
        Action<string> _flash;

        bool _recording;
        float _nextSample;
        readonly List<Vector3> _localPts = new List<Vector3>();
        LineRenderer _line;
        Transform _lineRoot;

        public bool IsRecording => _recording;

        public static XrTrackHistory Create()
        {
            var go = new GameObject("XrTrackHistory");
            return go.AddComponent<XrTrackHistory>();
        }

        public void Configure(AppConfig config, XrWorldRoot world, DemTerrainMap terrain,
            Transform cam, Action<string> flash)
        {
            _config = config;
            _world = world;
            _terrain = terrain;
            _cam = cam;
            _flash = flash;
            _recording = PlayerPrefs.GetInt(PrefOn, 0) == 1;
            EnsureLine();
            if (_recording) _flash?.Invoke("Track History ON");
        }

        public void Toggle()
        {
            _recording = !_recording;
            PlayerPrefs.SetInt(PrefOn, _recording ? 1 : 0);
            PlayerPrefs.Save();
            if (!_recording)
            {
                _flash?.Invoke($"Track History OFF ({_localPts.Count} pts)");
            }
            else
            {
                SampleNow(force: true);
                _flash?.Invoke("Track History ON");
            }
            RefreshLine();
        }

        public void Clear()
        {
            _localPts.Clear();
            RefreshLine();
            _flash?.Invoke("Track cleared");
        }

        void EnsureLine()
        {
            if (_lineRoot != null || _world == null) return;
            _lineRoot = new GameObject("TrackLine").transform;
            _lineRoot.SetParent(_world.Root, false);
            _line = _lineRoot.gameObject.AddComponent<LineRenderer>();
            _line.useWorldSpace = false;
            _line.startWidth = 1.8f;
            _line.endWidth = 1.8f;
            _line.positionCount = 0;
            var sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(sh);
            var c = new Color(0.2f, 0.95f, 0.55f, 0.95f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            mat.renderQueue = 3050;
            _line.sharedMaterial = mat;
            _line.startColor = c;
            _line.endColor = c;
        }

        void Update()
        {
            if (!_recording || _world == null || _cam == null || _config == null) return;
            if (Time.unscaledTime < _nextSample) return;
            _nextSample = Time.unscaledTime + SampleIntervalSec;
            SampleNow(force: false);
        }

        void SampleNow(bool force)
        {
            EnsureLine();
            ApproximateViewerGeo(out double lat, out double lon);
            float hae = (float)_config.originAlt;
            if (_terrain != null && _terrain.TrySampleHae(lat, lon, out var dem))
                hae = dem + 2f;
            var origin = new GeoMath.Geodetic(_config.originLat, _config.originLon, _config.originAlt);
            var enu = GeoMath.GeodeticToEnu(new GeoMath.Geodetic(lat, lon, hae), origin);
            var local = GeoMath.EnuToUnity(enu);
            if (!force && _localPts.Count > 0)
            {
                float d = Vector3.Distance(_localPts[_localPts.Count - 1], local);
                if (d < MinMoveMeters) return;
            }
            _localPts.Add(local);
            while (_localPts.Count > MaxPoints) _localPts.RemoveAt(0);
            RefreshLine();
        }

        void RefreshLine()
        {
            if (_line == null) return;
            _line.positionCount = _localPts.Count;
            for (int i = 0; i < _localPts.Count; i++)
                _line.SetPosition(i, _localPts[i]);
            _line.enabled = _localPts.Count >= 2;
        }

        void ApproximateViewerGeo(out double lat, out double lon)
        {
            lat = _config.originLat;
            lon = _config.originLon;
            var local = _world.Root.InverseTransformPoint(_cam.position);
            const double mPerDegLat = 111320.0;
            double mPerDegLon = mPerDegLat * Math.Cos(_config.originLat * Math.PI / 180.0);
            if (Math.Abs(mPerDegLon) < 1e-3) mPerDegLon = mPerDegLat;
            lat = _config.originLat + local.z / mPerDegLat;
            lon = _config.originLon + local.x / mPerDegLon;
        }
    }
}
