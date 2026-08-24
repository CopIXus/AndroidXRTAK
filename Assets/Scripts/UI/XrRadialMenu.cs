using System;
using System.Collections.Generic;
using TakXr.Cot;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK-style radial "coin" menu opened when a CoT is tapped — web-XR parity:
    /// a dark coin of wedge slices (Details / Follow / Video / R&amp;B / Delete) around
    /// a center badge showing the CoT's type icon (camcorder for video CoTs), with
    /// a callsign pill below. Billboarded at the marker and scaled with distance so
    /// it keeps a constant angular size. Details routes to the existing XrInfoPanel;
    /// Video to XrVideoPanel; Follow to XrFollowController; R&amp;B toggles a
    /// range/bearing line from the viewer to the marker; Delete removes locally
    /// drawn CoTs and publishes a t-x-d-d. Center badge click closes.
    /// </summary>
    // Runs after XrChromeHud so our SetUiBlocking(true) while aiming at the menu is
    // not stomped by the chrome's per-frame blocking re-evaluation.
    [DefaultExecutionOrder(60)]
    public class XrRadialMenu : MonoBehaviour
    {
        // Whole-menu angular diameter (radians) — constant apparent size at any range.
        const float AngularSize = 0.13f;
        const float MenuUnitDiameter = 1.40f; // coin diameter at unit scale
        // Wedge annulus, relative to the coin half-size (see WedgeTexture).
        const float WedgeInnerFrac = 0.37f;
        const float WedgeOuterFrac = 0.985f;
        const float CenterDiameter = 0.44f;
        const float AutoCloseSec = 12f;
        const float OpenGraceSec = 0.45f;
        // XrDrawTool.BaseCot uid prefix — only locally drawn CoTs are deletable.
        const string LocalUidPrefix = "takxr.";

        Transform _cam;
        CotFeedClient _feed;
        CotLayerController _cotLayer;
        XrInfoPanel _info;
        XrVideoPanel _video;
        XrFollowController _follow;
        TakDirectHub _direct;
        XrWorldRoot _world;
        Action<string> _flash;

        Transform _root;
        string _uid;
        NormalizedCot _cot;
        Vector3 _lastMarkerWorld;
        float _openedAt;
        float _nextClickTime;
        readonly List<Btn> _buttons = new List<Btn>();
        readonly bool[] _wasGrabbing = new bool[2];
        readonly bool[] _blockedByMe = new bool[2];

        // Range & bearing overlay (independent of menu visibility).
        string _rbUid;
        Transform _rbRoot;       // under world root — line follows map transforms
        LineRenderer _rbLine;
        Transform _rbLabelRoot;  // world space — billboarded, distance-sized
        TextMesh _rbLabel;
        Vector3 _rbMarkerLocal;
        bool _rbHasMarkerLocal;

        struct Btn
        {
            public Transform Root;
            public Action OnClick;
        }

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;
        public string CurrentUid => IsVisible ? _uid : null;

        public static XrRadialMenu Create()
        {
            var go = new GameObject("XrRadialMenu");
            return go.AddComponent<XrRadialMenu>();
        }

        public void Configure(
            Transform cam,
            CotFeedClient feed,
            CotLayerController cotLayer,
            XrInfoPanel info,
            XrVideoPanel video,
            XrFollowController follow,
            TakDirectHub direct,
            XrWorldRoot world,
            Action<string> flash = null)
        {
            _cam = cam;
            _feed = feed;
            _cotLayer = cotLayer;
            _info = info;
            _video = video;
            _follow = follow;
            _direct = direct;
            _world = world;
            _flash = flash;
        }

        // ---------------- open / close ----------------

        public void Open(NormalizedCot cot)
        {
            if (cot == null || string.IsNullOrEmpty(cot.uid)) return;
            _cot = cot;
            _uid = cot.uid;
            _openedAt = Time.unscaledTime;

            if (_cotLayer != null && _cotLayer.TryGetMarkerWorldPos(_uid, out var world))
                _lastMarkerWorld = world;
            else if (_root == null || !_root.gameObject.activeSelf)
                _lastMarkerWorld = _cam != null
                    ? _cam.position + _cam.forward * 3f
                    : Vector3.zero;

            BuildSlices();
            _root.gameObject.SetActive(true);
            PlaceAndScale();
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            _cot = null;
            _uid = null;
            for (int h = 0; h < 2; h++)
            {
                if (_blockedByMe[h]) XrHandPinchInput.SetUiBlocking(h, false);
                _blockedByMe[h] = false;
            }
        }

        void BuildSlices()
        {
            if (_root == null)
            {
                _root = new GameObject("Root").transform;
                _root.SetParent(transform, false);
            }
            foreach (var b in _buttons)
                if (b.Root != null) Destroy(b.Root.gameObject);
            _buttons.Clear();

            var uid = _uid;
            var cot = _cot;
            bool following = _follow != null && _follow.FollowUid == uid;
            bool hasVideo =
                !string.IsNullOrEmpty(cot.detail?.video?.url) ||
                (cot.type != null && cot.type.StartsWith("b-m-p-s-p-loc", StringComparison.Ordinal));
            bool deletable = uid.StartsWith(LocalUidPrefix, StringComparison.Ordinal);
            bool rbActive = _rbUid == uid && _rbLine != null;

            // Web-reference coin: dark wedge slices with white icons around a center
            // badge showing the CoT's type icon. Video wedge green / Follow wedge
            // blue (blue also marks the active follow), rest dark — like the web app.
            var dark = new Color(0.13f, 0.16f, 0.21f, 0.88f);
            var blue = new Color(0.20f, 0.42f, 0.78f, 0.92f);
            var green = new Color(0.16f, 0.45f, 0.26f, 0.92f);
            var red = new Color(0.42f, 0.16f, 0.15f, 0.92f);

            var slices = new List<(string icon, string label, Color tint, Action act)>
            {
                ("details", "Details", dark, OnDetails),
                ("follow", following ? "Unfollow" : "Follow", blue, OnFollow),
                ("locate", "Go To", dark, OnGoTo),
            };
            if (hasVideo) slices.Add(("video", "Video", green, OnVideo));
            slices.Add(("rb", rbActive ? "R&B off" : "R&B", dark, OnRangeBearing));
            if (deletable) slices.Add(("delete", "Delete", red, OnDelete));

            float stepDeg = 360f / slices.Count;
            for (int i = 0; i < slices.Count; i++)
            {
                // Half-step offset puts the gaps at 12 o'clock like the web coin.
                float centerDeg = 90f - (i + 0.5f) * stepDeg;
                AddWedgeButton(slices[i].icon, slices[i].label, centerDeg, stepDeg,
                    slices[i].tint, slices[i].act);
            }

            // Center badge: CoT type icon (camcorder for video CoTs); click closes.
            var center = AddCenterBadge(hasVideo);

            // Callsign pill below the coin (web reference style); parented to the
            // center button so it is destroyed with the slices on the next Open.
            float pillY = -(MenuUnitDiameter * 0.5f + 0.17f);
            Quad("TitlePill", center, new Vector3(0f, pillY, -0.01f),
                new Vector2(0.95f, 0.17f), new Color(0.04f, 0.05f, 0.07f, 0.92f), 3005);
            MakeText("Title", center, new Vector3(0f, pillY, -0.02f),
                cot.Callsign ?? uid, 0.014f, new Color(0.95f, 0.98f, 1f, 0.98f));
        }

        void AddWedgeButton(string icon, string label, float centerDeg, float spanDeg,
            Color tint, Action onClick)
        {
            float a = centerDeg * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            float coinR = MenuUnitDiameter * 0.5f;
            float centroidR = coinR * (WedgeInnerFrac + WedgeOuterFrac) * 0.5f;

            var root = new GameObject("Btn_" + icon).transform;
            root.SetParent(_root, false);
            root.localPosition = new Vector3(dir.x * centroidR, dir.y * centroidR, 0f);

            // Wedge plate: full-coin quad centered on the coin (so we offset by
            // -centroid), z-rotated so the up-pointing wedge texture aims at the
            // slice angle. Parenting it here keeps the per-Open cleanup simple.
            var wedge = GameObject.CreatePrimitive(PrimitiveType.Quad);
            wedge.name = "Wedge";
            wedge.transform.SetParent(root, false);
            wedge.transform.localPosition = new Vector3(-dir.x * centroidR, -dir.y * centroidR, 0.01f);
            wedge.transform.localRotation = Quaternion.Euler(0f, 0f, centerDeg - 90f);
            wedge.transform.localScale = new Vector3(MenuUnitDiameter, MenuUnitDiameter, 1f);
            Destroy(wedge.GetComponent<Collider>());
            var wr = wedge.GetComponent<Renderer>();
            var wm = new Material(Shader.Find("Sprites/Default")
                                  ?? Shader.Find("Universal Render Pipeline/Unlit")
                                  ?? Shader.Find("Unlit/Transparent"));
            var wedgeTex = WedgeTexture(spanDeg);
            wm.mainTexture = wedgeTex;
            if (wm.HasProperty("_BaseMap")) wm.SetTexture("_BaseMap", wedgeTex);
            if (wm.HasProperty("_Color")) wm.SetColor("_Color", tint);
            if (wm.HasProperty("_BaseColor")) wm.SetColor("_BaseColor", tint);
            wm.renderQueue = 3000;
            wr.sharedMaterial = wm;
            wr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            wr.receiveShadows = false;

            MakeIconQuad(root, icon, 0.21f, new Vector3(0f, 0.035f, -0.02f));
            if (label != null)
                MakeText("Lbl", root, new Vector3(0f, -0.105f, -0.02f),
                    label, 0.009f, new Color(0.93f, 0.97f, 1f, 0.97f));

            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.40f, 0.36f, 0.08f);
            col.isTrigger = true;

            _buttons.Add(new Btn { Root = root, OnClick = onClick });
        }

        /// <summary>Center badge (dark disc + CoT type icon, red live-dot for video).
        /// Clicking it closes the menu. Returns its root transform.</summary>
        Transform AddCenterBadge(bool video)
        {
            var root = new GameObject("Btn_center").transform;
            root.SetParent(_root, false);
            root.localPosition = Vector3.zero;

            var plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
            plate.name = "Plate";
            plate.transform.SetParent(root, false);
            plate.transform.localScale = new Vector3(CenterDiameter, CenterDiameter, 1f);
            Destroy(plate.GetComponent<Collider>());
            var pr = plate.GetComponent<Renderer>();
            var pm = new Material(Shader.Find("Sprites/Default")
                                  ?? Shader.Find("Universal Render Pipeline/Unlit")
                                  ?? Shader.Find("Unlit/Transparent"));
            var disc = DiscTexture();
            pm.mainTexture = disc;
            if (pm.HasProperty("_BaseMap")) pm.SetTexture("_BaseMap", disc);
            if (pm.HasProperty("_Color")) pm.SetColor("_Color", Color.white);
            if (pm.HasProperty("_BaseColor")) pm.SetColor("_BaseColor", Color.white);
            pm.renderQueue = 3003;
            pr.sharedMaterial = pm;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pr.receiveShadows = false;

            MakeIconQuad(root, video ? "video" : "point", CenterDiameter * 0.58f,
                new Vector3(0f, 0f, -0.02f));
            if (video)
            {
                // Live red dot on the badge corner, like the web camcorder badge.
                var dot = GameObject.CreatePrimitive(PrimitiveType.Quad);
                dot.name = "LiveDot";
                dot.transform.SetParent(root, false);
                dot.transform.localPosition = new Vector3(
                    CenterDiameter * 0.30f, CenterDiameter * 0.30f, -0.025f);
                dot.transform.localScale = Vector3.one * (CenterDiameter * 0.22f);
                Destroy(dot.GetComponent<Collider>());
                var dr = dot.GetComponent<Renderer>();
                var dm = new Material(Shader.Find("Sprites/Default")
                                      ?? Shader.Find("Universal Render Pipeline/Unlit")
                                      ?? Shader.Find("Unlit/Transparent"));
                var dotTex = SoftDotTexture();
                dm.mainTexture = dotTex;
                if (dm.HasProperty("_BaseMap")) dm.SetTexture("_BaseMap", dotTex);
                if (dm.HasProperty("_Color")) dm.SetColor("_Color", new Color(1f, 0.30f, 0.26f, 1f));
                if (dm.HasProperty("_BaseColor")) dm.SetColor("_BaseColor", new Color(1f, 0.30f, 0.26f, 1f));
                dm.renderQueue = 3010;
                dr.sharedMaterial = dm;
                dr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                dr.receiveShadows = false;
            }

            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(CenterDiameter * 1.05f, CenterDiameter * 1.05f, 0.08f);
            col.isTrigger = true;

            _buttons.Add(new Btn { Root = root, OnClick = Hide });
            return root;
        }

        // ---------------- slice actions ----------------

        void OnDetails()
        {
            var cot = _cot;
            bool following = _follow != null && _follow.FollowUid == _uid;
            Hide();
            _info?.Show(cot, following, _cam);
        }

        void OnVideo()
        {
            var cot = _cot;
            Hide();
            _video?.Show(cot, _cam);
        }

        void OnFollow()
        {
            var uid = _uid;
            var name = _cot?.Callsign ?? uid;
            bool turnOn = _follow != null && _follow.FollowUid != uid;
            Hide();
            _follow?.SetFollow(turnOn ? uid : null);
            _flash?.Invoke(turnOn ? $"Following {name}" : "Follow stopped");
        }

        void OnGoTo()
        {
            var cot = _cot;
            var uid = _uid;
            Hide();
            if (_cotLayer != null && _cotLayer.TryGetMarkerWorldPos(uid, out var pos) && _world != null)
            {
                // Nudge world so marker sits ahead of camera (same idea as FrameWorldPoint).
                if (_cam != null)
                {
                    var camPos = _cam.position;
                    var flat = _cam.forward; flat.y = 0f;
                    if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
                    flat.Normalize();
                    var desired = camPos + flat * 160f + Vector3.down * 50f;
                    _world.Root.position += desired - pos;
                    _flash?.Invoke("View from " + (cot?.Callsign ?? uid));
                    return;
                }
            }
            _flash?.Invoke("Go To unavailable");
        }

        void OnRangeBearing()
        {
            var uid = _uid;
            var cot = _cot;
            Hide();
            if (_rbUid == uid && _rbLine != null)
            {
                ClearRangeBearing();
                _flash?.Invoke("R&B cleared");
                return;
            }
            StartRangeBearing(uid, cot);
        }

        void OnDelete()
        {
            var uid = _uid;
            var cot = _cot;
            Hide();
            if (string.IsNullOrEmpty(uid)) return;

            if (_rbUid == uid) ClearRangeBearing();
            if (_follow != null && _follow.FollowUid == uid) _follow.SetFollow(null);
            if (_info != null && _info.IsVisible && _info.CurrentUid == uid) _info.Hide();

            bool sent = _direct != null && _direct.SendCot(BuildDeleteXml(uid, cot?.type));
            _feed?.RemoveByUid(uid);
            _flash?.Invoke($"Deleted {cot?.Callsign ?? uid}" +
                           (sent ? "\nsent t-x-d-d to TAK server" : "\nLOCAL ONLY — TAK stream down"));
            Debug.Log($"[XrRadialMenu] delete {uid} sent={sent}");
        }

        /// <summary>Minimal TAK delete event: t-x-d-d + link + __forcedelete.</summary>
        static string BuildDeleteXml(string uid, string cotType)
        {
            var now = DateTime.UtcNow;
            string time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string stale = now.AddSeconds(20).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string eu = XmlEsc(uid);
            string et = XmlEsc(string.IsNullOrEmpty(cotType) ? "a-u-G" : cotType);
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                $"<event version=\"2.0\" uid=\"{eu}-del\" type=\"t-x-d-d\" how=\"m-g\" " +
                $"time=\"{time}\" start=\"{time}\" stale=\"{stale}\">" +
                "<point lat=\"0\" lon=\"0\" hae=\"0\" ce=\"9999999\" le=\"9999999\"/>" +
                $"<detail><link uid=\"{eu}\" relation=\"p-p\" type=\"{et}\"/>" +
                "<__forcedelete/></detail></event>";
        }

        static string XmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        // ---------------- range & bearing ----------------

        void StartRangeBearing(string uid, NormalizedCot cot)
        {
            ClearRangeBearing();
            if (_world == null || _cam == null || string.IsNullOrEmpty(uid)) return;
            _rbUid = uid;
            _rbHasMarkerLocal = false;

            _rbRoot = new GameObject("RbLine").transform;
            _rbRoot.SetParent(_world.Root, false);
            var lineGo = new GameObject("Line");
            lineGo.transform.SetParent(_rbRoot, false);
            _rbLine = lineGo.AddComponent<LineRenderer>();
            _rbLine.useWorldSpace = false;
            _rbLine.positionCount = 2;
            _rbLine.startWidth = 2.5f;
            _rbLine.endWidth = 2.5f;
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            _rbLine.sharedMaterial = new Material(sh);
            var c = new Color(1f, 0.62f, 0.12f, 0.95f);
            if (_rbLine.sharedMaterial.HasProperty("_Color"))
                _rbLine.sharedMaterial.SetColor("_Color", c);
            _rbLine.startColor = c;
            _rbLine.endColor = c;

            _rbLabelRoot = new GameObject("RbLabel").transform;
            _rbLabelRoot.SetParent(transform, false);
            _rbLabel = MakeText("Text", _rbLabelRoot, Vector3.zero, "",
                0.02f, new Color(1f, 0.8f, 0.4f, 0.98f));

            UpdateRangeBearing();
            _flash?.Invoke($"R&B → {cot?.Callsign ?? uid}");
        }

        public void ClearRangeBearing()
        {
            if (_rbRoot != null) Destroy(_rbRoot.gameObject);
            if (_rbLabelRoot != null) Destroy(_rbLabelRoot.gameObject);
            _rbRoot = null;
            _rbLine = null;
            _rbLabelRoot = null;
            _rbLabel = null;
            _rbUid = null;
            _rbHasMarkerLocal = false;
        }

        void UpdateRangeBearing()
        {
            if (_rbLine == null || _world == null || _cam == null) return;
            if (_feed != null && !_feed.Cots.ContainsKey(_rbUid))
            {
                ClearRangeBearing();
                return;
            }

            var root = _world.Root;
            if (_cotLayer != null && _cotLayer.TryGetMarkerWorldPos(_rbUid, out var markerWorld))
            {
                _rbMarkerLocal = root.InverseTransformPoint(markerWorld);
                _rbHasMarkerLocal = true;
            }
            if (!_rbHasMarkerLocal) return;

            // Viewer's ground position: viewer lat/lon at the marker's ground height.
            var viewerLocal = root.InverseTransformPoint(_cam.position);
            var a = new Vector3(viewerLocal.x, _rbMarkerLocal.y, viewerLocal.z);
            var b = _rbMarkerLocal;
            const float lift = 3f; // meters above ground so the line isn't buried
            _rbLine.SetPosition(0, a + Vector3.up * lift);
            _rbLine.SetPosition(1, b + Vector3.up * lift);

            // World-root local frame is ENU meters: +Z = north, +X = east.
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            float distM = Mathf.Sqrt(dx * dx + dz * dz);
            float bearing = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            if (bearing < 0f) bearing += 360f;
            string distStr = distM >= 1000f ? $"{distM / 1000f:0.00} km" : $"{Mathf.RoundToInt(distM)} m";
            if (_rbLabel != null) _rbLabel.text = $"{distStr}  ·  {bearing:000}°";

            if (_rbLabelRoot != null)
            {
                var mid = root.TransformPoint((a + b) * 0.5f + Vector3.up * (lift + 6f));
                _rbLabelRoot.position = mid;
                XrUiFacing.FaceUser(_rbLabelRoot, _cam);
                float d = Vector3.Distance(mid, _cam.position);
                _rbLabelRoot.localScale = Vector3.one * Mathf.Max(d / 2.35f, 0.4f);
            }
        }

        // ---------------- per-frame ----------------

        void LateUpdate()
        {
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
            UpdateRangeBearing();

            if (!IsVisible) return;

            if (Time.unscaledTime > _openedAt + AutoCloseSec)
            {
                Hide();
                return;
            }
            // Selected CoT vanished from the feed — nothing to act on anymore.
            if (_feed != null && _uid != null && !_feed.Cots.ContainsKey(_uid))
            {
                Hide();
                return;
            }

            PlaceAndScale();
            PollUiSelect();
        }

        void PlaceAndScale()
        {
            if (_root == null || _cam == null) return;
            if (_cotLayer != null && _cotLayer.TryGetMarkerWorldPos(_uid, out var world))
                _lastMarkerWorld = world;

            var camPos = _cam.position;
            var toCam = camPos - _lastMarkerWorld;
            float markerDist = toCam.magnitude;
            if (markerDist < 1e-4f) return;
            // Pull slightly toward the viewer so the coin floats in front of the marker.
            var pos = _lastMarkerWorld + toCam.normalized * (markerDist * 0.08f);
            _root.position = pos;
            _root.rotation = XrUiFacing.RotationFacingUser(pos, camPos);
            float dist = Vector3.Distance(pos, camPos);
            float scale = Mathf.Max(dist * AngularSize / MenuUnitDiameter, 0.08f);
            _root.localScale = Vector3.one * scale;
        }

        // ---------------- input (XrChromeHud.PollUiSelect pattern) ----------------

        void PollUiSelect()
        {
            bool inGrace = Time.unscaledTime - _openedAt < OpenGraceSec;

            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;

                bool pointingAtUi = false;
                if (aimOk && TryHitButton(new Ray(origin, fwd), out var btn))
                {
                    pointingAtUi = true;
                    if (rising && !inGrace && Time.unscaledTime >= _nextClickTime)
                    {
                        _nextClickTime = Time.unscaledTime + 0.35f;
                        btn.OnClick?.Invoke();
                    }
                }

                // Only raise/lower the shared blocking flag for our own pointing so we
                // don't stomp XrChromeHud's blocking (we run after it, see class attr).
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
            }
        }

        bool TryHitButton(Ray ray, out Btn btn)
        {
            btn = default;
            // Menu can sit kilometers away — colliders scale with the coin.
            var hits = Physics.RaycastAll(ray, 9000f, ~0, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            foreach (var hit in hits)
            {
                foreach (var b in _buttons)
                {
                    if (b.Root == null) continue;
                    if (hit.transform == b.Root || hit.transform.IsChildOf(b.Root))
                    {
                        btn = b;
                        return true;
                    }
                }
                // Other hits (CoT markers, terrain) don't occlude — the coin floats
                // just in front of its marker, so keep scanning the sorted hits.
            }
            return false;
        }

        /// <summary>
        /// XrCopController entry point: consume selects aimed at the menu (and click
        /// the button under the ray). Shares the click debounce with the hand poll,
        /// so gaze-fallback taps work without double-firing.
        /// </summary>
        public bool HandleRaySelect(Ray ray)
        {
            if (!IsVisible) return false;
            if (!TryHitButton(ray, out var btn))
            {
                // Ray over the coin but not a button (e.g. ring gap) still consumes
                // when it crosses any of our colliders' parent root.
                var hits = Physics.RaycastAll(ray, 9000f, ~0, QueryTriggerInteraction.Collide);
                foreach (var hit in hits)
                    if (_root != null && (hit.transform == _root || hit.transform.IsChildOf(_root)))
                        return true;
                return false;
            }
            if (Time.unscaledTime - _openedAt >= OpenGraceSec &&
                Time.unscaledTime >= _nextClickTime)
            {
                _nextClickTime = Time.unscaledTime + 0.35f;
                btn.OnClick?.Invoke();
            }
            return true;
        }

        // ---------------- primitives ----------------

        static readonly Dictionary<int, Texture2D> WedgeCache = new Dictionary<int, Texture2D>();
        static Texture2D _softDotTex;

        /// <summary>
        /// One annular wedge pointing UP (centered on 90°), white fill with a light
        /// rim, tinted per slice via material color. Quad scale = coin diameter;
        /// rotate the quad to aim the wedge. Cached per angular span. Mips +
        /// trilinear so the edges stay clean at any magnification.
        /// </summary>
        static Texture2D WedgeTexture(float spanDeg)
        {
            int key = Mathf.RoundToInt(spanDeg);
            if (WedgeCache.TryGetValue(key, out var cached) && cached != null) return cached;

            const int S = 256;
            const float gapDeg = 7f;
            float halfSpan = Mathf.Max(spanDeg - gapDeg, 8f) * 0.5f * Mathf.Deg2Rad;
            var px = new Color32[S * S];
            var fill = new Color32(255, 255, 255, 255);
            var rim = new Color32(255, 255, 255, 140);
            float halfPx = S * 0.5f;
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f - halfPx) / halfPx;
                float v = (y + 0.5f - halfPx) / halfPx;
                float r = Mathf.Sqrt(u * u + v * v);
                // Angular deviation from straight up.
                float dev = Mathf.Abs(Mathf.Atan2(u, v));
                Color32 c = new Color32(0, 0, 0, 0);
                if (r >= WedgeInnerFrac && r <= WedgeOuterFrac && dev <= halfSpan)
                {
                    bool edge = r <= WedgeInnerFrac + 0.02f || r >= WedgeOuterFrac - 0.02f
                                || dev >= halfSpan - 0.02f;
                    c = edge ? rim : fill;
                }
                px[y * S + x] = c;
            }
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.SetPixels32(px);
            tex.Apply(true, false);
            WedgeCache[key] = tex;
            return tex;
        }

        /// <summary>Soft-edged white dot (tinted red for the live-video badge).</summary>
        static Texture2D SoftDotTexture()
        {
            if (_softDotTex != null) return _softDotTex;
            const int S = 64;
            var px = new Color32[S * S];
            float half = S * 0.5f;
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f - half) / half;
                float v = (y + 0.5f - half) / half;
                float r = Mathf.Sqrt(u * u + v * v);
                byte a = (byte)(255f * Mathf.Clamp01((0.92f - r) / 0.12f));
                px[y * S + x] = new Color32(255, 255, 255, a);
            }
            _softDotTex = new Texture2D(S, S, TextureFormat.RGBA32, true);
            _softDotTex.filterMode = FilterMode.Trilinear;
            _softDotTex.SetPixels32(px);
            _softDotTex.Apply(true, false);
            return _softDotTex;
        }

        static void Quad(string name, Transform parent, Vector3 localPos, Vector2 size,
            Color color, int renderQueue)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Sprites/Default")
                                   ?? Shader.Find("Universal Render Pipeline/Unlit")
                                   ?? Shader.Find("Unlit/Color"));
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.renderQueue = renderQueue;
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static Texture2D _discTex;

        /// <summary>Dark disc with a subtle blue rim — ATAK radial slice plate.</summary>
        static Texture2D DiscTexture()
        {
            if (_discTex != null) return _discTex;
            const int S = 128;
            var px = new Color32[S * S];
            var fill = new Color32(10, 14, 20, 226);
            var rim = new Color32(90, 140, 190, 255);
            float half = S * 0.5f;
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f - half) / half;
                float v = (y + 0.5f - half) / half;
                float d = Mathf.Sqrt(u * u + v * v);
                Color32 c;
                if (d > 0.99f) c = new Color32(0, 0, 0, 0);
                else if (d > 0.90f) c = rim;
                else c = fill;
                px[y * S + x] = c;
            }
            _discTex = new Texture2D(S, S, TextureFormat.RGBA32, true);
            _discTex.filterMode = FilterMode.Trilinear;
            _discTex.anisoLevel = 4;
            _discTex.SetPixels32(px);
            _discTex.Apply(true, false);
            return _discTex;
        }

        static void MakeIconQuad(Transform parent, string iconName, float size, Vector3 localPos)
        {
            var iconGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            iconGo.name = "Icon";
            iconGo.transform.SetParent(parent, false);
            iconGo.transform.localPosition = localPos;
            iconGo.transform.localScale = new Vector3(size, size, 1f);
            Destroy(iconGo.GetComponent<Collider>());
            var r = iconGo.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Sprites/Default")
                                   ?? Shader.Find("Universal Render Pipeline/Unlit")
                                   ?? Shader.Find("Unlit/Transparent"));
            var tex = AtakToolbarIcons.Get(iconName);
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            // Icons always draw AFTER plates (deterministic transparent layering).
            mat.renderQueue = 3010;
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static TextMesh MakeText(string name, Transform parent, Vector3 localPos, string msg,
            float charSize, Color color)
        {
            // Crisp raster, identical world size to the legacy fontSize-64 sites.
            return XrText.Make(name, parent, localPos, msg, charSize, color);
        }
    }
}
