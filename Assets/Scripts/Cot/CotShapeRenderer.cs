using System.Collections.Generic;
using TakXr.Core;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.Cot
{
    /// <summary>
    /// Renders drawing CoTs — routes (b-m-r), polygons (u-d-f) and circles
    /// (u-d-c-c / ellipse detail) — as terrain-clamped LineRenderers under the
    /// world root, matching the web viewer's drawing layer.
    /// </summary>
    public class CotShapeRenderer : MonoBehaviour
    {
        AppConfig _config;
        CotFeedClient _feed;
        XrWorldRoot _world;
        DemTerrainMap _terrain;
        Transform _shapesRoot;
        readonly Dictionary<string, GameObject> _shapes = new Dictionary<string, GameObject>();
        readonly Dictionary<string, string> _shapeSig = new Dictionary<string, string>();
        bool _dirty;
        float _nextSync;

        public void Configure(AppConfig config, CotFeedClient feed, XrWorldRoot world, DemTerrainMap terrain)
        {
            _config = config;
            _feed = feed;
            _world = world;
            if (_terrain != null) _terrain.TerrainChanged -= OnTerrainChanged;
            _terrain = terrain;
            if (_terrain != null) _terrain.TerrainChanged += OnTerrainChanged;
            _shapesRoot = new GameObject("CotShapes").transform;
            _shapesRoot.SetParent(_world.Root, false);
            _feed.Changed += () => _dirty = true;
            _dirty = true;
        }

        void OnDestroy()
        {
            if (_terrain != null) _terrain.TerrainChanged -= OnTerrainChanged;
        }

        void OnTerrainChanged()
        {
            // Force rebuild so polylines re-sample finer DEM meshes.
            _shapeSig.Clear();
            _dirty = true;
            _nextSync = 0f;
        }

        void Update()
        {
            if (!_dirty || Time.unscaledTime < _nextSync) return;
            _dirty = false;
            _nextSync = Time.unscaledTime + 1.5f;
            Sync();
        }

        void Sync()
        {
            if (_feed == null || _shapesRoot == null) return;
            var seen = new HashSet<string>();

            foreach (var cot in _feed.Cots.Values)
            {
                var d = cot?.detail;
                if (d == null) continue;
                bool hasPts = d.shapePoints != null && d.shapePoints.Count >= 2;
                bool hasEllipse = d.ellipse != null && d.ellipse.major > 0f;
                if (!hasPts && !hasEllipse) continue;

                seen.Add(cot.uid);
                string sig = ShapeSignature(cot);
                if (_shapes.TryGetValue(cot.uid, out var existing))
                {
                    if (_shapeSig.TryGetValue(cot.uid, out var prev) && prev == sig)
                        continue; // unchanged
                    Destroy(existing);
                    _shapes.Remove(cot.uid);
                }
                _shapeSig[cot.uid] = sig;

                var go = new GameObject("Shape:" + (cot.Callsign ?? cot.uid));
                go.transform.SetParent(_shapesRoot, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.startWidth = 8f;
                lr.endWidth = 8f;
                var color = ParseColor(d.strokeColor, new Color(0f, 0.82f, 1f, 0.95f));
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                lr.sharedMaterial = new Material(sh);
                if (lr.sharedMaterial.HasProperty("_Color")) lr.sharedMaterial.SetColor("_Color", color);
                lr.startColor = color;
                lr.endColor = color;

                if (hasPts)
                {
                    lr.loop = d.closed;
                    lr.positionCount = d.shapePoints.Count;
                    for (int i = 0; i < d.shapePoints.Count; i++)
                    {
                        var p = d.shapePoints[i];
                        lr.SetPosition(i, LocalPos(p.lat, p.lon));
                    }
                }
                else
                {
                    // Circle/ellipse around the CoT point.
                    const int segs = 48;
                    lr.loop = true;
                    lr.positionCount = segs;
                    float a = d.ellipse.major / 2f;
                    float b = d.ellipse.minor > 0f ? d.ellipse.minor / 2f : a;
                    float rot = d.ellipse.angle * Mathf.Deg2Rad;
                    var centerEnu = GeoMath.GeodeticToEnu(
                        new GeoMath.Geodetic(cot.point.lat, cot.point.lon, 0), Origin);
                    for (int i = 0; i < segs; i++)
                    {
                        float ang = i / (float)segs * Mathf.PI * 2f;
                        float e = Mathf.Cos(ang) * a;
                        float n = Mathf.Sin(ang) * b;
                        float er = e * Mathf.Cos(rot) - n * Mathf.Sin(rot);
                        float nr = e * Mathf.Sin(rot) + n * Mathf.Cos(rot);
                        double lat = cot.point.lat + nr / 111320.0;
                        double lon = cot.point.lon +
                                     er / (111320.0 * System.Math.Cos(cot.point.lat * System.Math.PI / 180.0));
                        lr.SetPosition(i, LocalPos(lat, lon));
                    }
                }

                _shapes[cot.uid] = go;
            }

            // Remove shapes whose CoT left the feed (layer removed / stale swept).
            List<string> dead = null;
            foreach (var kv in _shapes)
                if (!seen.Contains(kv.Key)) (dead ??= new List<string>()).Add(kv.Key);
            if (dead != null)
            {
                foreach (var uid in dead)
                {
                    Destroy(_shapes[uid]);
                    _shapes.Remove(uid);
                    _shapeSig.Remove(uid);
                }
            }
        }

        static string ShapeSignature(NormalizedCot cot)
        {
            var d = cot.detail;
            var sb = new System.Text.StringBuilder();
            sb.Append(d.strokeColor).Append('|').Append(d.fillColor).Append('|').Append(d.closed);
            if (d.ellipse != null && d.ellipse.major > 0f)
                sb.Append("|e").Append(d.ellipse.major).Append(',').Append(d.ellipse.minor).Append(',').Append(d.ellipse.angle);
            if (d.shapePoints != null)
            {
                sb.Append("|p").Append(d.shapePoints.Count);
                foreach (var p in d.shapePoints)
                    sb.Append(';').Append(p.lat.ToString("F5")).Append(',').Append(p.lon.ToString("F5"));
            }
            return sb.ToString();
        }

        GeoMath.Geodetic Origin => new GeoMath.Geodetic(
            _config.originLat, _config.originLon, _config.originAlt);

        Vector3 LocalPos(double lat, double lon)
        {
            float hae = (float)_config.originAlt;
            if (_terrain != null && _terrain.TrySampleHae(lat, lon, out var demHae)) hae = demHae;
            var enu = GeoMath.GeodeticToEnu(new GeoMath.Geodetic(lat, lon, hae), Origin);
            var pos = GeoMath.EnuToUnity(enu);
            pos.y += 5f; // float above terrain like markers
            return pos;
        }

        static Color ParseColor(string css, Color fallback)
        {
            if (!string.IsNullOrEmpty(css) && ColorUtility.TryParseHtmlString(css, out var c))
            {
                c.a = 0.95f;
                return c;
            }
            return fallback;
        }
    }
}
