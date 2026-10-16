using System.Collections.Generic;
using TakXr.Core;
using TakXr.Cot;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// Interactive 3D Tactical World Globe widget mounted on the spatial XR dock.
    /// Renders a 3D lit sphere with lat/lon graticules, self-position beacon,
    /// and live 3D CoT location blips (color-coded by team/affiliation and elevated
    /// for airborne tracks). Tapping toggles between Global Lat/Lon view and
    /// Local AOI 3D Hemisphere Radar view (and expands the globe size).
    /// </summary>
    public class XrMiniWorldGlobe : MonoBehaviour
    {
        const int MaxBlips = 64;

        AppConfig _config;
        CotFeedClient _feed;
        Transform _cam;

        Transform _globeSphere;
        Transform _blipsRoot;
        Transform _equatorRing;
        TextMesh _modeLabel;
        readonly List<Transform> _blipPool = new List<Transform>();
        readonly List<Renderer> _blipRends = new List<Renderer>();
        bool _aoiRadarMode = true; // true = magnified AOI on 3D globe, false = full Earth lat/lon
        bool _expanded;
        float _spinOffsetDeg;
        float _nextRefresh;

        static Texture2D _globeTex;
        static Material _globeMat;

        public bool IsAoiRadarMode => _aoiRadarMode;
        public bool IsExpanded => _expanded;

        public void Configure(AppConfig config, CotFeedClient feed, Transform cam)
        {
            _config = config;
            _feed = feed;
            _cam = cam;
            EnsureBuilt();
        }

        public void CycleGlobeMode(System.Action<string> flash = null)
        {
            if (_aoiRadarMode && !_expanded)
            {
                _expanded = true;
                flash?.Invoke("3D World Globe: Expanded AOI 3D Sphere");
            }
            else if (_aoiRadarMode && _expanded)
            {
                _aoiRadarMode = false;
                flash?.Invoke("3D World Globe: Global Earth Lat/Lon View");
            }
            else
            {
                _aoiRadarMode = true;
                _expanded = false;
                flash?.Invoke("3D World Globe: Compact AOI 3D View");
            }
            ApplySizeAndLabel();
            RefreshBlips();
        }

        void EnsureBuilt()
        {
            if (_globeSphere != null) return;

            var sphereGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereGo.name = "Globe3DSphere";
            Destroy(sphereGo.GetComponent<Collider>());
            sphereGo.transform.SetParent(transform, false);
            sphereGo.transform.localPosition = Vector3.zero;
            _globeSphere = sphereGo.transform;

            var r = sphereGo.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = GetGlobeMaterial();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            // Equatorial tactical ring around the 3D globe
            var ringGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ringGo.name = "EquatorRing";
            Destroy(ringGo.GetComponent<Collider>());
            ringGo.transform.SetParent(_globeSphere, false);
            ringGo.transform.localPosition = Vector3.zero;
            ringGo.transform.localScale = new Vector3(1.14f, 0.012f, 1.14f);
            var rr = ringGo.GetComponent<Renderer>();
            if (rr != null)
                rr.sharedMaterial = Cot3dModelFactory.GetLitMaterial(new Color(0.15f, 0.82f, 1f, 1f), 0.45f);
            _equatorRing = ringGo.transform;

            // Self / Origin North marker at top of globe
            var selfPin = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            selfPin.name = "OriginBeacon";
            Destroy(selfPin.GetComponent<Collider>());
            selfPin.transform.SetParent(_globeSphere, false);
            selfPin.transform.localPosition = new Vector3(0f, 0f, -0.515f);
            selfPin.transform.localScale = Vector3.one * 0.08f;
            var sr = selfPin.GetComponent<Renderer>();
            if (sr != null)
                sr.sharedMaterial = Cot3dModelFactory.GetLitMaterial(new Color(1f, 1f, 1f, 1f), 0.8f);

            _blipsRoot = new GameObject("CotBlips3D").transform;
            _blipsRoot.SetParent(_globeSphere, false);
            _blipsRoot.localPosition = Vector3.zero;
            _blipsRoot.localRotation = Quaternion.identity;
            _blipsRoot.localScale = Vector3.one;

            _modeLabel = XrText.Make("GlobeLbl", transform, new Vector3(0f, -0.092f, -0.02f),
                "3D WORLD · AOI", 0.0042f, new Color(0.65f, 0.90f, 1f, 0.95f));

            ApplySizeAndLabel();
        }

        void ApplySizeAndLabel()
        {
            if (_globeSphere == null) return;
            float d = _expanded ? 0.21f : 0.125f;
            _globeSphere.localScale = Vector3.one * d;
            _globeSphere.localPosition = new Vector3(0f, _expanded ? 0.055f : 0.012f, -0.04f);
            if (_modeLabel != null)
            {
                _modeLabel.text = _aoiRadarMode
                    ? (_expanded ? "3D GLOBE: AOI XL" : "3D GLOBE: AOI")
                    : "3D GLOBE: EARTH";
            }
        }

        void Update()
        {
            if (_globeSphere == null) return;
            _spinOffsetDeg = (_spinOffsetDeg + Time.unscaledDeltaTime * 8f) % 360f;

            if (_aoiRadarMode)
            {
                // Tilt slightly toward user so top/front hemisphere shows local AOI clearly
                _globeSphere.localRotation = Quaternion.Euler(-22f, Mathf.Sin(Time.unscaledTime * 0.6f) * 8f, 0f);
            }
            else
            {
                // Slowly rotate Earth globe around Y while keeping AOI latitude tilted toward user
                float oLat = _config != null ? (float)_config.originLat : 35f;
                _globeSphere.localRotation = Quaternion.Euler(-oLat * 0.4f, _spinOffsetDeg, 0f);
            }

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.5f;
                RefreshBlips();
            }
        }

        void RefreshBlips()
        {
            if (_feed == null || _blipsRoot == null) return;

            double oLat = _config != null ? _config.originLat : 35.96;
            double oLon = _config != null ? _config.originLon : -83.92;

            int idx = 0;
            foreach (var cot in _feed.Cots.Values)
            {
                if (cot?.point == null || string.IsNullOrEmpty(cot.uid) || cot.IsChatMessage) continue;
                if (idx >= MaxBlips) break;

                var blip = GetOrCreateBlip(idx, out var rend);
                blip.gameObject.SetActive(true);

                float radius = cot.IsAirborne ? 0.56f : 0.515f;
                Vector3 dir;
                if (_aoiRadarMode)
                {
                    // Map ±0.12 deg (~13 km) onto the front/upper hemisphere of the 3D globe
                    float dLat = Mathf.Clamp((float)(cot.point.lat - oLat) / 0.08f, -1f, 1f);
                    float dLon = Mathf.Clamp((float)(cot.point.lon - oLon) / 0.08f, -1f, 1f);
                    float lonAng = dLon * 70f * Mathf.Deg2Rad;
                    float latAng = dLat * 70f * Mathf.Deg2Rad;
                    // Front of sphere facing user in XrUiFacing is -Z
                    float x = Mathf.Sin(lonAng) * Mathf.Cos(latAng);
                    float y = Mathf.Sin(latAng);
                    float z = -Mathf.Cos(lonAng) * Mathf.Cos(latAng);
                    dir = new Vector3(x, y, z).normalized;
                }
                else
                {
                    // True spherical Earth coordinates centered on (oLat, oLon) at front (-Z)
                    float latRad = (float)cot.point.lat * Mathf.Deg2Rad;
                    float lonRad = (float)(cot.point.lon - oLon) * Mathf.Deg2Rad;
                    float x = Mathf.Cos(latRad) * Mathf.Sin(lonRad);
                    float y = Mathf.Sin(latRad);
                    float z = -Mathf.Cos(latRad) * Mathf.Cos(lonRad);
                    dir = new Vector3(x, y, z).normalized;
                }

                blip.localPosition = dir * radius;
                float blipSize = cot.IsEmergency ? 0.095f : (cot.IsAirborne ? 0.075f : 0.062f);
                blip.localScale = Vector3.one * blipSize;

                if (rend != null)
                {
                    Color col = CotMarkerView.GetAffiliationOrTeamColor(cot);
                    rend.sharedMaterial = Cot3dModelFactory.GetLitMaterial(col, 0.65f);
                }
                idx++;
            }

            for (int i = idx; i < _blipPool.Count; i++)
            {
                if (_blipPool[i] != null) _blipPool[i].gameObject.SetActive(false);
            }
        }

        Transform GetOrCreateBlip(int index, out Renderer rend)
        {
            while (_blipPool.Count <= index)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Blip_" + _blipPool.Count;
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(_blipsRoot, false);
                var r = go.GetComponent<Renderer>();
                if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _blipPool.Add(go.transform);
                _blipRends.Add(r);
            }
            rend = _blipRends[index];
            return _blipPool[index];
        }

        static Material GetGlobeMaterial()
        {
            if (_globeMat != null) return _globeMat;
            if (_globeTex == null) _globeTex = BuildGlobeTexture();

            var sh = Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Standard")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Sprites/Default");
            _globeMat = new Material(sh);
            _globeMat.mainTexture = _globeTex;
            if (_globeMat.HasProperty("_BaseMap")) _globeMat.SetTexture("_BaseMap", _globeTex);
            if (_globeMat.HasProperty("_BaseColor")) _globeMat.SetColor("_BaseColor", Color.white);
            if (_globeMat.HasProperty("_Color")) _globeMat.SetColor("_Color", Color.white);
            if (_globeMat.HasProperty("_Smoothness")) _globeMat.SetFloat("_Smoothness", 0.65f);
            if (_globeMat.HasProperty("_EmissionColor"))
            {
                _globeMat.EnableKeyword("_EMISSION");
                _globeMat.SetColor("_EmissionColor", new Color(0.06f, 0.22f, 0.36f, 1f));
            }
            return _globeMat;
        }

        static Texture2D BuildGlobeTexture()
        {
            const int W = 256;
            const int H = 128;
            var px = new Color32[W * H];
            var ocean = new Color32(10, 28, 48, 255);
            var land = new Color32(20, 62, 78, 255);
            var grid = new Color32(45, 155, 210, 255);
            var equator = new Color32(80, 220, 255, 255);

            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)W;
                float v = y / (float)H;
                // Procedural continental harmonic pattern + tactical lat/lon graticule
                float n = Mathf.Sin(u * Mathf.PI * 6f) * Mathf.Cos(v * Mathf.PI * 4f)
                        + 0.5f * Mathf.Sin((u + v) * Mathf.PI * 10f);
                Color32 c = n > 0.18f ? land : ocean;

                bool latLine = (y % 16) == 0;
                bool lonLine = (x % 16) == 0;
                if (Mathf.Abs(y - H / 2) <= 1) c = equator;
                else if (latLine || lonLine) c = grid;

                px[y * W + x] = c;
            }

            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }
    }
}
