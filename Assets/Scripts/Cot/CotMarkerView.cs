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
    /// Billboard CoT marker matching web XR: camera glyph for video streams,
    /// callsign label, constant angular size, pickable collider.
    /// Glyph stays ground-clamped; callsign may stack with a leader line.
    /// </summary>
    public class CotMarkerView : MonoBehaviour
    {
        const float AngularSize = 0.048f;
        const float MinMeters = 14f;
        // Cap so huge billboards don't steal controller rays meant for the toolbar.
        const float MaxMeters = 120f;

        // Callsign labels: constant ANGULAR size (worldH ≈ dist × LabelAngular) with
        // a near soft-clamp so close labels don't dominate the FOV. There is NO
        // absolute far cap — a fixed world-height cap (the old 16 m) froze label
        // growth past ~667 m and far labels collapsed to unreadable arcminutes.
        const float LabelAngular = 0.024f;       // ~1.4° of visual height at mid/far
        const float LabelNearAngular = 0.012f;   // tighter when camDist is small
        const float LabelNearSoftM = 70f;        // blend toward near angular below this
        const float LabelMinCharSize = 0.016f;   // lower floor — near clamp shrinks further
        const float LabelNearMaxWorldH = 16f;    // ceiling inside the near zone only
        const float LabelAbsMaxWorldH = 300f;    // sanity ceiling (only binds beyond 12.5 km)
        const float LabelFontSize = 256f;        // match XrText.CrispFontSize raster
        const float LabelHoverFlat = 0.42f;      // fraction of marker meters above pin
        const float LabelHoverBillboard = 0.38f;

        // Crowded markers (several CoTs at nearly the same spot) shrink so the
        // pile stays readable; labels keep stacking with leader lines.
        const float ClusterScaleFactor = 0.6f;

        // Terrain clamp: keep every glyph above the rendered surface. Flat discs
        // additionally lift by half their world size (pivot at disc center) so a
        // disc edge can't dig into an uphill slope.
        const float GroundLiftSampledM = 12f;
        const float GroundLiftFallbackM = 6f;

        /// <summary>Global marker ICON size multiplier from settings (default 1).</summary>
        public static float ScaleMultiplier { get; set; } = 1f;

        /// <summary>Global callsign LABEL size multiplier from settings (default 1).
        /// Split from ScaleMultiplier so icon and text sizes tune independently.</summary>
        public static float LabelScaleMultiplier { get; set; } = 1f;

        public string Uid { get; private set; }
        public NormalizedCot Cot { get; private set; }

        /// <summary>Priority for label-collision stacking (higher wins lower stack).</summary>
        public int LabelPriority { get; private set; }

        Transform _billboardRoot;
        Renderer _rend;
        TextMesh _label;
        MeshRenderer _labelRend;
        TextMesh _labelShadow;
        Transform _labelPlate;
        Renderer _labelPlateRend;
        LineRenderer _leader;
        /// <summary>Flat ground quad under the observer person billboard carrying
        /// the gaze-direction wedge (the person itself billboards upright).</summary>
        Transform _gazeQuad;
        Renderer _gazeRend;
        BoxCollider _hit;
        Texture2D _runtimeTex;
        Texture2D _generatedTex;
        Coroutine _iconLoad;
        bool _isVideo;
        /// <summary>Unit dots / video / dCFS lie flat on the map (ATAK-style).
        /// Other badges/icons stay billboarded.</summary>
        bool _flat;
        string _dotKey;
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
        static Texture2D _cameraIcon;
        static Texture2D _dcfsIcon;
        static readonly Dictionary<string, Texture2D> DotCache = new Dictionary<string, Texture2D>();

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
            // contract-tested at app start (CotClassifier.SelfTest). Do NOT add
            // ad-hoc icon decisions here; extend the classifier + its test table.
            var kind = CotClassifier.Classify(cot);
            LabelPriority = ComputeLabelPriority(kind);
            _isVideo = kind == MarkerKind.Video;

            if (_label != null)
            {
                _label.text = cot.Callsign ?? cot.uid;
                _label.fontSize = (int)LabelFontSize;
                XrText.Sharpen(_label);
                ForceFontBilinear(_label);
                if (_labelShadow != null)
                {
                    _labelShadow.text = _label.text;
                    _labelShadow.fontSize = _label.fontSize;
                }
            }

            if (_iconLoad != null) StopCoroutine(_iconLoad);
            // Gaze wedge only exists for observers; re-shown by ApplyObserverGlyph.
            if (kind != MarkerKind.Observer) HideGazeWedge();

            switch (kind)
            {
                case MarkerKind.Observer:
                    // VRTAK XR observer (incl. self) — team-colored standing-person
                    // silhouette, billboarded upright so it reads as a person. The
                    // gaze direction (published sensor azimuth) stays flat on the
                    // ground as a separate wedge quad under the person.
                    _flat = false;
                    ApplyObserverGlyph(cot);
                    break;

                case MarkerKind.Video:
                    // Flat on map like unit dots — upright billboards clip into DEM.
                    _dotKey = null;
                    _flat = true;
                    ApplyTexture(GetCameraIcon(), Color.white);
                    break;

                case MarkerKind.Dcfs:
                    _dotKey = "dcfs";
                    _flat = true;
                    ApplyTexture(GetDcfsIcon(), Color.white);
                    break;

                case MarkerKind.TeamMember:
                    // Colored team dot with a direction-of-travel dot+arrow.
                    _flat = true;
                    ApplyUnitDot(cot);
                    break;

                case MarkerKind.AircraftFixed:
                case MarkerKind.AircraftRotary:
                    // Top-down silhouette tinted by affiliation, nose baked toward
                    // the course bucket (web air-PNG parity).
                    _flat = true;
                    ApplyAircraftGlyph(cot, kind == MarkerKind.AircraftRotary);
                    break;

                case MarkerKind.LocalIcon:
                    // Explicit on-device iconsetpath / type2525b PNG — no LXC.
                    // (Never the affiliation-default fallback; that would swallow
                    // the Dot branch below.)
                    if (!TryApplyLocalIconset(cot))
                        ApplyFallbackDot(cot);
                    break;

                case MarkerKind.RemoteIcon:
                    _dotKey = null;
                    _flat = false;
                    var color = ParseColor(cot.detail?.markerColor, new Color(0.2f, 1f, 0.45f, 1f));
                    ApplyColor(color);
                    var url = config != null ? config.ResolveIconUrl(cot.iconUrl) : null;
                    if (!string.IsNullOrEmpty(url))
                        _iconLoad = StartCoroutine(LoadIcon(url));
                    else
                        ApplyTexture(GetDotIcon(color), Color.white);
                    break;

                default: // MarkerKind.Dot
                    ApplyFallbackDot(cot);
                    break;
            }
        }

        /// <summary>Affiliation dot for "a-*" units, marker-color dot otherwise. Flat.</summary>
        void ApplyFallbackDot(NormalizedCot cot)
        {
            _flat = true; // dots stay on the map surface
            if (IsUnitCot(cot))
            {
                ApplyUnitDot(cot);
                return;
            }
            _dotKey = null;
            ApplyTexture(GetDotIcon(ParseColor(cot.detail?.markerColor,
                new Color(0.35f, 0.85f, 1f, 1f))), Color.white);
        }

        bool TryApplyLocalIconset(NormalizedCot cot)
        {
            var tex = IconResolver.ResolveExplicitTexture(cot);
            if (tex == null) return false;
            _dotKey = "iconset:" + (cot.detail?.userIcon?.iconsetpath ?? cot.type ?? "");
            _flat = false;
            // White tint — CloudTAK PNGs are already colored; keep as-is.
            ApplyTexture(tex, Color.white);
            return true;
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

        void ApplyAircraftGlyph(NormalizedCot cot, bool rotary)
        {
            var color = UnitDotColor(cot);
            int bucket = CourseBucket(cot.detail?.track != null ? cot.detail.track.course : float.NaN);
            if (bucket < 0) bucket = 0; // no course → nose north
            string key = "air:" + ColorUtility.ToHtmlStringRGB(color) + ":" + bucket + (rotary ? ":H" : ":F");
            if (key == _dotKey) return;
            _dotKey = key;
            ApplyTexture(GetAircraftIcon(color, bucket, rotary, key), Color.white);
        }

        void ApplyUnitDot(NormalizedCot cot)
        {
            var color = UnitDotColor(cot);
            int bucket = CourseBucket(cot.detail?.track != null ? cot.detail.track.course : float.NaN);
            string key = ColorUtility.ToHtmlStringRGB(color) + ":" + bucket;
            if (key == _dotKey) return;
            _dotKey = key;
            ApplyTexture(GetUnitDotIcon(color, bucket, key), Color.white);
        }

        void ApplyObserverGlyph(NormalizedCot cot)
        {
            var color = UnitDotColor(cot);
            // Gaze direction from the published sensor azimuth (falls back to
            // track course, then no direction). Same 15° buckets as unit dots.
            float az = float.NaN;
            var sensor = cot.detail?.sensor;
            if (sensor != null && (sensor.fov > 0f || sensor.range > 0f))
                az = sensor.azimuth;
            else if (cot.detail?.track != null)
                az = cot.detail.track.course;
            int bucket = CourseBucket(az);
            string key = "person:" + ColorUtility.ToHtmlStringRGB(color) + ":" + bucket;
            if (key == _dotKey) return;
            _dotKey = key;
            ApplyTexture(GetPersonIcon(color), Color.white);
            ShowGazeWedge(color, bucket);
        }

        /// <summary>
        /// Flat gaze-direction wedge on its own ground quad beneath the upright
        /// person billboard. bucket &lt; 0 (no azimuth) hides it.
        /// </summary>
        void ShowGazeWedge(Color color, int bucket)
        {
            if (bucket < 0)
            {
                HideGazeWedge();
                return;
            }
            if (_gazeQuad == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "GazeWedge";
                go.transform.SetParent(transform, false);
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                // Same orientation as flat markers: quad front (-Z) faces the sky,
                // texture-up = map north; the bearing is baked into the texture.
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                _gazeQuad = go.transform;
                _gazeRend = go.GetComponent<Renderer>();
                if (_gazeRend != null)
                {
                    _gazeRend.sharedMaterial = new Material(GetUnlitMaterial());
                    _gazeRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    _gazeRend.receiveShadows = false;
                    _gazeRend.material.renderQueue = 2999; // just under marker glyphs
                }
            }
            _gazeQuad.gameObject.SetActive(true);
            if (_gazeRend != null)
            {
                var tex = GetGazeWedgeIcon(color, bucket);
                var mat = _gazeRend.material;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                mat.mainTexture = tex;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            }
        }

        void HideGazeWedge()
        {
            if (_gazeQuad != null) _gazeQuad.gameObject.SetActive(false);
        }

        static Color UnitDotColor(NormalizedCot cot)
        {
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

        /// <summary>15° course buckets like the web renderer; -1 = no course.</summary>
        static int CourseBucket(float course)
        {
            if (float.IsNaN(course)) return -1;
            float norm = ((course % 360f) + 360f) % 360f;
            return Mathf.RoundToInt(norm / 15f) % 24;
        }

        public void FaceCamera(Transform cam)
        {
            if (cam == null || _billboardRoot == null) return;
            if (_flat)
            {
                // Lay flat on the map: quad front (-Z) faces the sky, texture-up = map
                // north (marker is a child of the world root, so it yaws with the map).
                // The heading arrow is baked into the texture at the course bearing.
                _billboardRoot.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else
            {
                // XrUiFacing convention: +Z AWAY from camera (readable side faces viewer).
                var away = _billboardRoot.position - cam.position;
                if (away.sqrMagnitude < 1e-8f) return;
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
        /// (same frame as transform.localPosition). Enforced every frame in
        /// UpdateAngularScale: the glyph never renders below groundLocalY plus a
        /// clearance (plus half the disc size for flat markers). sampled=false
        /// means "no DEM under us yet" — a smaller base-plane floor applies.
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
            if (!_haveClampGeo || _terrain == null) return;
            // Every frame near the camera is fine — TrySampleHae is a mesh bilinear.
            if (!_terrain.TrySampleHae(_clampLat, _clampLon, out float demHae)) return;
            var groundEnu = GeoMath.GeodeticToEnu(
                new GeoMath.Geodetic(_clampLat, _clampLon, demHae), _origin);
            float gy = GeoMath.EnuToUnity(groundEnu).y;
            // Only raise the floor (detail LOD never lowers the visual surface
            // under an already-clamped marker in a way we should follow down).
            if (!_groundSampled || gy > _groundLocalY + 0.02f)
            {
                _groundLocalY = gy;
                _groundSampled = true;
            }
        }

        /// <summary>World-space anchor used for label crowding checks (base label pos).
        /// Glyph stays at transform.position — never offset by stacking.</summary>
        public Vector3 LabelAnchorWorld
        {
            get
            {
                float hover = _lastMarkerMeters * (_flat ? LabelHoverFlat : LabelHoverBillboard);
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
            _billboardRoot.localScale = new Vector3(meters, meters, 1f);
            if (_gazeQuad != null && _gazeQuad.gameObject.activeSelf)
                _gazeQuad.localScale = new Vector3(meters, meters, 1f);

            // Terrain clamp — every frame, so it also tracks marker size changes.
            // Flat discs (pivot at center) additionally lift by half their size so
            // the uphill edge can't dig into sloped terrain.
            if (!float.IsNegativeInfinity(_groundLocalY))
            {
                float lift = _groundSampled ? GroundLiftSampledM : GroundLiftFallbackM;
                if (_flat) lift += meters * 0.5f;
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
                // Generous pick volume in local billboard space (scale already applied on transform).
                float hit = Mathf.Max(1.8f, 2.4f / Mathf.Max(meters, 1f) * 40f);
                // Flat markers need pick depth along their normal (now vertical).
                _hit.size = new Vector3(hit, hit, _flat ? 3f : 0.5f);
            }
            if (_label != null)
            {
                float hover = meters * (_flat ? LabelHoverFlat : LabelHoverBillboard);
                _label.transform.localPosition =
                    new Vector3(0f, hover, 0f) + _labelAvoidOffset;

                // Keep fontSize high for sharp glyph raster; drive world size via
                // characterSize. Near-distance soft clamp shrinks labels as you approach.
                // Labels use their OWN multiplier (Text Size setting), independent
                // of the icon multiplier above.
                float lmul = Mathf.Clamp(LabelScaleMultiplier, 0.5f, 5f);
                _label.fontSize = (int)LabelFontSize;
                float ang = LabelAngular;
                if (dist < LabelNearSoftM)
                {
                    float t = Mathf.Clamp01(dist / LabelNearSoftM);
                    // Smoothstep toward full angular size once past the near zone.
                    t = t * t * (3f - 2f * t);
                    ang = Mathf.Lerp(LabelNearAngular, LabelAngular, t);
                }
                float minH = LabelMinCharSize * LabelFontSize * 0.1f;
                // Allow the floor to drop when very close so labels don't dominate FOV.
                if (dist < LabelNearSoftM)
                    minH *= Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(dist / LabelNearSoftM));
                // Constant angular size at mid/far: the ceiling GROWS with distance
                // (never below dist·ang), so far labels hold ~1.4° instead of being
                // frozen at a fixed world height. Absolute cap only for sanity.
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
        }

        /// <summary>
        /// Keep the shadow copy and background plate in sync with the label:
        /// shadow = black offset text (poor-man's outline), plate = dark quad
        /// sized to the text bounds so white text reads over any terrain.
        /// </summary>
        void UpdateLabelDecorations()
        {
            // Local line height of the TextMesh (same units as its mesh/bounds).
            float h = _label.characterSize * _label.fontSize * 0.1f;

            if (_labelShadow != null)
            {
                _labelShadow.fontSize = _label.fontSize;
                _labelShadow.characterSize = _label.characterSize;
                // Down-right offset, slightly behind the text (+Z faces away from cam).
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
            // Show connector whenever the label has been pushed off the pin.
            bool show = _labelAvoidOffset.sqrMagnitude > 0.5f;
            _leader.enabled = show;
            if (!show) return;
            var pin = MarkerPinWorld + Vector3.up * Mathf.Max(0.4f, _lastMarkerMeters * 0.02f);
            var labelBase = _label.transform.position;
            _leader.SetPosition(0, pin);
            _leader.SetPosition(1, labelBase);
            float w = Mathf.Clamp(_lastMarkerMeters * 0.006f, 0.06f, 0.45f);
            _leader.startWidth = w;
            _leader.endWidth = w * 0.45f;
        }

        public void SetWorldScale(float meters)
        {
            // Kept for callers; angular scale in LateUpdate wins when camera present.
            float s = Mathf.Clamp(meters, MinMeters, MaxMeters);
            if (_billboardRoot != null)
                _billboardRoot.localScale = new Vector3(s, s, 1f);
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

            var root = GameObject.CreatePrimitive(PrimitiveType.Quad);
            root.name = "Billboard";
            root.transform.SetParent(transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localScale = Vector3.one * 40f;
            var oldCol = root.GetComponent<Collider>();
            if (oldCol != null) Destroy(oldCol);
            // Non-trigger so default Physics.Raycast always hits.
            _hit = root.AddComponent<BoxCollider>();
            _hit.isTrigger = false;
            _billboardRoot = root.transform;
            _rend = root.GetComponent<Renderer>();
            if (_rend != null)
            {
                _rend.sharedMaterial = new Material(GetUnlitMaterial());
                _rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _rend.receiveShadows = false;
                // Always draw on top of distant terrain haze.
                _rend.material.renderQueue = 3000;
            }

            var labelGo = new GameObject("Callsign");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 22f, 0f);
            _label = labelGo.AddComponent<TextMesh>();
            _label.anchor = TextAnchor.LowerCenter;
            _label.alignment = TextAlignment.Center;
            // High-res raster via XrText.CrispFontSize; world height driven by characterSize.
            // TMP is not in Packages/manifest — TextMesh + MSAA 4x + bilinear atlas.
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
                // Render order: plate (3000) < shadow (3001) < text (3002).
                _labelRend.material.renderQueue = 3002;
            }

            // Black shadow copy offset down-right behind the text — poor-man's
            // outline so small far text keeps an edge over bright terrain.
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

            // Dark semi-transparent plate behind the callsign so white text reads
            // over any terrain. Sized to the text bounds in UpdateLabelDecorations.
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
                pm.renderQueue = 3000; // just below shadow + text
            }
            plateGo.SetActive(false); // enabled once text bounds are known

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

        void ApplyColor(Color color)
        {
            if (_rend == null) return;
            var mat = _rend.material;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        }

        void ApplyTexture(Texture2D tex, Color tint)
        {
            if (_rend == null || tex == null) return;
            var mat = _rend.material;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
        }

        IEnumerator LoadIcon(string url)
        {
            if (string.IsNullOrEmpty(url) || _rend == null) yield break;
            using var req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) yield break;
            // Video markers keep the camera glyph even if a man PNG arrives.
            if (_isVideo) yield break;
            var tex = DownloadHandlerTexture.GetContent(req);
            if (tex == null) yield break;
            if (_runtimeTex != null) Destroy(_runtimeTex);
            _runtimeTex = tex;
            ApplyTexture(tex, Color.white);
        }

        /// <summary>
        /// Top-down camera/camcorder glyph for flat map markers (readable from above).
        /// Dark disc + cyan ring + white body/lens — not a side-view billboard badge.
        /// </summary>
        static Texture2D GetCameraIcon()
        {
            if (_cameraIcon != null) return _cameraIcon;
            const int s = 192;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            var px = new Color32[s * s];
            var clear = new Color32(0, 0, 0, 0);
            var fill = new Color32(16, 22, 30, 235);
            var ring = new Color32(143, 212, 255, 255);
            var outline = new Color32(0, 0, 0, 210);
            var white = new Color32(255, 255, 255, 255);
            var live = new Color32(255, 82, 82, 255);

            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f;
                float v = (y + 0.5f) / s - 0.5f;
                float r = Mathf.Sqrt(u * u + v * v);
                if (r > 0.48f) px[y * s + x] = clear;
                else if (r > 0.42f) px[y * s + x] = outline;
                else if (r > 0.36f) px[y * s + x] = ring;
                else px[y * s + x] = fill;
            }

            // Top-down camcorder: body (centered rect) + lens disc toward +Y (north).
            FillRect(px, s, 0.28f, 0.30f, 0.72f, 0.62f, white);
            // Viewfinder bump on the side
            FillRect(px, s, 0.22f, 0.40f, 0.30f, 0.54f, white);
            // Lens circle (north / "forward")
            FillCircleC(px, s, 0.50f, 0.72f, 0.12f, outline);
            FillCircleC(px, s, 0.50f, 0.72f, 0.095f, white);
            FillCircleC(px, s, 0.50f, 0.72f, 0.045f, fill);
            // Live indicator
            FillCircleC(px, s, 0.68f, 0.36f, 0.045f, live);

            tex.SetPixels32(px);
            tex.Apply(true, false);
            _cameraIcon = tex;
            return _cameraIcon;
        }

        /// <summary>
        /// Web drawDcfsBadge parity: orange disc + white "C" + dark outline.
        /// Color #FF7700 (DCFS_MARKER_COLOR).
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

            // White "C": thick annulus with an opening on the right.
            const float cOuter = 0.22f;
            const float cInner = 0.11f;
            const float openHalf = 0.55f; // radians from +X excluded
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f;
                float v = (y + 0.5f) / s - 0.5f;
                float r = Mathf.Sqrt(u * u + v * v);
                if (r < cInner || r > cOuter) continue;
                float ang = Mathf.Atan2(v, u); // 0 = +X (right opening)
                if (ang > -openHalf && ang < openHalf) continue;
                px[y * s + x] = white;
            }

            tex.SetPixels32(px);
            tex.Apply(true, false);
            _dcfsIcon = tex;
            return _dcfsIcon;
        }

        /// <summary>
        /// ATAK-style unit marker: colored dot (white ring + dark outline) and, when a
        /// course is known, a small dot + arrow on an outer ring at the course bearing.
        /// Cached per (color, 15° bucket).
        /// </summary>
        static Texture2D GetUnitDotIcon(Color color, int bucket, string key)
        {
            if (DotCache.TryGetValue(key, out var cached) && cached != null) return cached;

            const int s = 96;
            var px = new Color32[s * s];
            var fill = (Color32)color;
            var outline = new Color32(0, 0, 0, 210);
            var ring = new Color32(255, 255, 255, 235);

            bool hasCourse = bucket >= 0;
            float dotR = hasCourse ? 0.20f : 0.30f;

            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f;
                float v = (y + 0.5f) / s - 0.5f;
                float r = Mathf.Sqrt(u * u + v * v);
                if (r <= dotR) px[y * s + x] = fill;
                else if (r <= dotR + 0.035f) px[y * s + x] = ring;
                else if (r <= dotR + 0.055f) px[y * s + x] = outline;
                else px[y * s + x] = new Color32(0, 0, 0, 0);
            }

            if (hasCourse)
            {
                // Texture space: +y up, course 0° = north = up.
                float ang = (90f - bucket * 15f) * Mathf.Deg2Rad;
                float dx = Mathf.Cos(ang), dy = Mathf.Sin(ang);

                // Heading dot on the outer ring.
                float ringR = 0.335f;
                FillCircleC(px, s, 0.5f + ringR * dx, 0.5f + ringR * dy, 0.062f, outline);
                FillCircleC(px, s, 0.5f + ringR * dx, 0.5f + ringR * dy, 0.048f, fill);

                // Arrow from ring outward to the tip.
                float tipR = 0.46f, baseR = 0.36f, halfW = 0.05f;
                float tx = 0.5f + tipR * dx, ty = 0.5f + tipR * dy;
                float bx = 0.5f + baseR * dx, by = 0.5f + baseR * dy;
                float pxp = -dy, pyp = dx;
                FillTriC(px, s, tx, ty,
                    bx + halfW * pxp, by + halfW * pyp,
                    bx - halfW * pxp, by - halfW * pyp, fill);
            }

            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.SetPixels32(px);
            tex.Apply(true, false);
            DotCache[key] = tex;
            return tex;
        }

        /// <summary>
        /// VRTAK XR observer glyph: standing-person silhouette (head, torso, arms,
        /// legs) tinted with the team color, white ring + dark outline on
        /// transparent. Billboarded UPRIGHT (unlike the flat dots) so headset
        /// operators read as people at a glance. Cached per color — gaze lives on
        /// the separate flat wedge quad (GetGazeWedgeIcon).
        /// </summary>
        static Texture2D GetPersonIcon(Color color)
        {
            string key = "personTex:" + ColorUtility.ToHtmlStringRGB(color);
            if (DotCache.TryGetValue(key, out var cached) && cached != null) return cached;

            const int s = 96;
            var mask = new bool[s * s];
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f;
                float v = (y + 0.5f) / s - 0.5f;
                mask[y * s + x] = PersonInside(u, v);
            }

            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.SetPixels32(OutlineMaskPixels(mask, s, (Color32)color));
            tex.Apply(true, false);
            DotCache[key] = tex;
            return tex;
        }

        /// <summary>Standing person, front view (+y up): head, torso, arms, legs.</summary>
        static bool PersonInside(float x, float y)
        {
            // Head
            float hx = x / 0.105f, hy = (y - 0.315f) / 0.105f;
            if (hx * hx + hy * hy <= 1f) return true;
            // Torso (shoulders through hips)
            float tx = x / 0.135f, ty = (y - 0.035f) / 0.19f;
            if (tx * tx + ty * ty <= 1f) return true;
            // Arms hanging at the sides
            if (Mathf.Abs(Mathf.Abs(x) - 0.16f) <= 0.036f && y >= -0.10f && y <= 0.12f) return true;
            // Legs
            if (Mathf.Abs(Mathf.Abs(x) - 0.062f) <= 0.044f && y >= -0.42f && y <= -0.08f) return true;
            return false;
        }

        /// <summary>
        /// Gaze-direction wedge for observers: dark-outlined team-colored arrow at
        /// the 15°-bucketed azimuth near the texture edge, on transparent. Drawn on
        /// a flat ground quad (texture-up = north) under the person billboard.
        /// Cached per (color, bucket).
        /// </summary>
        static Texture2D GetGazeWedgeIcon(Color color, int bucket)
        {
            string key = "gaze:" + ColorUtility.ToHtmlStringRGB(color) + ":" + bucket;
            if (DotCache.TryGetValue(key, out var cached) && cached != null) return cached;

            const int s = 96;
            var px = new Color32[s * s]; // starts fully transparent
            var fill = (Color32)color;
            var outline = new Color32(0, 0, 0, 210);

            // Texture space: +y up = north, azimuth 0 = up.
            float ang = (90f - bucket * 15f) * Mathf.Deg2Rad;
            float dx = Mathf.Cos(ang), dy = Mathf.Sin(ang);
            float pxp = -dy, pyp = dx;

            const float tipR = 0.46f, baseR = 0.24f, halfW = 0.09f, pad = 0.024f;
            float tx = 0.5f + tipR * dx, ty = 0.5f + tipR * dy;
            float bx = 0.5f + baseR * dx, by = 0.5f + baseR * dy;
            // Slightly larger dark triangle first, then the colored fill on top.
            FillTriC(px, s,
                0.5f + (tipR + pad) * dx, 0.5f + (tipR + pad) * dy,
                bx - pad * dx + (halfW + pad) * pxp, by - pad * dy + (halfW + pad) * pyp,
                bx - pad * dx - (halfW + pad) * pxp, by - pad * dy - (halfW + pad) * pyp, outline);
            FillTriC(px, s, tx, ty,
                bx + halfW * pxp, by + halfW * pyp,
                bx - halfW * pxp, by - halfW * pyp, fill);

            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.SetPixels32(px);
            tex.Apply(true, false);
            DotCache[key] = tex;
            return tex;
        }

        /// <summary>
        /// Top-down aircraft silhouette (fixed-wing or rotary), tinted by affiliation,
        /// white ring + dark outline for readability. Course rotation is BAKED into the
        /// texture per 15° bucket (marker quads lie flat and are never rotated by course).
        /// Cached per (color, bucket, airframe kind).
        /// </summary>
        static Texture2D GetAircraftIcon(Color color, int bucket, bool rotary, string key)
        {
            if (DotCache.TryGetValue(key, out var cached) && cached != null) return cached;

            const int s = 96;
            // Texture space: +y up = north, course 0 = up, angle = (90 - bucket*15)°.
            float ang = (90f - bucket * 15f) * Mathf.Deg2Rad;
            // Rotation mapping shape-local +y (the nose) onto the course direction.
            float rot = ang - Mathf.PI * 0.5f;
            float cosR = Mathf.Cos(rot), sinR = Mathf.Sin(rot);

            // 1) Silhouette mask, tested in shape-local space (nose = +y).
            var mask = new bool[s * s];
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f;
                float v = (y + 0.5f) / s - 0.5f;
                float lx = cosR * u + sinR * v;
                float ly = -sinR * u + cosR * v;
                mask[y * s + x] = rotary ? RotaryInside(lx, ly) : FixedWingInside(lx, ly);
            }

            // 2) Fill + white ring + dark outline via small-radius mask dilation.
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.SetPixels32(OutlineMaskPixels(mask, s, (Color32)color));
            tex.Apply(true, false);
            DotCache[key] = tex;
            return tex;
        }

        /// <summary>
        /// Silhouette mask → colored fill + white ring + dark outline pixels via
        /// small-radius dilation. Shared by the aircraft and person glyphs.
        /// </summary>
        static Color32[] OutlineMaskPixels(bool[] mask, int s, Color32 fill)
        {
            var px = new Color32[s * s];
            var ring = new Color32(255, 255, 255, 235);
            var outline = new Color32(0, 0, 0, 210);
            const int ringPx = 2; // white ring thickness (pixels from silhouette)
            const int outPx = 4;  // dark outline outer edge
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                int i = y * s + x;
                if (mask[i]) { px[i] = fill; continue; }
                int d2min = int.MaxValue;
                for (int dy = -outPx; dy <= outPx; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= s) continue;
                    for (int dx = -outPx; dx <= outPx; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= s || !mask[yy * s + xx]) continue;
                        int d2 = dx * dx + dy * dy;
                        if (d2 < d2min) d2min = d2;
                    }
                }
                if (d2min <= ringPx * ringPx) px[i] = ring;
                else if (d2min <= outPx * outPx) px[i] = outline;
                else px[i] = new Color32(0, 0, 0, 0);
            }
            return px;
        }

        /// <summary>Fixed-wing silhouette (nose +y): fuselage, swept wings, swept tail.</summary>
        static bool FixedWingInside(float x, float y)
        {
            // Fuselage
            if (Mathf.Abs(x) <= 0.05f && y >= -0.34f && y <= 0.30f) return true;
            // Nose cone
            if (InTri(x, y, -0.05f, 0.28f, 0.05f, 0.28f, 0f, 0.44f)) return true;
            // Swept wings
            if (InTri(x, y, 0.045f, 0.16f, 0.36f, -0.12f, 0.045f, -0.06f)) return true;
            if (InTri(x, y, -0.045f, 0.16f, -0.36f, -0.12f, -0.045f, -0.06f)) return true;
            // Swept tailplane
            if (InTri(x, y, 0.04f, -0.20f, 0.17f, -0.38f, 0.04f, -0.34f)) return true;
            if (InTri(x, y, -0.04f, -0.20f, -0.17f, -0.38f, -0.04f, -0.34f)) return true;
            return false;
        }

        /// <summary>Rotary silhouette (nose +y): cabin, tail boom, tail rotor, crossed blades.</summary>
        static bool RotaryInside(float x, float y)
        {
            // Cabin (rounded fuselage)
            float ex = x / 0.11f, ey = (y - 0.10f) / 0.20f;
            if (ex * ex + ey * ey <= 1f) return true;
            // Tail boom
            if (Mathf.Abs(x) <= 0.028f && y >= -0.36f && y <= -0.02f) return true;
            // Tail rotor bar
            if (Mathf.Abs(y + 0.36f) <= 0.022f && Mathf.Abs(x) <= 0.09f) return true;
            // Main rotor: two crossed blades through the hub at ±45°.
            const float d = 0.70710678f;
            return BladeInside(x, y - 0.10f, d, d) || BladeInside(x, y - 0.10f, d, -d);
        }

        static bool BladeInside(float x, float y, float dx, float dy)
        {
            float along = x * dx + y * dy;
            float across = x * dy - y * dx;
            return Mathf.Abs(across) <= 0.022f && Mathf.Abs(along) <= 0.34f;
        }

        static bool InTri(float px, float py,
            float x0, float y0, float x1, float y1, float x2, float y2)
        {
            float d1 = (px - x1) * (y0 - y1) - (x0 - x1) * (py - y1);
            float d2 = (px - x2) * (y1 - y2) - (x1 - x2) * (py - y2);
            float d3 = (px - x0) * (y2 - y0) - (x2 - x0) * (py - y0);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static void FillCircleC(Color32[] px, int s, float cx, float cy, float r, Color32 c)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt((cx - r) * s));
            int x1 = Mathf.Min(s - 1, Mathf.CeilToInt((cx + r) * s));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((cy - r) * s));
            int y1 = Mathf.Min(s - 1, Mathf.CeilToInt((cy + r) * s));
            float r2 = r * r;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float u = (x + 0.5f) / s - cx;
                float v = (y + 0.5f) / s - cy;
                if (u * u + v * v <= r2) px[y * s + x] = c;
            }
        }

        static void FillTriC(Color32[] px, int s,
            float x0, float y0, float x1, float y1, float x2, float y2, Color32 c)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(x0, Mathf.Min(x1, x2)) * s));
            int maxX = Mathf.Min(s - 1, Mathf.CeilToInt(Mathf.Max(x0, Mathf.Max(x1, x2)) * s));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(y0, Mathf.Min(y1, y2)) * s));
            int maxY = Mathf.Min(s - 1, Mathf.CeilToInt(Mathf.Max(y0, Mathf.Max(y1, y2)) * s));
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float u = (x + 0.5f) / s;
                float v = (y + 0.5f) / s;
                float d1 = (u - x1) * (y0 - y1) - (x0 - x1) * (v - y1);
                float d2 = (u - x2) * (y1 - y2) - (x1 - x2) * (v - y2);
                float d3 = (u - x0) * (y2 - y0) - (x2 - x0) * (v - y0);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0;
                bool pos = d1 > 0 || d2 > 0 || d3 > 0;
                if (!(neg && pos)) px[y * s + x] = c;
            }
        }

        static Texture2D GetDotIcon(Color color)
        {
            const int s = 48;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            var px = new Color32[s * s];
            var fill = (Color32)color;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f;
                float v = (y + 0.5f) / s - 0.5f;
                float r = Mathf.Sqrt(u * u + v * v);
                if (r > 0.42f) px[y * s + x] = new Color32(0, 0, 0, 0);
                else if (r > 0.34f) px[y * s + x] = new Color32(255, 255, 255, 220);
                else px[y * s + x] = fill;
            }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }

        static void FillRect(Color32[] px, int s, float x0, float y0, float x1, float y1, Color32 c)
        {
            int ix0 = Mathf.Clamp(Mathf.FloorToInt(x0 * s), 0, s - 1);
            int iy0 = Mathf.Clamp(Mathf.FloorToInt(y0 * s), 0, s - 1);
            int ix1 = Mathf.Clamp(Mathf.CeilToInt(x1 * s), 0, s);
            int iy1 = Mathf.Clamp(Mathf.CeilToInt(y1 * s), 0, s);
            for (int y = iy0; y < iy1; y++)
            for (int x = ix0; x < ix1; x++)
                px[y * s + x] = c;
        }

        static void FillCircle(Color32[] px, int s, float cx, float cy, float r, Color32 c)
        {
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - cx;
                float v = (y + 0.5f) / s - cy;
                if (u * u + v * v <= r * r)
                    px[y * s + x] = c;
            }
        }

        static Color ParseColor(string css, Color fallback)
        {
            if (string.IsNullOrEmpty(css)) return fallback;
            return ColorUtility.TryParseHtmlString(css, out var c) ? c : fallback;
        }

        void OnDestroy()
        {
            if (_runtimeTex != null) Destroy(_runtimeTex);
            if (_generatedTex != null) Destroy(_generatedTex);
        }
    }
}
