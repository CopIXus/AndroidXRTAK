using System.Collections;
using System.Collections.Generic;
using TakXr.Core;
using TakXr.Map;
using TakXr.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Cot
{
    /// <summary>
    /// Volumetric 3D CoT marker view:
    /// - 3D spheres ("balls") with equatorial rings and 3D course cones for units/dots
    /// - 3D fixed-wing airplanes and 3D rotary helicopters with altitude drop-lines
    /// - 3D gimbal video camera pods for video streams
    /// - 3D standing observer pawns with volumetric 3D FOV cones
    /// - 3D extruded relief meshes on 3D affiliation spheres/rings for all 2D ATAK/2525 icons
    /// Includes constant-angular-size scaling, terrain clamping, and callsign leader-line stacking.
    /// </summary>
    public class CotMarkerView : MonoBehaviour
    {
        const float AngularSize = 0.048f;
        const float MinMeters = 14f;
        // Cap so huge markers don't steal controller rays meant for the toolbar.
        const float MaxMeters = 120f;

        // Callsign labels: constant ANGULAR size (worldH ≈ dist × LabelAngular) with
        // a near soft-clamp so close labels don't dominate the FOV.
        const float LabelAngular = 0.024f;       // ~1.4° of visual height at mid/far
        const float LabelNearAngular = 0.012f;   // tighter when camDist is small
        const float LabelNearSoftM = 70f;        // blend toward near angular below this
        const float LabelMinCharSize = 0.016f;   // lower floor — near clamp shrinks further
        const float LabelNearMaxWorldH = 16f;    // ceiling inside the near zone only
        const float LabelAbsMaxWorldH = 300f;    // sanity ceiling (only binds beyond 12.5 km)
        const float LabelFontSize = 256f;        // match XrText.CrispFontSize raster
        const float LabelHover3D = 0.52f;        // fraction of marker meters above 3D center

        // Crowded markers (several CoTs at nearly the same spot) shrink so the
        // pile stays readable; labels keep stacking with leader lines.
        const float ClusterScaleFactor = 0.6f;

        // Terrain clamp: keep every 3D object above the rendered surface.
        const float GroundLiftSampledM = 12f;
        const float GroundLiftFallbackM = 6f;

        /// <summary>Global marker ICON size multiplier from settings (default 1).</summary>
        public static float ScaleMultiplier { get; set; } = 1f;

        /// <summary>Global callsign LABEL size multiplier from settings (default 1).
        /// Split from ScaleMultiplier so icon and text sizes tune independently.</summary>
        public static float LabelScaleMultiplier { get; set; } = 1f;

        /// <summary>When true, CoTs occluded behind hills/buildings within XrayMaxDistanceMeters
        /// render semi-transparently (X-Ray silhouette) through terrain.</summary>
        public static bool XrayOccludedEnabled { get; set; } = true;
        public static float XrayMaxDistanceMeters { get; set; } = 12000f;
        public static float XrayBaseAlpha { get; set; } = 0.38f;

        public string Uid { get; private set; }
        public NormalizedCot Cot { get; private set; }

        /// <summary>Priority for label-collision stacking (higher wins lower stack).</summary>
        public int LabelPriority { get; private set; }

        Transform _billboardRoot;
        GameObject _modelInstance;
        readonly List<Renderer> _modelRenderers = new List<Renderer>(12);
        readonly List<Material> _modelOrigMats = new List<Material>(12);
        bool _isOccludedBehindTerrain;
        bool _xrayApplied;
        int _lastXrayAlphaQuant = -1;
        int _occlusionTickOffset;
        Transform _emergencyHalo;
        TextMesh _label;
        MeshRenderer _labelRend;
        TextMesh _labelShadow;
        Transform _labelPlate;
        Renderer _labelPlateRend;
        LineRenderer _leader;
        LineRenderer _altDropLine;
        BoxCollider _hit;
        Texture2D _runtimeTex;
        Coroutine _iconLoad;
        bool _isVideo;
        bool _isAirborne;
        bool _isEmergency;
        /// <summary>True when the 3D model is oriented to world compass heading (balls with course,
        /// 3D aircraft, helicopters, video pods, observers). False when the 3D extruded icon
        /// medallion yaws toward the viewer.</summary>
        bool _worldOriented3D;
        float _headingDeg;
        string _modelKey;
        /// <summary>Extra local offset applied to the callsign for collision avoidance.
        /// Does NOT move the ground glyph.</summary>
        Vector3 _labelAvoidOffset;
        float _lastMarkerMeters = MinMeters;
        /// <summary>1 normally, ClusterScaleFactor when crowded (set by CotLayerController).</summary>
        float _clusterScale = 1f;
        /// <summary>Terrain surface Y (marker-local frame) under this marker; NegativeInfinity = unset.</summary>
        float _groundLocalY = float.NegativeInfinity;
        bool _groundSampled;
        DemTerrainMap _terrain;
        GeoMath.Geodetic _origin;
        double _clampLat;
        double _clampLon;
        bool _haveClampGeo;
        static Material _sharedUnlit;
        static Material _leaderMat;
        static Material _altLineMat;
        static Texture2D _dcfsIcon;

        // ATAK team colors (Settings > My Callsign > Team) — mirrors web milIcons.ts.
        static readonly Dictionary<string, string> TeamColors = new Dictionary<string, string>
        {
            { "White", "#FFFFFF" }, { "Yellow", "#FFFF00" }, { "Orange", "#FF8000" },
            { "Magenta", "#FF00FF" }, { "Red", "#FF0000" }, { "Maroon", "#800000" },
            { "Purple", "#800080" }, { "Dark Blue", "#00008B" }, { "Blue", "#0000FF" },
            { "Cyan", "#00FFFF" }, { "Teal", "#008080" }, { "Green", "#00FF00" },
            { "Dark Green", "#006400" }, { "Brown", "#A52A2A" },
        };

        public void Bind(NormalizedCot cot, AppConfig config, Transform cameraTransform)
        {
            Uid = cot.uid;
            Cot = cot;
            _occlusionTickOffset = Mathf.Abs((cot.uid ?? "").GetHashCode()) % 6;
            if (config != null)
            {
                XrayOccludedEnabled = config.xrayOccludedCots;
                if (config.xrayMaxDistanceMeters > 100f)
                    XrayMaxDistanceMeters = config.xrayMaxDistanceMeters;
                if (config.xrayOccludedAlpha > 0.05f)
                    XrayBaseAlpha = config.xrayOccludedAlpha;
            }
            EnsureVisuals();
            Refresh(cot, config);
            FaceCamera(cameraTransform);
            UpdateAngularScale(cameraTransform);
        }

        public void Refresh(NormalizedCot cot, AppConfig config)
        {
            Cot = cot;
            gameObject.name = $"COT:{cot.Callsign}";

            // ALL classification goes through CotClassifier — the branch order is
            // contract-tested at app start (CotClassifier.SelfTest).
            var kind = CotClassifier.Classify(cot);
            LabelPriority = ComputeLabelPriority(kind);
            _isVideo = kind == MarkerKind.Video;
            _isAirborne = kind == MarkerKind.AircraftFixed || kind == MarkerKind.AircraftRotary || cot.IsAirborne;
            _isEmergency = cot.IsEmergency;

            if (_label != null)
            {
                string text = cot.Callsign ?? cot.uid;
                if (_isEmergency) text = "(!) " + text;
                _label.text = text;
                _label.fontSize = (int)LabelFontSize;
                _label.color = _isEmergency
                    ? new Color(1f, 0.35f, 0.35f, 1f)
                    : new Color(1f, 1f, 0.95f, 0.98f);
                XrText.Sharpen(_label);
                ForceFontBilinear(_label);
                if (_labelShadow != null)
                {
                    _labelShadow.text = _label.text;
                    _labelShadow.fontSize = _label.fontSize;
                }
            }

            if (_iconLoad != null) StopCoroutine(_iconLoad);

            switch (kind)
            {
                case MarkerKind.Observer:
                    ApplyObserver3D(cot);
                    break;

                case MarkerKind.Video:
                    ApplyVideoPod3D(cot);
                    break;

                case MarkerKind.Dcfs:
                    ApplyDcfs3D();
                    break;

                case MarkerKind.TeamMember:
                    ApplyUnitBall3D(cot);
                    break;

                case MarkerKind.AircraftFixed:
                    ApplyAircraft3D(cot, rotary: false);
                    break;

                case MarkerKind.AircraftRotary:
                    ApplyAircraft3D(cot, rotary: true);
                    break;

                case MarkerKind.LocalIcon:
                    if (!TryApplyLocalIconset3D(cot))
                        ApplyFallbackBall3D(cot);
                    break;

                case MarkerKind.RemoteIcon:
                    var color = ParseColor(cot.detail?.markerColor, UnitDotColor(cot));
                    ApplyRemoteIcon3D(cot, config, color);
                    break;

                default: // MarkerKind.Dot
                    ApplyFallbackBall3D(cot);
                    break;
            }

            UpdateEmergencyHalo();
        }

        static GameObject ModelGo(Transform built) => built != null ? built.gameObject : null;

        void ReplaceModel(string key, System.Func<GameObject> builder)
        {
            if (_modelKey == key && _modelInstance != null) return;
            _modelKey = key;
            if (_modelInstance != null)
                Destroy(_modelInstance);
            _modelRenderers.Clear();
            _modelOrigMats.Clear();
            _xrayApplied = false;
            _lastXrayAlphaQuant = -1;
            _modelInstance = builder();
            if (_modelInstance != null && _billboardRoot != null)
            {
                _modelInstance.transform.SetParent(_billboardRoot, false);
                _modelInstance.transform.localPosition = Vector3.zero;
                _modelInstance.transform.localRotation = Quaternion.identity;
                _modelInstance.transform.localScale = Vector3.one;
                var rends = _modelInstance.GetComponentsInChildren<Renderer>(true);
                foreach (var r in rends)
                {
                    if (r == null) continue;
                    _modelRenderers.Add(r);
                    _modelOrigMats.Add(r.sharedMaterial);
                }
            }
        }

        void ApplyUnitBall3D(NormalizedCot cot)
        {
            var color = UnitDotColor(cot);
            float course = cot.detail?.track != null ? cot.detail.track.course : float.NaN;
            bool hasCourse = !float.IsNaN(course);
            _worldOriented3D = true;
            _headingDeg = hasCourse ? course : 0f;
            string hex = ColorUtility.ToHtmlStringRGB(color);
            string key = $"ball3d:{hex}:{(hasCourse ? "c" : "nc")}";
            ReplaceModel(key, () => ModelGo(Cot3dModelFactory.BuildUnitBall(null, color, hasCourse ? 0f : float.NaN)));
        }

        void ApplyFallbackBall3D(NormalizedCot cot)
        {
            if (IsUnitCot(cot))
            {
                ApplyUnitBall3D(cot);
                return;
            }
            var color = _isEmergency
                ? new Color(1f, 0.18f, 0.18f, 1f)
                : ParseColor(cot.detail?.markerColor, UnitDotColor(cot));
            float course = cot.detail?.track != null ? cot.detail.track.course : float.NaN;
            bool hasCourse = !float.IsNaN(course);
            _worldOriented3D = true;
            _headingDeg = hasCourse ? course : 0f;
            string hex = ColorUtility.ToHtmlStringRGB(color);
            string key = $"ball3d:{hex}:{(hasCourse ? "c" : "nc")}";
            ReplaceModel(key, () => ModelGo(Cot3dModelFactory.BuildUnitBall(null, color, hasCourse ? 0f : float.NaN)));
        }

        void ApplyAircraft3D(NormalizedCot cot, bool rotary)
        {
            var color = UnitDotColor(cot);
            float course = cot.detail?.track != null && !float.IsNaN(cot.detail.track.course)
                ? cot.detail.track.course
                : 0f;
            _worldOriented3D = true;
            _headingDeg = course;
            string hex = ColorUtility.ToHtmlStringRGB(color);
            string key = (rotary ? "heli3d:" : "plane3d:") + hex;
            ReplaceModel(key, () => ModelGo(rotary
                ? Cot3dModelFactory.BuildRotaryHelicopter(null, color, float.NaN)
                : Cot3dModelFactory.BuildFixedWingAirplane(null, color, float.NaN)));
        }

        void ApplyVideoPod3D(NormalizedCot cot)
        {
            var color = new Color(0.22f, 0.86f, 1f, 1f);
            float az = cot.detail?.sensor != null ? cot.detail.sensor.azimuth : 0f;
            _worldOriented3D = true;
            _headingDeg = float.IsNaN(az) ? 0f : az;
            string key = "videopod3d";
            ReplaceModel(key, () => ModelGo(Cot3dModelFactory.BuildVideoPod(null, float.NaN)));
        }

        void ApplyObserver3D(NormalizedCot cot)
        {
            var color = UnitDotColor(cot);
            float az = float.NaN;
            var sensor = cot.detail?.sensor;
            if (sensor != null && (sensor.fov > 0f || sensor.range > 0f))
                az = sensor.azimuth;
            else if (cot.detail?.track != null)
                az = cot.detail.track.course;

            bool hasAz = !float.IsNaN(az);
            _worldOriented3D = hasAz;
            _headingDeg = hasAz ? az : 0f;
            string hex = ColorUtility.ToHtmlStringRGB(color);
            string key = $"observer3d:{hex}:{(hasAz ? "fov" : "nofov")}";
            ReplaceModel(key, () => ModelGo(Cot3dModelFactory.BuildObserverPawn(null, color, hasAz ? 0f : float.NaN)));
        }

        void ApplyDcfs3D()
        {
            _worldOriented3D = false;
            _headingDeg = 0f;
            var orange = new Color(1f, 0.47f, 0f, 1f);
            string key = "dcfs3d";
            ReplaceModel(key, () => ModelGo(Cot3dModelFactory.BuildExtrudedIconObject(null, GetDcfsIcon(), orange)));
        }

        bool TryApplyLocalIconset3D(NormalizedCot cot)
        {
            var tex = IconResolver.ResolveExplicitTexture(cot);
            if (tex == null) return false;
            var color = ParseColor(cot.detail?.markerColor, UnitDotColor(cot));
            string id = cot.detail?.userIcon?.iconsetpath ?? cot.type ?? tex.name;
            string key = "iconset3d:" + id + ":" + ColorUtility.ToHtmlStringRGB(color);
            _worldOriented3D = false;
            _headingDeg = 0f;
            ReplaceModel(key, () => ModelGo(Cot3dModelFactory.BuildExtrudedIconObject(null, tex, color)));
            return true;
        }

        void ApplyRemoteIcon3D(NormalizedCot cot, AppConfig config, Color color)
        {
            var url = config != null ? config.ResolveIconUrl(cot.iconUrl) : null;
            if (string.IsNullOrEmpty(url))
            {
                ApplyFallbackBall3D(cot);
                return;
            }
            string remoteKey = "remote3d:" + url;
            if (_modelKey == remoteKey && _modelInstance != null) return;

            // Show a 3D ball immediately until the remote icon texture downloads.
            string fallbackKey = "ball3d:" + ColorUtility.ToHtmlStringRGB(color) + ":nc";
            _worldOriented3D = false;
            ReplaceModel(fallbackKey, () => ModelGo(Cot3dModelFactory.BuildUnitBall(null, color, float.NaN)));
            _iconLoad = StartCoroutine(LoadIcon3D(url, color, remoteKey));
        }

        IEnumerator LoadIcon3D(string url, Color color, string remoteKey)
        {
            if (string.IsNullOrEmpty(url)) yield break;
            using var req = UnityWebRequestTexture.GetTexture(url, nonReadable: false);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) yield break;
            if (_isVideo) yield break;
            var tex = DownloadHandlerTexture.GetContent(req);
            if (tex == null) yield break;
            if (_runtimeTex != null) Destroy(_runtimeTex);
            _runtimeTex = tex;
            _worldOriented3D = false;
            ReplaceModel(remoteKey, () => ModelGo(Cot3dModelFactory.BuildExtrudedIconObject(null, tex, color)));
        }

        void UpdateEmergencyHalo()
        {
            if (!_isEmergency)
            {
                if (_emergencyHalo != null) _emergencyHalo.gameObject.SetActive(false);
                return;
            }
            if (_emergencyHalo == null && _billboardRoot != null)
            {
                var halo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                halo.name = "EmergencyHalo";
                var col = halo.GetComponent<Collider>();
                if (col != null) Destroy(col);
                halo.transform.SetParent(_billboardRoot, false);
                halo.transform.localPosition = Vector3.zero;
                halo.transform.localScale = new Vector3(1.05f, 0.03f, 1.05f);
                var r = halo.GetComponent<Renderer>();
                if (r != null)
                    r.sharedMaterial = Cot3dModelFactory.GetLitMaterial(new Color(1f, 0.12f, 0.12f, 1f), 0.7f);
                _emergencyHalo = halo.transform;
            }
            if (_emergencyHalo != null) _emergencyHalo.gameObject.SetActive(true);
        }

        static int ComputeLabelPriority(MarkerKind kind)
        {
            switch (kind)
            {
                case MarkerKind.TeamMember: return 600;
                case MarkerKind.Observer: return 500;
                case MarkerKind.Video: return 400;
                case MarkerKind.Dcfs: return 300;
                case MarkerKind.AircraftFixed:
                case MarkerKind.AircraftRotary: return 200;
                default: return 100;
            }
        }

        static bool IsUnitCot(NormalizedCot cot) =>
            !string.IsNullOrEmpty(cot.type) && cot.type.StartsWith("a-");

        public static Color GetAffiliationOrTeamColor(NormalizedCot cot) => UnitDotColor(cot);

        static Color UnitDotColor(NormalizedCot cot)
        {
            if (cot == null) return new Color(0f, 0.90f, 1f);
            if (cot.IsEmergency) return new Color(1f, 0.2f, 0.2f);
            var teamName = cot.detail?.team?.name;
            if (!string.IsNullOrEmpty(teamName) && TeamColors.TryGetValue(teamName, out var teamHex)
                && ColorUtility.TryParseHtmlString(teamHex, out var teamColor))
                return teamColor;
            if (!string.IsNullOrEmpty(cot.detail?.markerColor)
                && ColorUtility.TryParseHtmlString(cot.detail.markerColor, out var mc))
                return mc;
            // Affiliation fallback colors — same as web affiliationLineColor().
            char aff = cot.type != null && cot.type.Length >= 3 ? cot.type[2] : 'u';
            switch (aff)
            {
                case 'f': return new Color(0f, 0.90f, 1f);      // friendly cyan
                case 'h': case 'j': case 'k': return new Color(1f, 0.25f, 0.25f); // hostile red
                case 'n': return new Color(0.49f, 0.99f, 0f);   // neutral green
                default: return new Color(1f, 0.82f, 0.10f);    // unknown yellow
            }
        }

        public void FaceCamera(Transform cam)
        {
            if (cam == null || _billboardRoot == null) return;
            if (_worldOriented3D)
            {
                // World-oriented 3D model: local +Z = Map North, rotated by compass heading.
                _billboardRoot.localRotation = Quaternion.Euler(0f, _headingDeg, 0f);
            }
            else
            {
                // 3D Extruded Icon Medallion: yaw toward viewer so the 3D relief face (-Z)
                // points toward the camera while keeping upright 3D depth on the map.
                var away = _billboardRoot.position - cam.position;
                away.y = 0f;
                if (away.sqrMagnitude < 1e-6f)
                    away = _billboardRoot.position - cam.position;
                if (away.sqrMagnitude > 1e-8f)
                    _billboardRoot.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
            }
            if (_label != null)
            {
                // Upright billboard — always face camera, keep world up.
                var la = _label.transform.position - cam.position;
                if (la.sqrMagnitude > 1e-8f)
                    _label.transform.rotation = Quaternion.LookRotation(la.normalized, Vector3.up);
            }
        }

        /// <summary>
        /// Collision-avoidance offset in marker-local space (typically stacked up).
        /// Applied on top of the base callsign hover height each frame.
        /// </summary>
        public void SetLabelAvoidanceOffset(Vector3 localOffset) =>
            _labelAvoidOffset = localOffset;

        public void ClearLabelAvoidanceOffset() =>
            _labelAvoidOffset = Vector3.zero;

        /// <summary>Shrink grouped markers (~60%) when several CoTs share a spot.</summary>
        public void SetCrowded(bool crowded) =>
            _clusterScale = crowded ? ClusterScaleFactor : 1f;

        /// <summary>
        /// Terrain reference under this marker, in the marker's PARENT-local frame
        /// (same frame as transform.localPosition).
        /// </summary>
        public void SetGroundClamp(float groundLocalY, bool sampled)
        {
            _groundLocalY = groundLocalY;
            _groundSampled = sampled;
        }

        /// <summary>
        /// Live DEM re-sample so markers lift when finer tiles load near the ground
        /// (without waiting for CotLayerController's next Sync).
        /// </summary>
        public void BindTerrainSample(DemTerrainMap terrain, GeoMath.Geodetic origin,
            double lat, double lon)
        {
            _terrain = terrain;
            _origin = origin;
            _clampLat = lat;
            _clampLon = lon;
            _haveClampGeo = true;
        }

        void ResampleGroundIfNeeded()
        {
            // 1. Sample DemTerrainMap elevation if available.
            if (_haveClampGeo && _terrain != null && _terrain.TrySampleHae(_clampLat, _clampLon, out float demHae))
            {
                var groundEnu = GeoMath.GeodeticToEnu(
                    new GeoMath.Geodetic(_clampLat, _clampLon, demHae), _origin);
                float gy = GeoMath.EnuToUnity(groundEnu).y;
                if (!_groundSampled || gy > _groundLocalY + 0.02f)
                {
                    _groundLocalY = gy;
                    _groundSampled = true;
                }
            }

            // 2. Also sample downward against any 3D photogrammetry / Google 3D Tiles meshes
            // (every 12 frames per marker so CoTs ride on top of 3D buildings and bridges).
            if (!_isAirborne && transform.parent != null && ((Time.frameCount + _occlusionTickOffset) % 12 == 0))
            {
                var topWorld = transform.position + Vector3.up * 250f;
                var hits = Physics.RaycastAll(topWorld, Vector3.down, 600f, ~0, QueryTriggerInteraction.Ignore);
                float bestSurfaceWorldY = float.NegativeInfinity;
                foreach (var h in hits)
                {
                    if (h.collider == null || h.collider.isTrigger) continue;
                    if (h.transform.GetComponentInParent<CotMarkerView>() != null) continue;
                    if (h.transform.GetComponentInParent<XrChromeHud>() != null) continue;
                    if (h.point.y > bestSurfaceWorldY)
                        bestSurfaceWorldY = h.point.y;
                }
                if (!float.IsNegativeInfinity(bestSurfaceWorldY))
                {
                    var surfaceLocal = transform.parent.InverseTransformPoint(
                        new Vector3(transform.position.x, bestSurfaceWorldY, transform.position.z));
                    if (!_groundSampled || surfaceLocal.y > _groundLocalY + 0.25f)
                    {
                        _groundLocalY = surfaceLocal.y;
                        _groundSampled = true;
                    }
                }
            }
        }

        /// <summary>World-space anchor used for label crowding checks (base label pos).
        /// Glyph stays at transform.position — never offset by stacking.</summary>
        public Vector3 LabelAnchorWorld
        {
            get
            {
                float hover = _lastMarkerMeters * LabelHover3D;
                return transform.TransformPoint(new Vector3(0f, hover, 0f));
            }
        }

        /// <summary>Ground pin world position (marker glyph center) for leader lines.</summary>
        public Vector3 MarkerPinWorld => transform.position;

        /// <summary>Approximate world-space label height for stack spacing.</summary>
        public float LabelWorldHeight =>
            _label != null
                ? _label.characterSize * _label.fontSize * 0.1f
                : 8f;

        public bool HasLabel =>
            _label != null && !string.IsNullOrEmpty(_label.text);

        public void UpdateAngularScale(Transform cam)
        {
            if (cam == null || _billboardRoot == null) return;
            ResampleGroundIfNeeded();
            float dist = Vector3.Distance(cam.position, transform.position);
            float mul = Mathf.Clamp(ScaleMultiplier, 0.5f, 5f);
            float meters = Mathf.Clamp(dist * AngularSize * mul, MinMeters * mul, MaxMeters * mul);
            meters *= _clusterScale; // crowded groups render smaller (~60%)
            _lastMarkerMeters = meters;

            // Uniform 3D scale on all axes so 3D spheres, airplanes, helicopters, and
            // extruded 3D relief icons maintain true volumetric depth.
            _billboardRoot.localScale = new Vector3(meters, meters, meters);

            if (_isEmergency && _emergencyHalo != null && _emergencyHalo.gameObject.activeSelf)
            {
                float pulse = 1.0f + 0.35f * Mathf.Sin(Time.unscaledTime * 5f);
                _emergencyHalo.localScale = new Vector3(pulse, 0.03f, pulse);
            }

            // Terrain clamp — every frame, so 3D objects never clip into sloped terrain.
            if (!float.IsNegativeInfinity(_groundLocalY))
            {
                float lift = _groundSampled ? GroundLiftSampledM : GroundLiftFallbackM;
                lift += meters * 0.32f;
                float minY = _groundLocalY + lift;
                var lp = transform.localPosition;
                if (lp.y < minY)
                {
                    lp.y = minY;
                    transform.localPosition = lp;
                }
            }

            if (_hit != null)
            {
                // Uniform 3D pick cube in _billboardRoot local space (where 1 unit = meters).
                _hit.size = new Vector3(1.1f, 1.1f, 1.1f);
            }

            UpdateOcclusionXray(cam, dist);

            if (_label != null)
            {
                float hover = meters * LabelHover3D;
                _label.transform.localPosition =
                    new Vector3(0f, hover, 0f) + _labelAvoidOffset;

                float lmul = Mathf.Clamp(LabelScaleMultiplier, 0.5f, 5f);
                _label.fontSize = (int)LabelFontSize;
                float ang = LabelAngular;
                if (dist < LabelNearSoftM)
                {
                    float t = Mathf.Clamp01(dist / LabelNearSoftM);
                    t = t * t * (3f - 2f * t);
                    ang = Mathf.Lerp(LabelNearAngular, LabelAngular, t);
                }
                float minH = LabelMinCharSize * LabelFontSize * 0.1f;
                if (dist < LabelNearSoftM)
                    minH *= Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(dist / LabelNearSoftM));
                float maxH = Mathf.Max(LabelNearMaxWorldH * lmul, dist * ang * lmul);
                if (dist < LabelNearSoftM)
                    maxH = Mathf.Lerp(6f * lmul, LabelNearMaxWorldH * lmul, Mathf.Clamp01(dist / LabelNearSoftM));
                maxH = Mathf.Min(maxH, LabelAbsMaxWorldH * lmul);
                float worldH = Mathf.Clamp(dist * ang * lmul, minH * lmul, maxH);
                _label.characterSize = Mathf.Max(0.004f, worldH / (LabelFontSize * 0.1f));
                ForceFontBilinear(_label);
                UpdateLabelDecorations();
            }
            UpdateLeaderLine();
            UpdateAltitudeDropLine();
        }

        void UpdateOcclusionXray(Transform cam, float dist)
        {
            if (((Time.frameCount + _occlusionTickOffset) % 6) == 0)
            {
                _isOccludedBehindTerrain = CheckIfOccludedFromCamera(cam, dist);
            }

            bool withinReasonableDist = dist <= XrayMaxDistanceMeters;
            bool wantXray = XrayOccludedEnabled && _isOccludedBehindTerrain && withinReasonableDist;

            if (wantXray)
            {
                float distFactor = Mathf.Clamp01(dist / Mathf.Max(500f, XrayMaxDistanceMeters));
                float alpha = Mathf.Lerp(XrayBaseAlpha + 0.08f, Mathf.Max(0.16f, XrayBaseAlpha - 0.16f), distFactor);
                int qAlpha = Mathf.RoundToInt(alpha * 20f);
                if (!_xrayApplied || qAlpha != _lastXrayAlphaQuant)
                {
                    _xrayApplied = true;
                    _lastXrayAlphaQuant = qAlpha;
                    var fallbackCol = UnitDotColor(Cot);
                    for (int i = 0; i < _modelRenderers.Count; i++)
                    {
                        var r = _modelRenderers[i];
                        if (r == null) continue;
                        r.sharedMaterial = Cot3dModelFactory.GetXraySeeThroughMaterial(_modelOrigMats[i], fallbackCol, alpha);
                    }
                }
            }
            else if (_xrayApplied)
            {
                _xrayApplied = false;
                _lastXrayAlphaQuant = -1;
                for (int i = 0; i < _modelRenderers.Count; i++)
                {
                    var r = _modelRenderers[i];
                    if (r == null) continue;
                    r.sharedMaterial = _modelOrigMats[i];
                }
            }

            if (_label != null)
            {
                // Hide callsigns of occluded CoTs that are beyond the reasonable X-Ray distance.
                bool showLabel = !_isOccludedBehindTerrain || (XrayOccludedEnabled && withinReasonableDist);
                if (_label.gameObject.activeSelf != showLabel)
                    _label.gameObject.SetActive(showLabel);
                if (showLabel)
                {
                    float lblAlpha = _isOccludedBehindTerrain ? 0.65f : 0.98f;
                    var c = _isEmergency ? new Color(1f, 0.35f, 0.35f, lblAlpha) : new Color(1f, 1f, 0.95f, lblAlpha);
                    _label.color = c;
                }
            }
        }

        bool CheckIfOccludedFromCamera(Transform cam, float dist)
        {
            if (dist < 25f) return false;
            var camPos = cam.position;
            var targetPos = transform.position;
            var dir = (targetPos - camPos);
            float len = dir.magnitude;
            if (len < 1e-3f) return false;
            dir /= len;

            // 1. Check 3D physics colliders (3D buildings from Google Photorealistic 3D Tiles or terrain colliders).
            var hits = Physics.RaycastAll(camPos, dir, len * 0.93f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.isTrigger) continue;
                if (h.transform.IsChildOf(transform) || h.transform.GetComponentInParent<CotMarkerView>() != null) continue;
                if (h.transform.GetComponentInParent<XrChromeHud>() != null) continue;
                return true;
            }

            // 2. Sample DEM terrain elevation profile along the line-of-sight between viewer and CoT.
            if (_terrain != null && transform.parent != null)
            {
                var camLocal = transform.parent.InverseTransformPoint(camPos);
                var targetLocal = transform.localPosition;
                // Sample 4 intermediate steps along the ray (20%, 42%, 64%, 84%).
                float[] steps = { 0.20f, 0.42f, 0.64f, 0.84f };
                foreach (float t in steps)
                {
                    var sampleLocal = Vector3.Lerp(camLocal, targetLocal, t);
                    var sampleEnu = new GeoMath.Enu(sampleLocal.x, sampleLocal.z, sampleLocal.y);
                    var sampleGeo = GeoMath.EnuToGeodetic(sampleEnu, _origin);
                    if (_terrain.TrySampleHae(sampleGeo.Lat, sampleGeo.Lon, out float ridgeHae))
                    {
                        var ridgeEnu = GeoMath.GeodeticToEnu(
                            new GeoMath.Geodetic(sampleGeo.Lat, sampleGeo.Lon, ridgeHae), _origin);
                        float ridgeLocalY = GeoMath.EnuToUnity(ridgeEnu).y;
                        if (ridgeLocalY > sampleLocal.y + 4.5f)
                            return true;
                    }
                }
            }

            return false;
        }

        void UpdateLabelDecorations()
        {
            float h = _label.characterSize * _label.fontSize * 0.1f;

            if (_labelShadow != null)
            {
                _labelShadow.fontSize = _label.fontSize;
                _labelShadow.characterSize = _label.characterSize;
                _labelShadow.transform.localPosition = new Vector3(h * 0.05f, -h * 0.05f, h * 0.03f);
            }

            if (_labelPlate != null && _labelRend != null)
            {
                var b = _labelRend.localBounds;
                if (b.size.x > 1e-4f && b.size.y > 1e-4f)
                {
                    _labelPlate.gameObject.SetActive(true);
                    _labelPlate.localPosition =
                        new Vector3(b.center.x, b.center.y, h * 0.06f);
                    _labelPlate.localScale =
                        new Vector3(b.size.x + h * 0.5f, b.size.y + h * 0.3f, 1f);
                }
                else
                {
                    _labelPlate.gameObject.SetActive(false);
                }
            }
        }

        void UpdateLeaderLine()
        {
            if (_leader == null || _label == null) return;
            bool show = _labelAvoidOffset.sqrMagnitude > 0.5f;
            _leader.enabled = show;
            if (!show) return;
            var pin = MarkerPinWorld + Vector3.up * Mathf.Max(0.4f, _lastMarkerMeters * 0.18f);
            var labelBase = _label.transform.position;
            _leader.SetPosition(0, pin);
            _leader.SetPosition(1, labelBase);
            float w = Mathf.Clamp(_lastMarkerMeters * 0.006f, 0.06f, 0.45f);
            _leader.startWidth = w;
            _leader.endWidth = w * 0.45f;
        }

        void UpdateAltitudeDropLine()
        {
            if (!_isAirborne || float.IsNegativeInfinity(_groundLocalY) || transform.parent == null)
            {
                if (_altDropLine != null) _altDropLine.enabled = false;
                return;
            }
            float localHeightAboveGround = transform.localPosition.y - _groundLocalY;
            if (localHeightAboveGround < _lastMarkerMeters * 0.6f)
            {
                if (_altDropLine != null) _altDropLine.enabled = false;
                return;
            }
            EnsureAltitudeDropLine();
            _altDropLine.enabled = true;
            var topWorld = transform.position;
            var bottomLocal = transform.localPosition;
            bottomLocal.y = _groundLocalY + 1.5f;
            var bottomWorld = transform.parent.TransformPoint(bottomLocal);
            _altDropLine.SetPosition(0, topWorld);
            _altDropLine.SetPosition(1, bottomWorld);
            float w = Mathf.Clamp(_lastMarkerMeters * 0.012f, 0.12f, 1.2f);
            _altDropLine.startWidth = w;
            _altDropLine.endWidth = w * 0.4f;
        }

        void EnsureAltitudeDropLine()
        {
            if (_altDropLine != null) return;
            var go = new GameObject("AltitudeDropLine");
            go.transform.SetParent(transform, false);
            _altDropLine = go.AddComponent<LineRenderer>();
            _altDropLine.positionCount = 2;
            _altDropLine.useWorldSpace = true;
            _altDropLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _altDropLine.receiveShadows = false;
            _altDropLine.numCapVertices = 2;
            _altDropLine.alignment = LineAlignment.View;
            if (_altLineMat == null)
            {
                var sh = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color");
                _altLineMat = sh != null ? new Material(sh) : null;
            }
            if (_altLineMat != null) _altDropLine.sharedMaterial = _altLineMat;
            var c = UnitDotColor(Cot);
            _altDropLine.startColor = new Color(c.r, c.g, c.b, 0.65f);
            _altDropLine.endColor = new Color(c.r, c.g, c.b, 0.15f);
            _altDropLine.enabled = false;
        }

        public void SetWorldScale(float meters)
        {
            float s = Mathf.Clamp(meters, MinMeters, MaxMeters);
            if (_billboardRoot != null)
                _billboardRoot.localScale = new Vector3(s, s, s);
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            FaceCamera(cam.transform);
            UpdateAngularScale(cam.transform);
        }

        void EnsureVisuals()
        {
            if (_billboardRoot != null) return;

            var root = new GameObject("ModelRoot");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localScale = Vector3.one * 30f;
            // Non-trigger so default Physics.Raycast always hits.
            _hit = root.AddComponent<BoxCollider>();
            _hit.isTrigger = false;
            _hit.size = new Vector3(1.1f, 1.1f, 1.1f);
            _billboardRoot = root.transform;

            var labelGo = new GameObject("Callsign");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 22f, 0f);
            _label = labelGo.AddComponent<TextMesh>();
            _label.anchor = TextAnchor.LowerCenter;
            _label.alignment = TextAlignment.Center;
            _label.fontSize = (int)LabelFontSize;
            _label.characterSize = LabelMinCharSize;
            _label.color = new Color(1f, 1f, 0.95f, 0.98f);
            _label.font = XrText.SharedFont
                          ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                          ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            ForceFontBilinear(_label);
            _labelRend = labelGo.GetComponent<MeshRenderer>();
            if (_labelRend != null)
            {
                _labelRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _labelRend.receiveShadows = false;
                _labelRend.material.renderQueue = 3002;
            }

            var shadowGo = new GameObject("CallsignShadow");
            shadowGo.transform.SetParent(labelGo.transform, false);
            _labelShadow = shadowGo.AddComponent<TextMesh>();
            _labelShadow.anchor = _label.anchor;
            _labelShadow.alignment = _label.alignment;
            _labelShadow.fontSize = _label.fontSize;
            _labelShadow.characterSize = _label.characterSize;
            _labelShadow.color = new Color(0f, 0f, 0f, 0.9f);
            _labelShadow.font = _label.font;
            var sr = shadowGo.GetComponent<MeshRenderer>();
            if (sr != null)
            {
                sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sr.receiveShadows = false;
                sr.material.renderQueue = 3001;
            }

            var plateGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            plateGo.name = "CallsignPlate";
            plateGo.transform.SetParent(labelGo.transform, false);
            var plateCol = plateGo.GetComponent<Collider>();
            if (plateCol != null) Destroy(plateCol);
            _labelPlate = plateGo.transform;
            _labelPlateRend = plateGo.GetComponent<Renderer>();
            if (_labelPlateRend != null)
            {
                _labelPlateRend.sharedMaterial = new Material(GetUnlitMaterial());
                _labelPlateRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _labelPlateRend.receiveShadows = false;
                var pm = _labelPlateRend.material;
                var plateColor = new Color(0.02f, 0.03f, 0.05f, 0.62f);
                if (pm.HasProperty("_BaseColor")) pm.SetColor("_BaseColor", plateColor);
                if (pm.HasProperty("_Color")) pm.SetColor("_Color", plateColor);
                pm.renderQueue = 3000;
            }
            plateGo.SetActive(false);

            EnsureLeaderLine();
        }

        void EnsureLeaderLine()
        {
            if (_leader != null) return;
            var go = new GameObject("LabelLeader");
            go.transform.SetParent(transform, false);
            _leader = go.AddComponent<LineRenderer>();
            _leader.positionCount = 2;
            _leader.useWorldSpace = true;
            _leader.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _leader.receiveShadows = false;
            _leader.numCapVertices = 2;
            _leader.alignment = LineAlignment.View;
            if (_leaderMat == null)
            {
                var sh = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color");
                _leaderMat = sh != null ? new Material(sh) : null;
            }
            if (_leaderMat != null) _leader.sharedMaterial = _leaderMat;
            var c0 = new Color(1f, 1f, 0.92f, 0.55f);
            var c1 = new Color(1f, 1f, 0.92f, 0.2f);
            _leader.startColor = c0;
            _leader.endColor = c1;
            _leader.enabled = false;
        }

        static void ForceFontBilinear(TextMesh tm)
        {
            if (tm?.font?.material?.mainTexture == null) return;
            tm.font.material.mainTexture.filterMode = FilterMode.Bilinear;
        }

        static Material GetUnlitMaterial()
        {
            if (_sharedUnlit != null) return _sharedUnlit;
            var sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Transparent")
                     ?? Shader.Find("Unlit/Color");
            _sharedUnlit = sh != null ? new Material(sh) : null;
            if (_sharedUnlit != null && _sharedUnlit.HasProperty("_Cull"))
                _sharedUnlit.SetFloat("_Cull", 0f);
            return _sharedUnlit;
        }

        /// <summary>
        /// Orange disc + white "C" + dark outline texture extruded into 3D for dCFS markers.
        /// </summary>
        static Texture2D GetDcfsIcon()
        {
            if (_dcfsIcon != null) return _dcfsIcon;
            const int s = 128;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            var px = new Color32[s * s];
            var clear = new Color32(0, 0, 0, 0);
            var orange = new Color32(255, 119, 0, 255); // #FF7700
            var outline = new Color32(0, 0, 0, 220);
            var white = new Color32(255, 255, 255, 255);

            const float discR = 0.38f;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f;
                float v = (y + 0.5f) / s - 0.5f;
                float r = Mathf.Sqrt(u * u + v * v);
                if (r > discR + 0.05f) px[y * s + x] = clear;
                else if (r > discR) px[y * s + x] = outline;
                else px[y * s + x] = orange;
            }

            const float cOuter = 0.22f;
            const float cInner = 0.11f;
            const float openHalf = 0.55f;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f;
                float v = (y + 0.5f) / s - 0.5f;
                float r = Mathf.Sqrt(u * u + v * v);
                if (r < cInner || r > cOuter) continue;
                float ang = Mathf.Atan2(v, u);
                if (ang > -openHalf && ang < openHalf) continue;
                px[y * s + x] = white;
            }

            tex.SetPixels32(px);
            tex.Apply(true, false);
            _dcfsIcon = tex;
            return _dcfsIcon;
        }

        static Color ParseColor(string css, Color fallback)
        {
            if (string.IsNullOrEmpty(css)) return fallback;
            return ColorUtility.TryParseHtmlString(css, out var c) ? c : fallback;
        }

        void OnDestroy()
        {
            if (_runtimeTex != null) Destroy(_runtimeTex);
        }
    }
}
